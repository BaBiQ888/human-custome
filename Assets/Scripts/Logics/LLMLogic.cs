using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.Commands.UnifiedService; // Added for unified service
using LKZ.DependencyInject;
using LKZ.GPT;
using LKZ.Models;
using LKZ.TypeEventSystem;
using LKZ.UnifiedService; // Added for unified service
using LKZ.VoiceSynthesis;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LKZ.Logics
{

    public sealed class LLMLogic
    {

        private sealed class ResultData : IEnumerator
        {
            public string result;
            public IEnumerator clip;

            public object Current => current;// clip.Current;

            object current = null;

            public bool MoveNext()
            {
                if (!(clip.Current is AudioClip))
                {
                    current = clip.Current;
                    return true;
                }
                else if (clip.Current is string str)
                    return str != VoiceTTS.ErrorMess;
                else
                    return false;
            }

            public void Reset()
            {

            }
        }

        /// <summary>
        /// 统一服务音频段数据结构
        /// </summary>
        private sealed class UnifiedAudioSegment // Added
        {
            public string text;
            public Queue<byte[]> audioChunks;
            public AudioClip generatedClip;
            public bool isComplete;
            public float estimatedDuration;
            public string originalText; // Added for storing the original text
            public bool isPlaying; // Added for tracking playback status

            public UnifiedAudioSegment(string text)
            {
                this.text = text;
                this.audioChunks = new Queue<byte[]>();
                this.isComplete = false;
                this.estimatedDuration = 0f;
                this.isPlaying = false; // Initialize
            }
        }

        [Inject]
        private AudioModel audioModel { get; set; }

        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        [Inject]
        private UnifiedVoiceServiceManager unifiedServiceManager { get; set; } // Added

        /// <summary>
        /// GPT播放语音片段
        /// </summary>
        Queue<ResultData> gptVoice = new Queue<ResultData>();

        /// <summary>
        /// 统一服务音频播放队列
        /// </summary>
        Queue<UnifiedAudioSegment> unifiedAudioQueue = new Queue<UnifiedAudioSegment>(); // Added

        Action<string> _showUITextAction;

        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        public bool UseUnifiedService { get; set; } = true; // Added

        /// <summary>
        /// 当前LLM会话的文本累积
        /// </summary>
        private string currentLLMAccumulatedText = ""; // Added

        /// <summary>
        /// 已显示文本集合，用于精确去重
        /// </summary>
        private HashSet<string> displayedTexts = new HashSet<string>(); // 🔧 新增字段定义

        /// <summary>
        /// 统一服务模式下的字幕同步协程
        /// </summary>
        private Coroutine _unifiedAudioSyncCoroutine; // Added

        private bool isTalkingAnimationStarted = false;

        private string onceResult;

        /// <summary>
        /// 是否接收完成GPT的内容
        /// </summary>
        private bool isRequestChatGPTContent, isStopCreate;

        /// <summary>
        /// 字幕同步携程
        /// </summary>
        private Coroutine _titleSynchronization_Cor;

        /// <summary>
        /// 字幕同步携程
        /// </summary>
        private Coroutine _requestGPTSegmentationCor;

        /// <summary>
        /// 当前对话的首次LLM回复是否已处理
        /// </summary>
        private bool hasProcessedFirstLLMReply = false;

        public void Initialized()
        {
            Debug.Log($"🎯 LLMLogic初始化 - 模式: {(UseUnifiedService ? "统一服务" : "传统")}");
            
            // 🔧 初始化监控系统
            InitializeMonitoring();
            
            // 注册原有命令监听
            RegisterCommand.Register<VoiceRecognitionResultCommand>(VoiceRecognitionResultCommandCallback);
            RegisterCommand.Register<StopGenerateCommand>(StopGenerateCommandCallback);
            
            // 注册统一服务事件监听
            if (UseUnifiedService)
            {
                RegisterCommand.Register<UnifiedLLMResultCommand>(OnUnifiedLLMResult); // Added
                RegisterCommand.Register<UnifiedAudioStartCommand>(OnUnifiedAudioStart); // Added
                RegisterCommand.Register<UnifiedAudioDataCommand>(OnUnifiedAudioData); // Added
                RegisterCommand.Register<UnifiedAudioEndCommand>(OnUnifiedAudioEnd); // Added
                RegisterCommand.Register<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged); // Added
                Debug.Log("✅ 统一服务事件监听注册完成");
            }
            
            // 🔧 启动定期状态报告
            if (_mono != null)
            {
                _mono.StartCoroutine(StatusReportCoroutine());
            }
        }

        /// <summary>
        /// 🔧 定期状态报告协程
        /// </summary>
        private IEnumerator StatusReportCoroutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(60f); // 每分钟检查一次
                PeriodicStatusReport();
                
                // 🔧 定期强制GC（如果需要）
                ForceGarbageCollection();
            }
        }
         
        private void StopGenerateCommandCallback(StopGenerateCommand obj)
        {
            if (!object.ReferenceEquals(null, _titleSynchronization_Cor))
                _mono.StopCoroutine(_titleSynchronization_Cor);
            if (!object.ReferenceEquals(null, _requestGPTSegmentationCor))
                _mono.StopCoroutine(_requestGPTSegmentationCor);

            _titleSynchronization_Cor = null;
            _requestGPTSegmentationCor = null;

            PlayFinish();
        }

        /// <summary>
        /// 语音识别到内容回调
        /// </summary>
        /// <param name="obj"></param>
        private void VoiceRecognitionResultCommandCallback(VoiceRecognitionResultCommand obj)
        {
            Debug.Log($"🔧 VoiceRecognitionResultCommandCallback被调用 - text: '{obj.text}', IsComplete: {obj.IsComplete}");
            
            if (!obj.IsComplete)
            {
                Debug.Log($"🔧 ASR中间结果，准备创建用户对话框");
                // 实时显示识别中的文本
                if (_showUITextAction == null)
                    SendCommand.Send(new AddChatContentCommand { infoType = Enum.InfoType.My, _addTextAction = value => _showUITextAction = value });

                _showUITextAction.Invoke(obj.text);
                onceResult = obj.text;
            }
            else
            {
                Debug.Log($"🔧 ASR最终结果，准备创建ChatGPT对话框");
                // 语音识别完成
                if (string.IsNullOrEmpty(onceResult))
                    return;

                onceResult = obj.text;
                SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = false });//停止语音识别

                if (UseUnifiedService)
                {
                    // 统一服务模式：等待LLM和音频结果
                    Debug.Log($"🎯 统一服务模式 - 语音识别完成: '{onceResult}', 等待LLM回复...");
                    
                    // 准备接收ChatGPT回复的UI
                    SendCommand.Send(new AddChatContentCommand { infoType = Enum.InfoType.ChatGPT, _addTextAction = value => _showUITextAction = value });
                    
                    // 清理音频队列
                    ClearGPTVoice();
                    ClearUnifiedAudioQueue(); // Added
                    
                    // 重置状态
                    currentLLMAccumulatedText = "";
                    isStopCreate = false;
                    isRequestChatGPTContent = false;
                    
                    Debug.Log("✅ 统一服务模式已准备好接收LLM回复和音频数据");
                }
                else
                {
                    // 传统模式：发送LLM HTTP请求
                    Debug.Log($"🔄 传统模式 - 发送LLM请求: '{onceResult}'");
                    
                    SendCommand.Send(new AddChatContentCommand { infoType = Enum.InfoType.ChatGPT, _addTextAction = value => _showUITextAction = value });
                    ClearGPTVoice();
                    
                    _requestGPTSegmentationCor = _mono.StartCoroutine(LLM.Request(onceResult, ChatGPTRequestCallback));
                    isStopCreate = false;
                }
                
                onceResult = string.Empty;
            }
        }

        private void ChatGPTRequestCallback(string arg1, bool arg2)
        {
            if (!string.IsNullOrEmpty(arg1))
                _mono.StartCoroutine(SynthesisCoroutine(arg1));
             
            isRequestChatGPTContent = arg2;

        }


        private IEnumerator SynthesisCoroutine(string text)
        {
            IEnumerator youdaoIE = VoiceTTS.Synthesis(text);

            gptVoice.Enqueue(new ResultData { clip = youdaoIE, result = text });
             
            yield return youdaoIE;
            if (_titleSynchronization_Cor == null)
                _titleSynchronization_Cor = _mono.StartCoroutine(TitleSynchronizationCoroutine());

        }

        /// <summary>
        /// 字幕同步协程
        /// </summary>
        /// <returns></returns>
        private IEnumerator TitleSynchronizationCoroutine()
        {
            SendCommand.Send(new ChatGPTStartTalkCommand());//开始播放

            int lastIndex = -1;
            while (!isStopCreate && (gptVoice.Count > 0 || !isRequestChatGPTContent))
            { 
                if (gptVoice.Count == 0)
                {
                    yield return null;
                    continue;
                }
                ResultData result = gptVoice.Dequeue();

                yield return result;
                if (result.clip.Current is AudioClip clip)
                {
                    audioModel.Play(clip);

                    int temp_Index = 0;
                    yield return null;
                    yield return null;

                     
                    while (true)
                    {
                        if (audioModel.Time == clip.length || !audioModel.IsPlaying || _showUITextAction == null)
                            break;

                        temp_Index = (int)((audioModel.Time / clip.length) * result.result.Length) - 1;
                        temp_Index = Mathf.Clamp(temp_Index, 0, result.result.Length);

                        if (lastIndex != temp_Index)
                        {
                            string str = result.result[temp_Index].ToString();
                            _showUITextAction(str);
                            lastIndex = temp_Index;
                        }

                        yield return null;
                    }


                    GameObject.Destroy(clip);

                    if (result.result.Length > ++temp_Index)
                    {
                        string str = result.result[temp_Index].ToString();
                        _showUITextAction(str);
                    }
                }
            }


            PlayFinish();
        }

        void ClearGPTVoice()
        {
            while (gptVoice.Count > 0)
            {
                var item = gptVoice.Dequeue();
                if (item.clip.Current is AudioClip clip)
                    GameObject.Destroy(clip);

            }
        }

        /// <summary>
        /// 播放完成
        /// </summary>
        private void PlayFinish()
        {
            isStopCreate = true;

            isTalkingAnimationStarted = false;

            _titleSynchronization_Cor = null;
            _unifiedAudioSyncCoroutine = null; // Added
            
            ClearGPTVoice();
            if (UseUnifiedService)
            {
                ClearUnifiedAudioQueue(); // Added
            }

            SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = true });//开始语音识别
            SendCommand.Send(new GenerateFinishCommand { });//生成完成命令

            _showUITextAction = null;

            audioModel.Stop();
        }

        #region 统一服务事件处理
        /// <summary>
        /// 清理统一服务音频队列
        /// </summary>
        private void ClearUnifiedAudioQueue() // Added
        {
            while (unifiedAudioQueue.Count > 0)
            {
                var segment = unifiedAudioQueue.Dequeue();
                if (segment.generatedClip != null)
                {
                    GameObject.Destroy(segment.generatedClip);
                }
            }
            Debug.Log("🗑️ 统一服务音频队列已清理");
        }

        /// <summary>
        /// 处理统一服务LLM结果
        /// </summary>
        private void OnUnifiedLLMResult(UnifiedLLMResultCommand command) // Added
        {
            try
            {
                // 🔧 事件去重检查
                string eventIdentifier = $"{command.text.GetHashCode()}_{command.isFirst}_{command.isEnd}";
                if (IsEventAlreadyProcessed("LLMResult", eventIdentifier))
                {
                    return; // 跳过重复事件
                }

                Debug.Log($"🤖 收到LLM回复: '{command.text}' (首次: {command.isFirst}, 结束: {command.isEnd})");

                // 🔧 修复：只在真正的首次回复时重置
                if (command.isFirst && !hasProcessedFirstLLMReply)
                {
                    hasProcessedFirstLLMReply = true;
                    displayedTexts.Clear();
                    currentLLMAccumulatedText = "";
                    Debug.Log("🔄 LLM首次回复，重置累积文本和去重集合");
                    
                    // 🔧 优化：在LLMLogic中移除过早的动画触发
                    // SendCommand.Send(new ChatGPTStartTalkCommand());
                    
                    // �� 新增：标记准备开始，但等待音频就绪
                    // isReadyForAnimation = true; // This variable is not defined in the original code
                    IncrementEventCounter("LLMFirstReply");
                }

                // 🔧 处理文本：分离对话内容和动作描述
                if (!string.IsNullOrEmpty(command.text))
                {
                    // 提取动作描述用于角色控制
                    var actionDescriptions = ExtractActionDescriptions(command.text);
                    foreach (var action in actionDescriptions)
                    {
                        Debug.Log($"🎭 检测到动作描述: '{action}'");
                        // TODO: 这里可以发送动作命令给角色控制系统
                        // SendCommand.Send(new CharacterActionCommand { action = action });
                    }
                    
                    // 过滤掉动作描述，只保留对话内容
                    string dialogueText = FilterActionDescriptions(command.text);
                    
                    if (!string.IsNullOrEmpty(dialogueText))
                    {
                        // 🔧 使用HashSet精确去重
                        if (!displayedTexts.Contains(dialogueText))
                        {
                            displayedTexts.Add(dialogueText);
                            currentLLMAccumulatedText += dialogueText;
                            
                            // 🔧 [移除] 不再此处直接显示UI，交由字幕协程处理
                            // if (_showUITextAction != null)
                            // {
                            //     _showUITextAction.Invoke(dialogueText);
                            // }
                            Debug.Log($"📝 累积新文本（已过滤）: '{dialogueText}', 总长度: {currentLLMAccumulatedText.Length}");
                        }
                        else
                        {
                            Debug.LogWarning($"⚠️ 跳过重复文本: '{dialogueText}'");
                            IncrementEventCounter("DuplicateTextSkipped");
                        }
                    }
                    else
                    {
                        Debug.Log($"📝 文本全为动作描述，跳过UI显示: '{command.text}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理LLM结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务音频开始通知
        /// </summary>
        private void OnUnifiedAudioStart(UnifiedAudioStartCommand command) // Added
        {
            try
            {
                // 🔧 事件去重检查
                string eventIdentifier = $"{command.text.GetHashCode()}_{command.fileName}";
                if (IsEventAlreadyProcessed("AudioStart", eventIdentifier))
                {
                    return; // 跳过重复事件
                }

                // 🔧 严格的去重检查 - 防止重复音频段
                bool isDuplicate = false;
                foreach (var existingSegment in unifiedAudioQueue)
                {
                    if (existingSegment.text == command.text)
                    {
                        Debug.LogWarning($"⚠️ 跳过重复音频段: '{command.text}'");
                        isDuplicate = true;
                        break;
                    }
                }
                
                if (isDuplicate)
                {
                    IncrementEventCounter("DuplicateAudioSegmentSkipped");
                    return;
                }

                // 🔧 过滤动作描述，只保留对话内容用于字幕
                string dialogueText = FilterActionDescriptions(command.text);
                
                if (string.IsNullOrEmpty(dialogueText) || IsNonSpeechText(dialogueText))
                {
                    Debug.Log($"⏭️ 跳过非语音音频段: '{command.text}'");
                    return;
                }

                Debug.Log($"🔊 音频段开始预处理: '{dialogueText}'");
                
                // 创建新的音频段（使用过滤后的文本）
                var audioSegment = new UnifiedAudioSegment(dialogueText);
                unifiedAudioQueue.Enqueue(audioSegment);
                
                // 存储原始文本用于音频匹配
                audioSegment.originalText = command.text;
                
                // 🔧 如果有缓冲的音频数据，立即添加到新创建的音频段
                int processedChunks = ProcessPendingAudioChunks(audioSegment);
                if (processedChunks > 0)
                {
                    Debug.Log($"✅ 音频段创建时处理了 {processedChunks} 个缓冲音频块");
                }
                
                // 启动音频同步协程（如果还没启动）
                if (_unifiedAudioSyncCoroutine == null)
                {
                    _unifiedAudioSyncCoroutine = _mono.StartCoroutine(OptimizedUnifiedAudioSynchronizationCoroutine());
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频开始异常: {ex}");
            }
        }

        // 音频数据缓冲区（当音频段还未创建时临时存储）
        private Queue<byte[]> _pendingAudioChunks = new Queue<byte[]>(); // Added
        
        // 🔧 内存管理优化：音频数据池化
        private readonly Queue<byte[]> _audioDataPool = new Queue<byte[]>(); // Added
        private readonly Queue<float[]> _floatArrayPool = new Queue<float[]>(); // Added
        private readonly List<AudioClip> _activeAudioClips = new List<AudioClip>(); // Added
        private const int MAX_POOL_SIZE = 20; // 池最大大小
        private const int TYPICAL_CHUNK_SIZE = 8192; // 典型音频块大小
        private long _totalBytesAllocated = 0; // 内存使用统计
        private int _audioClipCount = 0; // AudioClip数量统计
        
        // 🔧 监控和日志系统
        private readonly Dictionary<string, float> _performanceMetrics = new Dictionary<string, float>(); // Added
        private readonly Dictionary<string, int> _eventCounters = new Dictionary<string, int>(); // Added
        private readonly List<string> _recentErrors = new List<string>(); // Added
        private System.Diagnostics.Stopwatch _sessionStopwatch = new System.Diagnostics.Stopwatch(); // Added
        private DateTime _lastLogTime = DateTime.Now; // Added
        private const int MAX_ERROR_HISTORY = 20; // 最大错误历史记录数
        
        // 🔧 事件去重机制
        private readonly HashSet<string> _processedEventIds = new HashSet<string>(); // Added
        private readonly Queue<string> _eventIdHistory = new Queue<string>(); // Added
        private const int MAX_EVENT_HISTORY = 100; // 最大事件历史记录数
        
        /// <summary>
        /// 处理统一服务音频数据
        /// </summary>
        private void OnUnifiedAudioData(UnifiedAudioDataCommand command) // Added
        {
            try
            {
                // 🔧 监控：记录音频数据接收事件
                IncrementEventCounter("AudioDataReceived");
                RecordPerformanceMetric("LastAudioDataSize", command.audioData?.Length ?? 0);
                
                // 🔧 优化：先尝试创建缺失的音频段
                if (unifiedAudioQueue.Count == 0 && !string.IsNullOrEmpty(command.associatedText))
                {
                    // 如果没有音频段但有关联文本，主动创建音频段
                    Debug.Log($"🎯 音频数据到达但无音频段，主动创建段: '{command.associatedText}'");
                    var autoSegment = new UnifiedAudioSegment(command.associatedText);
                    unifiedAudioQueue.Enqueue(autoSegment);
                    IncrementEventCounter("AutoCreatedAudioSegments");
                }
                
                if (unifiedAudioQueue.Count > 0)
                {
                    // 将音频数据添加到最新的音频段
                    var currentSegment = unifiedAudioQueue.ToArray()[unifiedAudioQueue.Count - 1];
                    currentSegment.audioChunks.Enqueue(command.audioData);
                    
                    Debug.Log($"📡 收到音频数据: {command.audioData.Length} bytes, 添加到段: '{currentSegment.text.Substring(0, Math.Min(currentSegment.text.Length, 20))}...'");
                    
                    // 🔧 优化：如果有缓冲的音频数据，立即处理
                    int bufferedChunks = ProcessPendingAudioChunks(currentSegment);
                    
                    if (bufferedChunks > 0)
                    {
                        IncrementEventCounter("BufferedAudioDataApplied");
                        RecordPerformanceMetric("BufferedChunksProcessed", bufferedChunks);
                        Debug.Log($"✅ 已处理 {bufferedChunks} 个缓冲音频块");
                    }
                }
                else
                {
                    // 🔧 优化：智能缓冲策略
                    _pendingAudioChunks.Enqueue(command.audioData);
                    IncrementEventCounter("AudioDataBuffered");
                    Debug.LogWarning($"⚠️ 音频段未就绪，缓冲音频数据: {command.audioData.Length} bytes (缓冲区大小: {_pendingAudioChunks.Count})");
                    
                    // 🔧 优化：更智能的缓冲区管理
                    if (_pendingAudioChunks.Count > 30) // 降低阈值，更早清理
                    {
                        var discarded = _pendingAudioChunks.Dequeue();
                        IncrementEventCounter("AudioDataDiscarded");
                        RecordError($"缓冲区过大，丢弃音频数据: {discarded.Length} bytes");
                        Debug.LogWarning($"⚠️ 缓冲区过大({_pendingAudioChunks.Count + 1}块)，丢弃最旧音频数据: {discarded.Length} bytes");
                    }
                    
                    // 🔧 尝试触发延迟音频段创建
                    // if (_pendingAudioChunks.Count >= 3 && _delayedSegmentCreation == null)
                    // {
                    //     _delayedSegmentCreation = _mono.StartCoroutine(DelayedAudioSegmentCreation());
                    // }
                }
            }
            catch (Exception ex)
            {
                RecordError("处理音频数据异常", ex);
                Debug.LogError($"❌ 处理音频数据异常: {ex}");
            }
        }

        // 🔧 新增：延迟音频段创建协程
        private Coroutine _delayedSegmentCreation = null;
        
        /// <summary>
        /// 延迟创建音频段（当缓冲区积累过多数据时）
        /// </summary>
        private IEnumerator DelayedAudioSegmentCreation()
        {
            yield return new WaitForSeconds(0.5f); // 等待500ms
            
            if (unifiedAudioQueue.Count == 0 && _pendingAudioChunks.Count > 0)
            {
                Debug.Log("🚨 缓冲区积压过多，创建紧急音频段");
                var emergencySegment = new UnifiedAudioSegment("正在播放...");
                unifiedAudioQueue.Enqueue(emergencySegment);
                
                // 处理所有缓冲的音频数据
                int processedChunks = ProcessPendingAudioChunks(emergencySegment);
                Debug.Log($"🔧 紧急处理了 {processedChunks} 个缓冲音频块");
                
                IncrementEventCounter("EmergencyAudioSegmentsCreated");
            }
            
            _delayedSegmentCreation = null;
        }
        
        /// <summary>
        /// 处理待处理的音频块
        /// </summary>
        private int ProcessPendingAudioChunks(UnifiedAudioSegment targetSegment)
        {
            int processedCount = 0;
            while (_pendingAudioChunks.Count > 0)
            {
                var pendingChunk = _pendingAudioChunks.Dequeue();
                targetSegment.audioChunks.Enqueue(pendingChunk);
                processedCount++;
                Debug.Log($"📦 将缓冲音频数据添加到段: {pendingChunk.Length} bytes");
            }
            return processedCount;
        }
        
        /// <summary>
        /// 🔧 检查事件是否已经被处理过（去重机制）
        /// </summary>
        private bool IsEventAlreadyProcessed(string eventType, string identifier, double timestamp = 0)
        {
            // 生成事件ID
            string eventId = timestamp > 0 
                ? $"{eventType}_{identifier}_{timestamp:F3}" 
                : $"{eventType}_{identifier}";
            
            // 检查是否已处理
            if (_processedEventIds.Contains(eventId))
            {
                Debug.LogWarning($"⚠️ 跳过重复事件: {eventId}");
                IncrementEventCounter("DuplicateEventsSkipped");
                return true;
            }
            
            // 标记为已处理
            _processedEventIds.Add(eventId);
            _eventIdHistory.Enqueue(eventId);
            
            // 清理过期的事件记录
            while (_eventIdHistory.Count > MAX_EVENT_HISTORY)
            {
                var oldEventId = _eventIdHistory.Dequeue();
                _processedEventIds.Remove(oldEventId);
            }
            
            return false;
        }

        /// <summary>
        /// 处理统一服务音频结束通知
        /// </summary>
        private void OnUnifiedAudioEnd(UnifiedAudioEndCommand command) // Added
        {
            try
            {
                // 🔧 事件去重检查
                string eventIdentifier = $"{command.text.GetHashCode()}_{command.totalSize}";
                if (IsEventAlreadyProcessed("AudioEnd", eventIdentifier))
                {
                    return; // 跳过重复事件
                }

                Debug.Log($"✅ 音频结束: '{command.text}', 总大小: {command.totalSize} bytes");
                
                // 标记当前音频段为完成
                if (unifiedAudioQueue.Count > 0)
                {
                    var segments = unifiedAudioQueue.ToArray();
                    bool segmentFound = false;
                    foreach (var segment in segments)
                    {
                        var endDialogue = FilterActionDescriptions(command.text);
                        if ((segment.originalText == command.text) || (segment.text == endDialogue))
                        {
                            segment.isComplete = true;
                            // 开始生成AudioClip
                            _mono.StartCoroutine(GenerateAudioClipFromChunks(segment));
                            segmentFound = true;
                            Debug.Log($"🎵 标记音频段完成并开始生成: '{command.text}'");
                            break;
                        }
                    }
                    
                    if (!segmentFound)
                    {
                        Debug.LogWarning($"⚠️ 未找到匹配的音频段: '{command.text}'");
                    }
                }
                else
                {
                    Debug.LogWarning("⚠️ 音频结束但队列为空");
                }

                string dialogueText = FilterActionDescriptions(command.text);
                if (IsTooShortAudioBytes(command.totalSize) || string.IsNullOrWhiteSpace(dialogueText) || IsNonSpeechText(dialogueText))
                {
                    DropSegmentForText(command.text, dialogueText);
                    return; // 不生成AudioClip，不进入播放
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频结束异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务状态变化
        /// </summary>
        private void OnUnifiedServiceStateChanged(UnifiedServiceStateChangedCommand command) // Added
        {
            Debug.Log($"🔄 统一服务状态变化: {command.previousState} → {command.currentState}");
            
            if (command.currentState == UnifiedServiceState.Disconnected || 
                command.currentState == UnifiedServiceState.Error)
            {
                // 连接断开或出错，停止当前播放
                if (_unifiedAudioSyncCoroutine != null)
                {
                    _mono.StopCoroutine(_unifiedAudioSyncCoroutine);
                    _unifiedAudioSyncCoroutine = null;
                }
                PlayFinish();
            }
        }

        /// <summary>
        /// 从音频块生成AudioClip
        /// 🔧 增强的错误处理和质量控制
        /// </summary>
        private IEnumerator GenerateAudioClipFromChunks(UnifiedAudioSegment segment) // Added
        {
            // 🔧 基础验证
            if (segment == null)
            {
                Debug.LogError("❌ 音频段为null，无法生成AudioClip");
                yield break;
            }

            if (segment.audioChunks.Count == 0)
            {
                Debug.LogWarning($"⚠️ 音频段 '{segment.text}' 没有音频数据，生成静音段");
                // 🔧 为空音频段生成短静音
                SafeGenerateSilenceClipForSegment(segment, 0.1f);
                yield break;
            }

            Debug.Log($"🎵 开始生成AudioClip: '{segment.text}', 音频块数: {segment.audioChunks.Count}");

            // 🔧 合并音频块（协程方式，避免try-catch-yield冲突）
            yield return _mono.StartCoroutine(ProcessAudioChunksCoroutine(segment));
            
            yield return null;
        }

        /// <summary>
        /// 🔧 处理音频块的协程（分离yield逻辑）
        /// </summary>
        private IEnumerator ProcessAudioChunksCoroutine(UnifiedAudioSegment segment)
        {
            try 
            {
                byte[] combinedData = CombineAudioChunks(segment.audioChunks);
                
                // 🔧 添加音频格式检测
                if (IsMP3Format(combinedData))
                {
                    Debug.LogWarning($"⚠️ 检测到MP3格式音频，需要转换: '{segment.text}'");
                    // MP3格式需要特殊处理或转换
                    SafeGenerateSilenceClipForSegment(segment, 0.1f);
                    yield break;
                }
                
                // 🔧 添加音频数据验证
                if (!IsValidPCMData(combinedData))
                {
                    Debug.LogWarning($"⚠️ 音频数据格式无效: '{segment.text}', 数据长度: {combinedData.Length}");
                    SafeGenerateSilenceClipForSegment(segment, 0.1f);
                    yield break;
                }
                
                SafeCreateAudioClip(segment, combinedData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频处理异常: {ex.Message}");
                SafeGenerateSilenceClipForSegment(segment, 0.1f);
            }
        }

        private byte[] CombineAudioChunks(Queue<byte[]> chunks)
        {
            List<byte> combined = new List<byte>();
            while (chunks.Count > 0)
            {
                var chunk = chunks.Dequeue();
                if (chunk != null && chunk.Length > 0)
                {
                    combined.AddRange(chunk);
                }
            }
            return combined.ToArray();
        }

        private bool IsMP3Format(byte[] data)
        {
            // 检测MP3文件头（ID3标签或MPEG帧头）
            if (data.Length < 3) return false;
            
            // MP3文件通常以ID3标签开始
            if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) // "ID3"
                return true;
                
            // 或者以MPEG帧头开始
            if (data.Length >= 2 && (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
                return true;
                
            return false;
        }

        private bool IsValidPCMData(byte[] data)
        {
            // 基本验证：数据长度应该是2的倍数（16位PCM）
            if (data.Length % 2 != 0) return false;
            
            // 检查数据是否全为零（无效音频）
            bool hasNonZero = false;
            for (int i = 0; i < Math.Min(data.Length, 1000); i++)
            {
                if (data[i] != 0)
                {
                    hasNonZero = true;
                    break;
                }
            }
            
            return hasNonZero;
        }

        /// <summary>
        /// 🔧 安全处理音频块（带异常处理）
        /// </summary>
        private byte[] SafeProcessAudioChunk(Queue<byte[]> audioChunks)
        {
            try
            {
                if (audioChunks.Count > 0)
                {
                    var chunk = audioChunks.Dequeue();
                    if (chunk != null && chunk.Length > 0)
                    {
                        return chunk;
                    }
                    else
                    {
                        Debug.LogWarning($"⚠️ 跳过空音频块");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频块异常: {ex}");
                RecordError("处理音频块异常", ex);
            }
            
            return null;
        }

        /// <summary>
        /// 🔧 安全创建AudioClip（带完整异常处理）
        /// </summary>
        private void SafeCreateAudioClip(UnifiedAudioSegment segment, byte[] combinedData)
        {
            try
            {
                float[] pcmData = ConvertBytesToFloats(combinedData);
                
                if (pcmData.Length > 0)
                {
                    //  修复：使用uLipSync兼容的音频格式
                    int sampleRate = AudioSettings.outputSampleRate; // 使用Unity系统采样率
                    int channels = 1; // 单声道，uLipSync推荐
                    
                    // 🔧 修复：确保音频长度满足uLipSync要求
                    float minDuration = 0.3f; // 最少300ms，确保FFT分析准确
                    if (pcmData.Length < sampleRate * minDuration)
                    {
                        Debug.LogWarning($"⚠️ 音频段过短: {pcmData.Length / (float)sampleRate:F3}秒，扩展至{minDuration}秒");
                        // 扩展音频数据或生成静音填充
                        pcmData = ExtendAudioToMinimumLength(pcmData, sampleRate, minDuration);
                    }
                    
                    segment.generatedClip = AudioClip.Create(
                        $"UnifiedAudio_{segment.text.GetHashCode()}_{DateTime.Now.Ticks}", 
                        pcmData.Length, channels, sampleRate, false);
                    
                    if (segment.generatedClip != null)
                    {
                        //  修复：直接设置原始数据，避免过度处理
                        segment.generatedClip.SetData(pcmData, 0);
                        segment.estimatedDuration = (float)pcmData.Length / sampleRate;
                        
                        // 验证AudioClip质量
                        if (ValidateAudioClipForLipSync(segment.generatedClip))
                        {
                            RegisterAudioClip(segment.generatedClip);
                            Debug.Log($"✅ 生成uLipSync兼容AudioClip: 时长={segment.estimatedDuration:F2}秒, 采样率={sampleRate}Hz");
                        }
                        else
                        {
                            Debug.LogError($"❌ AudioClip质量验证失败，重新生成");
                            UnityEngine.Object.Destroy(segment.generatedClip);
                            segment.generatedClip = null;
                            SafeGenerateSilenceClipForSegment(segment, 0.5f);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 创建AudioClip异常: {ex}");
                SafeGenerateSilenceClipForSegment(segment, 0.5f);
            }
        }

        /// <summary>
        /// 🔧 安全生成静音段（最后的安全网）
        /// </summary>
        private void SafeGenerateSilenceClipForSegment(UnifiedAudioSegment segment, float duration)
        {
            try
            {
                segment.generatedClip = GenerateSilenceClip(duration);
                segment.estimatedDuration = duration;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 生成静音段异常: {ex}");
                RecordError("生成静音段异常", ex);
                segment.generatedClip = null;
                segment.estimatedDuration = 0f;
            }
        }

        /// <summary>
        /// 🔧 生成静音AudioClip
        /// </summary>
        private AudioClip GenerateSilenceClip(float duration)
        {
            try
            {
                // 🔧 修复：使用系统采样率，与uLipSync兼容
                int sampleRate = AudioSettings.outputSampleRate;
                int samples = Mathf.RoundToInt(sampleRate * duration);
                float[] silenceData = new float[samples];
                
                AudioClip silenceClip = AudioClip.Create($"Silence_{DateTime.Now.Ticks}", 
                    samples, 1, sampleRate, false);
                silenceClip.SetData(silenceData, 0);
                
                return silenceClip;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 生成静音AudioClip异常: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 🔧 音频后处理（音量调整、噪音抑制等）
        /// </summary>
        private float[] PostProcessAudio(float[] audio)
        {
            try
            {
                if (audio == null || audio.Length == 0) return audio;

                // 1) 去直流（DC offset）
                float mean = 0f;
                for (int i = 0; i < audio.Length; i++) mean += audio[i];
                mean /= audio.Length;
                for (int i = 0; i < audio.Length; i++) audio[i] -= mean;

                // 2) 峰值分析
                float maxAbs = 0f;
                for (int i = 0; i < audio.Length; i++)
                {
                    float a = Mathf.Abs(audio[i]);
                    if (a > maxAbs) maxAbs = a;
                }

                // 3) 预留头间距（headroom）：若过高则整体降增益
                const float headroom = 0.85f; // 预留15%空间
                if (maxAbs > headroom)
                {
                    float k = headroom / Mathf.Max(maxAbs, 1e-6f);
                    for (int i = 0; i < audio.Length; i++) audio[i] *= k;
                    maxAbs = headroom;
                }
                // 4) 仅在很低音量时适度增益（避免把峰值推到极限）
                else if (maxAbs > 0.001f && maxAbs < 0.35f)
                {
                    float targetPeak = 0.5f;               // 目标峰值
                    float k = targetPeak / maxAbs;
                    k = Mathf.Min(k, 2.0f);                // 最多放大2倍
                    for (int i = 0; i < audio.Length; i++) audio[i] *= k;
                    maxAbs = Mathf.Min(targetPeak, headroom);
                }

                // 5) 软限制（soft clip）进一步抑制尖峰
                for (int i = 0; i < audio.Length; i++)
                {
                    float v = audio[i] * 1.2f;             // 轻微预增益
                    v = (float)System.Math.Tanh(v);        // 注意：System.Math.Tanh
                    audio[i] = v * 0.9f;                   // 回拉一点，留余量
                }

                // 6) 首尾淡入/淡出（10-15ms）
                ApplyFadeInOut(audio, (int)(0.012f * 16000)); // 12ms@16kHz

                return audio;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频后处理异常: {ex}");
                return audio;
            }
        }

        private void ApplyFadeInOut(float[] data, int fadeSamples)
        {
            if (data == null || data.Length == 0) return;
            int n = data.Length;
            fadeSamples = Mathf.Clamp(fadeSamples, 1, n / 4);

            // 淡入
            for (int i = 0; i < fadeSamples; i++)
            {
                float t = i / (float)fadeSamples;
                data[i] *= t;
            }
            // 淡出
            for (int i = 0; i < fadeSamples; i++)
            {
                float t = i / (float)fadeSamples;
                int idx = n - 1 - i;
                data[idx] *= t;
            }
        }

        #region 🔧 内存管理优化方法

        /// <summary>
        /// 从池中获取字节数组
        /// </summary>
        private byte[] GetPooledByteArray(int minSize)
        {
            try
            {
                // 尝试从池中获取合适大小的数组
                while (_audioDataPool.Count > 0)
                {
                    var pooledArray = _audioDataPool.Dequeue();
                    if (pooledArray.Length >= minSize)
                    {
                        return pooledArray;
                    }
                    // 太小的数组直接丢弃，让GC回收
                }
                
                // 池中没有合适的，创建新数组
                var newArray = new byte[Math.Max(minSize, TYPICAL_CHUNK_SIZE)];
                _totalBytesAllocated += newArray.Length;
                return newArray;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 获取池化字节数组异常: {ex}");
                return new byte[minSize];
            }
        }

        /// <summary>
        /// 将字节数组归还到池中
        /// </summary>
        private void ReturnPooledByteArray(byte[] array)
        {
            try
            {
                if (array != null && _audioDataPool.Count < MAX_POOL_SIZE)
                {
                    // 清零数组内容
                    Array.Clear(array, 0, array.Length);
                    _audioDataPool.Enqueue(array);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 归还池化字节数组异常: {ex}");
            }
        }

        /// <summary>
        /// 从池中获取浮点数组
        /// </summary>
        private float[] GetPooledFloatArray(int minSize)
        {
            try
            {
                while (_floatArrayPool.Count > 0)
                {
                    var pooledArray = _floatArrayPool.Dequeue();
                    if (pooledArray.Length >= minSize)
                    {
                        return pooledArray;
                    }
                }
                
                return new float[Math.Max(minSize, TYPICAL_CHUNK_SIZE / 2)]; // 浮点数数组通常是字节数组的一半大小
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 获取池化浮点数组异常: {ex}");
                return new float[minSize];
            }
        }

        /// <summary>
        /// 将浮点数组归还到池中
        /// </summary>
        private void ReturnPooledFloatArray(float[] array)
        {
            try
            {
                if (array != null && _floatArrayPool.Count < MAX_POOL_SIZE)
                {
                    Array.Clear(array, 0, array.Length);
                    _floatArrayPool.Enqueue(array);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 归还池化浮点数组异常: {ex}");
            }
        }

        /// <summary>
        /// 管理AudioClip生命周期
        /// </summary>
        private void RegisterAudioClip(AudioClip clip)
        {
            try
            {
                if (clip != null)
                {
                    _activeAudioClips.Add(clip);
                    _audioClipCount++;
                    
                    // 🔧 限制同时活跃的AudioClip数量
                    if (_activeAudioClips.Count > 10)
                    {
                        // 移除最老的AudioClip
                        var oldestClip = _activeAudioClips[0];
                        _activeAudioClips.RemoveAt(0);
                        
                        if (oldestClip != null)
                        {
                            UnityEngine.Object.Destroy(oldestClip);
                            Debug.Log("🧹 自动清理旧AudioClip以控制内存使用");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 注册AudioClip异常: {ex}");
            }
        }

        /// <summary>
        /// 清理AudioClip
        /// </summary>
        private void UnregisterAudioClip(AudioClip clip)
        {
            try
            {
                if (clip != null && _activeAudioClips.Contains(clip))
                {
                    _activeAudioClips.Remove(clip);
                    UnityEngine.Object.Destroy(clip);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 注销AudioClip异常: {ex}");
            }
        }

        /// <summary>
        /// 强制垃圾回收（仅在必要时使用）
        /// </summary>
        private void ForceGarbageCollection()
        {
            try
            {
                // 只有在内存压力较大时才强制GC
                if (_totalBytesAllocated > 50 * 1024 * 1024) // 50MB
                {
                    Debug.Log("🧹 内存使用较高，强制执行垃圾回收");
                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                    _totalBytesAllocated = _totalBytesAllocated / 2; // 假设GC回收了一半内存
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 强制垃圾回收异常: {ex}");
            }
        }

        /// <summary>
        /// 打印内存使用统计
        /// </summary>
        private void LogMemoryStats()
        {
            try
            {
                Debug.Log($"📊 内存使用统计: " +
                         $"字节池: {_audioDataPool.Count}/{MAX_POOL_SIZE}, " +
                         $"浮点池: {_floatArrayPool.Count}/{MAX_POOL_SIZE}, " +
                         $"活跃AudioClip: {_activeAudioClips.Count}, " +
                         $"总AudioClip: {_audioClipCount}, " +
                         $"估算内存: {_totalBytesAllocated / 1024 / 1024:F1}MB");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 内存统计异常: {ex}");
            }
        }

        #endregion

        #region 🔧 监控和日志系统

        /// <summary>
        /// 记录性能指标
        /// </summary>
        private void RecordPerformanceMetric(string metricName, float value)
        {
            try
            {
                _performanceMetrics[metricName] = value;
                
                // 记录特别重要的指标
                if (metricName.Contains("Duration") || metricName.Contains("Latency"))
                {
                    LogStructured("PerformanceMetric", new Dictionary<string, object>
                    {
                        {"metric", metricName},
                        {"value", value},
                        {"timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")},
                        {"session_time", _sessionStopwatch.ElapsedMilliseconds}
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 记录性能指标异常: {ex}");
            }
        }

        /// <summary>
        /// 增加事件计数器
        /// </summary>
        private void IncrementEventCounter(string eventName)
        {
            try
            {
                if (_eventCounters.ContainsKey(eventName))
                {
                    _eventCounters[eventName]++;
                }
                else
                {
                    _eventCounters[eventName] = 1;
                }
                
                // 每10次记录一次统计
                if (_eventCounters[eventName] % 10 == 0)
                {
                    LogStructured("EventCounter", new Dictionary<string, object>
                    {
                        {"event", eventName},
                        {"count", _eventCounters[eventName]},
                        {"timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")}
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 增加事件计数器异常: {ex}");
            }
        }

        /// <summary>
        /// 记录错误到历史
        /// </summary>
        private void RecordError(string errorMessage, Exception exception = null)
        {
            try
            {
                var errorInfo = $"{DateTime.Now:HH:mm:ss.fff} - {errorMessage}";
                if (exception != null)
                {
                    errorInfo += $" | {exception.GetType().Name}: {exception.Message}";
                }
                
                _recentErrors.Add(errorInfo);
                
                // 限制错误历史大小
                while (_recentErrors.Count > MAX_ERROR_HISTORY)
                {
                    _recentErrors.RemoveAt(0);
                }
                
                // 结构化日志记录
                LogStructured("Error", new Dictionary<string, object>
                {
                    {"message", errorMessage},
                    {"exception_type", exception?.GetType().Name ?? "None"},
                    {"exception_message", exception?.Message ?? ""},
                    {"stack_trace", exception?.StackTrace ?? ""},
                    {"timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")},
                    {"session_time", _sessionStopwatch.ElapsedMilliseconds}
                });
                
                IncrementEventCounter("Errors");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 记录错误历史异常: {ex}");
            }
        }

        /// <summary>
        /// 结构化日志记录
        /// </summary>
        private void LogStructured(string logType, Dictionary<string, object> data)
        {
            try
            {
                var jsonData = new Dictionary<string, object>
                {
                    {"type", logType},
                    {"data", data},
                    {"system", "UnityWebGLVoice"},
                    {"component", "LLMLogic"},
                    {"timestamp_iso", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}
                };
                
                // 简化的JSON序列化（避免依赖外部库）
                var jsonString = SimpleJsonSerialize(jsonData);
                Debug.Log($"📊 [STRUCTURED_LOG] {jsonString}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 结构化日志记录异常: {ex}");
            }
        }

        /// <summary>
        /// 简单的JSON序列化（仅用于日志）
        /// </summary>
        private string SimpleJsonSerialize(Dictionary<string, object> data)
        {
            try
            {
                var items = new List<string>();
                foreach (var kvp in data)
                {
                    var value = kvp.Value;
                    string valueString;
                    
                    if (value is string)
                    {
                        valueString = $"\"{value}\"";
                    }
                    else if (value is Dictionary<string, object> dict)
                    {
                        valueString = SimpleJsonSerialize(dict);
                    }
                    else
                    {
                        valueString = value?.ToString() ?? "null";
                    }
                    
                    items.Add($"\"{kvp.Key}\":{valueString}");
                }
                
                return "{" + string.Join(",", items.ToArray()) + "}";
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ JSON序列化异常: {ex}");
                return "{}";
            }
        }

        /// <summary>
        /// 定期状态报告
        /// </summary>
        private void PeriodicStatusReport()
        {
            try
            {
                var now = DateTime.Now;
                if ((now - _lastLogTime).TotalMinutes >= 1) // 每分钟报告一次
                {
                    LogStructured("StatusReport", new Dictionary<string, object>
                    {
                        {"session_duration_minutes", _sessionStopwatch.ElapsedMilliseconds / 60000.0},
                        {"audio_clips_created", _audioClipCount},
                        {"active_audio_clips", _activeAudioClips.Count},
                        {"memory_pool_usage", new Dictionary<string, object>
                        {
                            {"byte_pool", _audioDataPool.Count},
                            {"float_pool", _floatArrayPool.Count},
                            {"estimated_memory_mb", _totalBytesAllocated / 1024.0 / 1024.0}
                        }},
                        {"queue_status", new Dictionary<string, object>
                        {
                            {"unified_audio_queue", unifiedAudioQueue.Count},
                            {"pending_audio_chunks", _pendingAudioChunks.Count}
                        }},
                        {"recent_errors_count", _recentErrors.Count},
                        {"event_counters", _eventCounters},
                        {"performance_metrics", _performanceMetrics}
                    });
                    
                    _lastLogTime = now;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 定期状态报告异常: {ex}");
            }
        }

        /// <summary>
        /// 初始化监控系统
        /// </summary>
        private void InitializeMonitoring()
        {
            try
            {
                _sessionStopwatch.Start();
                _lastLogTime = DateTime.Now;
                
                LogStructured("SessionStart", new Dictionary<string, object>
                {
                    {"unity_version", Application.unityVersion},
                    {"platform", Application.platform.ToString()},
                    {"is_webgl", Application.platform == RuntimePlatform.WebGLPlayer},
                    {"use_unified_service", UseUnifiedService}
                });
                
                Debug.Log("📊 监控系统已初始化");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 初始化监控系统异常: {ex}");
            }
        }

        #endregion

        /// <summary>
        /// 将字节数组转换为浮点音频数据
        /// 🔧 增强的音频数据验证和错误处理
        /// </summary>
        private float[] ConvertBytesToFloats(byte[] byteArray) // Added
        {
            try
            {
                // 🔧 基础数据验证
                if (byteArray == null || byteArray.Length == 0)
                {
                    Debug.LogWarning("⚠️ 音频数据为空或null");
                    return new float[0];
                }

                // 🔧 最小数据长度检查
                if (byteArray.Length < 2)
                {
                    Debug.LogWarning($"⚠️ 音频数据过短: {byteArray.Length} bytes，至少需要2 bytes");
                    return new float[0];
                }

                // 🔧 16位PCM格式验证
                if (byteArray.Length % 2 != 0)
                {
                    Debug.LogWarning($"⚠️ 音频数据长度 {byteArray.Length} 不是2的倍数，尝试修复");
                    // 截断最后一个字节以保证偶数长度
                    Array.Resize(ref byteArray, byteArray.Length - 1);
                }

                // 🔧 数据完整性检查
                if (!ValidateAudioData(byteArray))
                {
                    Debug.LogWarning("⚠️ 音频数据完整性验证失败，可能包含损坏数据");
                    byteArray = RepairAudioData(byteArray);
                }

                float[] floatArray = new float[byteArray.Length / 2];
                float maxAmplitude = 0f;
                
                for (int i = 0; i < floatArray.Length; i++)
                {
                    short sample = System.BitConverter.ToInt16(byteArray, i * 2);
                    floatArray[i] = sample / 32768.0f; // 归一化到 [-1, 1]
                    
                    // 🔧 计算最大振幅用于质量检查
                    maxAmplitude = Math.Max(maxAmplitude, Math.Abs(floatArray[i]));
                }
                
                // 🔧 音频质量检查
                if (maxAmplitude < 0.001f)
                {
                    Debug.LogWarning($"⚠️ 检测到静音或极低音量音频段，最大振幅: {maxAmplitude:F6}");
                }
                else if (maxAmplitude > 0.95f)
                {
                    Debug.LogWarning($"⚠️ 检测到可能的音频剪切，最大振幅: {maxAmplitude:F3}");
                }
                
                Debug.Log($"🎵 音频转换成功: {byteArray.Length} bytes → {floatArray.Length} samples, 最大振幅: {maxAmplitude:F3}");
                return floatArray;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频数据转换异常: {ex}");
                return new float[0];
            }
        }

        /// <summary>
        /// 🔧 验证音频数据完整性
        /// </summary>
        private bool ValidateAudioData(byte[] audioData)
        {
            try
            {
                // 检查是否全是零值（可能的损坏标志）
                int zeroCount = 0;
                for (int i = 0; i < Math.Min(audioData.Length, 100); i++) // 只检查前100字节
                {
                    if (audioData[i] == 0) zeroCount++;
                }
                
                // 如果超过80%是零值，可能是损坏数据
                if (zeroCount > 80)
                {
                    Debug.LogWarning($"⚠️ 音频数据可能损坏: {zeroCount}% 零值");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频数据验证异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 🔧 修复损坏的音频数据
        /// </summary>
        private byte[] RepairAudioData(byte[] audioData)
        {
            try
            {
                Debug.Log("🔧 尝试修复音频数据...");
                
                // 简单的修复策略：用非零值替换连续的零值
                byte[] repairedData = new byte[audioData.Length];
                Array.Copy(audioData, repairedData, audioData.Length);
                
                for (int i = 1; i < repairedData.Length - 1; i++)
                {
                    if (repairedData[i] == 0 && repairedData[i-1] != 0 && repairedData[i+1] != 0)
                    {
                        // 用前后值的平均值替换零值
                        repairedData[i] = (byte)((repairedData[i-1] + repairedData[i+1]) / 2);
                    }
                }
                
                Debug.Log("✅ 音频数据修复完成");
                return repairedData;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频数据修复异常: {ex}");
                return audioData; // 返回原始数据
            }
        }

        /// <summary>
        /// 过滤动作描述文字，只保留真正的对话内容
        /// </summary>
        private string FilterActionDescriptions(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            
            // 移除动作描述（星号包围的文字）
            string filteredText = System.Text.RegularExpressions.Regex.Replace(
                text, 
                @"\*[^*]*\*", 
                ""
            );
            
            // 清理多余的换行符和空格
            filteredText = System.Text.RegularExpressions.Regex.Replace(
                filteredText, 
                @"\n\s*\n", 
                "\n"
            ).Trim();
            
            return filteredText;
        }

        /// <summary>
        /// 提取动作描述文字，用于控制角色动画
        /// </summary>
        private List<string> ExtractActionDescriptions(string text)
        {
            var actions = new List<string>();
            if (string.IsNullOrEmpty(text))
                return actions;
            
            var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\*([^*]*)\*");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    actions.Add(match.Groups[1].Value.Trim());
                }
            }
            
            return actions;
        }

        /// <summary>
        /// 统一服务音频同步协程 - 优化版
        /// </summary>
        private IEnumerator OptimizedUnifiedAudioSynchronizationCoroutine()
        {
            Debug.Log("🎵 优化版统一服务音频同步协程启动");
            
            // 🔧 第一步：确保有音频段准备好再启动动画
            yield return new WaitUntil(() => 
                unifiedAudioQueue.Count > 0 && 
                unifiedAudioQueue.Peek().isComplete && 
                unifiedAudioQueue.Peek().generatedClip != null);
            
            
            // 🔧 第二步：音频准备好了，启动动画
            Debug.Log("🎭 音频准备完成，启动动画");
            // 🔧 第一个音频段需要特殊处理，确保动画同步
            // if (!isTalkingAnimationStarted)
            // {
            //     SendCommand.Send(new ChatGPTStartTalkCommand());
            //     isTalkingAnimationStarted = true;
            // }
            
            float idleTimeout = 3.0f;
            float idleTimer = 0f;

            while (!isStopCreate && (unifiedAudioQueue.Count > 0 || !isRequestChatGPTContent))
            {
                if (unifiedAudioQueue.Count == 0)
                {
                    idleTimer += Time.deltaTime;
                    if (idleTimer > idleTimeout)
                    {
                        Debug.LogWarning($"⚠️ 等待音频超时，强制结束");
                        break;
                    }
                    yield return null;
                    continue;
                }

                idleTimer = 0f;
                var segment = unifiedAudioQueue.Peek();

                // 🔧 等待音频段完全准备好
                if (!segment.isComplete || segment.generatedClip == null)
                {
                    yield return new WaitForSeconds(0.02f); // 减少等待时间，提高响应速度
                    continue;
                }

                // 🔧 检查是否已在播放
                if (audioModel.IsPlaying && segment.isPlaying)
                {
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }

                // 🔧 开始播放音频段
                segment = unifiedAudioQueue.Dequeue();
                segment.isPlaying = true;

                float actualDuration = segment.generatedClip.length;
                
                // 🔧 音频播放
                audioModel.Play(segment.generatedClip);
                
                // 🔧 启动字幕协程
                _mono.StartCoroutine(PlaySubtitlesWithAnimationControl(segment, actualDuration));

                // 🔧 等待播放完成
                yield return new WaitWhile(() => audioModel.IsPlaying);

                segment.isPlaying = false;
            }

            Debug.Log("🎵 优化版音频同步协程结束");
            PlayFinish();
        }

        /// <summary>
        /// 为音频段播放字幕
        /// </summary>
        private IEnumerator PlaySubtitlesForSegment(UnifiedAudioSegment segment, float actualDuration) // Added
        {
            if (_showUITextAction == null || string.IsNullOrEmpty(segment.text))
                yield break;

            float startTime = Time.time;
            int lastCharIndex = -1;
            
            while (audioModel.IsPlaying && (Time.time - startTime) < actualDuration)
            {
                if (isStopCreate)
                    break;

                float elapsed = Time.time - startTime;
                float progress = elapsed / actualDuration;  // 🔧 使用实际时长
                int charIndex = Mathf.Clamp((int)(progress * segment.text.Length), 0, segment.text.Length - 1);
                
                if (charIndex != lastCharIndex && charIndex < segment.text.Length)
                {
                    _showUITextAction.Invoke(segment.text[charIndex].ToString());
                    lastCharIndex = charIndex;
                }
                
                yield return null;
            }

            // 显示剩余字符
            if (lastCharIndex < segment.text.Length - 1)
            {
                for (int i = lastCharIndex + 1; i < segment.text.Length; i++)
                {
                    _showUITextAction.Invoke(segment.text[i].ToString());
                    yield return new WaitForSeconds(0.05f);
                }
            }
        }

        /// <summary>
        /// 🚀 智能动画强度控制
        /// </summary>
        private IEnumerator PlaySubtitlesWithAnimationControl(UnifiedAudioSegment segment, float actualDuration)
        {
            if (_showUITextAction == null || string.IsNullOrEmpty(segment.text))
                yield break;

            float startTime = Time.time;
            int lastCharIndex = -1;
            
            while (audioModel.IsPlaying && (Time.time - startTime) < actualDuration)
            {
                if (isStopCreate) break;

                float elapsed = Time.time - startTime;
                float progress = elapsed / actualDuration;
                int charIndex = Mathf.Clamp((int)(progress * segment.text.Length), 0, segment.text.Length - 1);
                
                if (charIndex != lastCharIndex && charIndex < segment.text.Length)
                {
                    _showUITextAction.Invoke(segment.text[charIndex].ToString());
                    lastCharIndex = charIndex;
                    
                    // 🔧 修复：移除不存在的命令，改为日志记录
                    char currentChar = segment.text[charIndex];
                    float animationIntensity = GetAnimationIntensityForCharacter(currentChar);
                    
                    // 记录动画强度信息（用于调试）
                    if (charIndex % 5 == 0) // 每5个字符记录一次，避免日志过多
                    {
                        Debug.Log($"🎭 字符 '{currentChar}' 动画强度: {animationIntensity:F2}");
                    }
                }
                
                yield return null;
            }

            // 显示剩余字符
            if (lastCharIndex < segment.text.Length - 1)
            {
                for (int i = lastCharIndex + 1; i < segment.text.Length; i++)
                {
                    _showUITextAction.Invoke(segment.text[i].ToString());
                    yield return new WaitForSeconds(0.03f); // 加快显示速度
                }
            }
        }

        /// <summary>
        /// 🔧 根据字符类型获取动画强度
        /// </summary>
        private float GetAnimationIntensityForCharacter(char character)
        {
            // 元音字母需要更大的嘴部动作
            if ("aeiouAEIOU".Contains(character))
                return 1.0f;
            
            // 辅音需要中等动作
            if (char.IsLetter(character))
                return 0.7f;
            
            // 标点符号降低强度
            if (char.IsPunctuation(character))
                return 0.3f;
            
            // 默认强度
            return 0.5f;
        }
        #endregion

        #region 资源清理
        /// <summary>
        /// 销毁时清理资源
        /// </summary>
        public void OnDestroy() // Added
        {
            Debug.Log("🗑️ LLMLogic 清理资源");
            
            try
            {
                // 取消注册事件监听
                if (RegisterCommand != null)
                {
                    RegisterCommand.UnRegister<VoiceRecognitionResultCommand>(VoiceRecognitionResultCommandCallback);
                    RegisterCommand.UnRegister<StopGenerateCommand>(StopGenerateCommandCallback);
                    
                    if (UseUnifiedService)
                    {
                        RegisterCommand.UnRegister<UnifiedLLMResultCommand>(OnUnifiedLLMResult);
                        RegisterCommand.UnRegister<UnifiedAudioStartCommand>(OnUnifiedAudioStart);
                        RegisterCommand.UnRegister<UnifiedAudioDataCommand>(OnUnifiedAudioData);
                        RegisterCommand.UnRegister<UnifiedAudioEndCommand>(OnUnifiedAudioEnd);
                        RegisterCommand.UnRegister<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged);
                    }
                }
                
                // 停止协程
                if (_titleSynchronization_Cor != null)
                {
                    _mono?.StopCoroutine(_titleSynchronization_Cor);
                }
                if (_requestGPTSegmentationCor != null)
                {
                    _mono?.StopCoroutine(_requestGPTSegmentationCor);
                }
                if (_unifiedAudioSyncCoroutine != null)
                {
                    _mono?.StopCoroutine(_unifiedAudioSyncCoroutine);
                }
                
                // 清理音频资源
                ClearGPTVoice();
                ClearUnifiedAudioQueue();
                
                // 🔧 清理音频数据缓冲区
                if (_pendingAudioChunks != null)
                {
                    _pendingAudioChunks.Clear();
                    Debug.Log("🗑️ 音频数据缓冲区已清理");
                }
                
                // 🔧 清理内存池
                try
                {
                    // 清理所有活跃的AudioClip
                    if (_activeAudioClips != null)
                    {
                        foreach (var clip in _activeAudioClips)
                        {
                            if (clip != null)
                            {
                                UnityEngine.Object.Destroy(clip);
                            }
                        }
                        _activeAudioClips.Clear();
                        Debug.Log($"🧹 清理了 {_activeAudioClips.Count} 个AudioClip");
                    }
                    
                    // 清理内存池
                    _audioDataPool?.Clear();
                    _floatArrayPool?.Clear();
                    _totalBytesAllocated = 0;
                    _audioClipCount = 0;
                    
                    Debug.Log("🧹 内存池已清理");
                    
                    // 强制垃圾回收
                    ForceGarbageCollection();
                }
                catch (Exception poolEx)
                {
                    Debug.LogWarning($"⚠️ 清理内存池时出现异常: {poolEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"⚠️ LLMLogic清理资源时出现异常: {ex.Message}");
            }
        }
        #endregion

        // 判定是否包含语音类字符（中/英文/数字）
        private bool ContainsSpeechLikeChars(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // CJK统一表意文字 + 任意字母 + 十进制数字
            return System.Text.RegularExpressions.Regex.IsMatch(
                text, @"[\p{IsCJKUnifiedIdeographs}\p{L}\p{Nd}]");
        }

        // 判定是否为"非语音"（纯emoji/标点/空白等）
        private bool IsNonSpeechText(string text) => !ContainsSpeechLikeChars(text);

        // 判定音频是否过短（阈值0.2秒，16k采样，16bit，单声道≈6400字节）
        private bool IsTooShortAudioBytes(int totalBytes, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16, float minSeconds = 0.3f) // 改为300ms
        {
            int bytesPerSecond = sampleRate * channels * (bitsPerSample / 8);
            return totalBytes < (int)(minSeconds * bytesPerSecond);
        }

        // 根据文本从队列移除未播放段（用于在end时丢弃）
        private void DropSegmentForText(string originalText, string dialogueText)
        {
            if (unifiedAudioQueue == null || unifiedAudioQueue.Count == 0) return;
            var list = new System.Collections.Generic.List<UnifiedAudioSegment>(unifiedAudioQueue);
            int idx = list.FindIndex(seg => seg != null && 
                (seg.originalText == originalText || seg.text == dialogueText));
            if (idx >= 0)
            {
                var seg = list[idx];
                if (seg.generatedClip != null) UnityEngine.Object.Destroy(seg.generatedClip);
                list.RemoveAt(idx);
                unifiedAudioQueue = new Queue<UnifiedAudioSegment>(list);
                Debug.Log($"🧹 丢弃非语音/过短音频段: '{originalText}'");
            }
        }

        /// <summary>
        /// 🔧 验证AudioClip是否适合uLipSync使用
        /// </summary>
        private bool ValidateAudioClipForLipSync(AudioClip clip)
        {
            try
            {
                if (clip == null || clip.length <= 0) return false;
                
                // 1. 检查采样率兼容性
                if (clip.frequency < 8000 || clip.frequency > 48000)
                {
                    Debug.LogWarning($"⚠️ 采样率 {clip.frequency}Hz 可能不兼容uLipSync");
                    return false;
                }
                
                // 2. 检查音频长度合理性（uLipSync要求）
                if (clip.length < 0.3f) // 最少300ms
                {
                    Debug.LogWarning($"⚠️ 音频过短: {clip.length:F3}秒，uLipSync需要至少300ms进行FFT分析");
                    return false;
                }
                
                if (clip.length > 30f) // 超过30秒
                {
                    Debug.LogWarning($"⚠️ 音频过长: {clip.length:F1}秒，可能影响实时处理");
                    return false;
                }
                
                // 3. 检查声道数
                if (clip.channels != 1)
                {
                    Debug.LogWarning($"⚠️ 多声道音频({clip.channels}声道)可能影响uLipSync");
                    return false;
                }
                
                // 4. 检查音频数据完整性
                float[] audioData = new float[clip.samples];
                clip.GetData(audioData, 0);
                
                // 检查是否全为静音
                float maxAmplitude = 0f;
                for (int i = 0; i < Math.Min(audioData.Length, 1000); i++)
                {
                    maxAmplitude = Math.Max(maxAmplitude, Math.Abs(audioData[i]));
                }
                
                if (maxAmplitude < 0.001f)
                {
                    Debug.LogWarning($"⚠️ 音频全为静音，不适合uLipSync");
                    return false;
                }
                
                // 5. 检查频谱特征（uLipSync关键要求）
                if (!HasValidSpectrumForLipSync(audioData))
                {
                    Debug.LogWarning($"⚠️ 音频频谱特征异常，可能影响uLipSync音素识别");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ AudioClip验证异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 🔧 检查音频是否具有uLipSync所需的频谱特征
        /// </summary>
        private bool HasValidSpectrumForLipSync(float[] audioData)
        {
            try
            {
                if (audioData == null || audioData.Length < 100) return false;
                
                // 1. 过零率检查（语音特征）
                int zeroCrossings = 0;
                for (int i = 1; i < audioData.Length; i++)
                {
                    if ((audioData[i] >= 0) != (audioData[i-1] >= 0))
                    {
                        zeroCrossings++;
                    }
                }
                
                float zeroCrossingRate = (float)zeroCrossings / audioData.Length;
                
                // 正常语音的过零率应该在合理范围内
                bool isValidZCR = zeroCrossingRate > 0.01f && zeroCrossingRate < 0.5f;
                
                // 2. 能量分布检查
                float totalEnergy = 0f;
                float lowFreqEnergy = 0f;
                
                for (int i = 0; i < audioData.Length; i++)
                {
                    float sample = audioData[i];
                    totalEnergy += sample * sample;
                    
                    // 低频能量（前1/3样本）
                    if (i < audioData.Length / 3)
                    {
                        lowFreqEnergy += sample * sample;
                    }
                }
                
                // 低频能量应该占总能量的一定比例
                float lowFreqRatio = lowFreqEnergy / Math.Max(totalEnergy, 1e-6f);
                bool isValidEnergy = lowFreqRatio > 0.1f && lowFreqRatio < 0.9f;
                
                bool isValid = isValidZCR && isValidEnergy;
                
                if (!isValid)
                {
                    Debug.LogWarning($"⚠️ 频谱特征异常: 过零率={zeroCrossingRate:F3}, 低频比例={lowFreqRatio:F3}");
                }
                
                return isValid;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 频谱特征检查异常: {ex}");
                return false;
            }
        }

        /// <summary>
        ///  优化音频后处理，保持uLipSync兼容性
        /// </summary>
        private float[] PostProcessAudioForLipSync(float[] audio)
        {
            try
            {
                if (audio == null || audio.Length == 0) return audio;

                // 1. 轻微的去直流，避免过度处理
                float mean = 0f;
                for (int i = 0; i < audio.Length; i++) mean += audio[i];
                mean /= audio.Length;
                
                // 只去除明显的直流偏移，保留频谱特征
                if (Mathf.Abs(mean) > 0.01f)
                {
                    for (int i = 0; i < audio.Length; i++) audio[i] -= mean;
                }

                // 2. 保守的增益控制，避免破坏频谱特征
                float maxAbs = 0f;
                for (int i = 0; i < audio.Length; i++)
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(audio[i]));
                }

                // 只在必要时调整增益，保持动态范围
                if (maxAbs > 0.9f)
                {
                    float scale = 0.85f / maxAbs;
                    for (int i = 0; i < audio.Length; i++) audio[i] *= scale;
                }

                // 3.  移除过度的淡入淡出，保持音频连续性
                // 注释掉：ApplyFadeInOut(audio, (int)(0.012f * 16000));
                // 改为：ApplyFadeInOut(audio, (int)(0.005f * AudioSettings.outputSampleRate)); // 减少到5ms

                return audio;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频后处理异常: {ex}");
                return audio;
            }
        }

        /// <summary>
        /// 🔧 扩展音频至最小长度，满足uLipSync要求
        /// </summary>
        private float[] ExtendAudioToMinimumLength(float[] audio, int sampleRate, float minDuration)
        {
            try
            {
                int minSamples = Mathf.RoundToInt(sampleRate * minDuration);
                
                if (audio.Length >= minSamples)
                {
                    return audio; // 已经满足要求
                }
                
                // 创建扩展后的数组
                float[] extendedAudio = new float[minSamples];
                
                // 复制原始音频
                Array.Copy(audio, extendedAudio, audio.Length);
                
                // 用静音填充剩余部分
                for (int i = audio.Length; i < minSamples; i++)
                {
                    extendedAudio[i] = 0f;
                }
                
                Debug.Log($" 音频已从 {audio.Length} 样本扩展至 {minSamples} 样本，满足uLipSync要求");
                return extendedAudio;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频扩展异常: {ex}");
                return audio; // 返回原始音频
            }
        }
    }
}
