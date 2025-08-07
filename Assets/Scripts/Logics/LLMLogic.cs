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
                    
                    // 发送开始对话命令
                    SendCommand.Send(new ChatGPTStartTalkCommand());
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

                if (command.isEnd)
                {
                    // LLM回复完成
                    isRequestChatGPTContent = true;
                    Debug.Log($"✅ LLM回复完成，总文本: '{currentLLMAccumulatedText}'");
                    
                    // 如果统一音频同步协程还没有启动，现在启动
                    if (_unifiedAudioSyncCoroutine == null && unifiedAudioQueue.Count > 0)
                    {
                        _unifiedAudioSyncCoroutine = _mono.StartCoroutine(UnifiedAudioSynchronizationCoroutine());
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
                
                if (string.IsNullOrEmpty(dialogueText))
                {
                    Debug.Log($"🎭 音频段全为动作描述，跳过字幕显示: '{command.text}'");
                    return;
                }

                Debug.Log($"🔊 音频开始: '{dialogueText}'");
                
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
                    _unifiedAudioSyncCoroutine = _mono.StartCoroutine(UnifiedAudioSynchronizationCoroutine());
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
                : $"{eventType}_{identifier}_{DateTime.Now.Ticks}";
            
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
                        if (segment.text == command.text && !segment.isComplete)
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
                // 转换音频数据
                float[] pcmData = ConvertBytesToFloats(combinedData);
                
                if (pcmData.Length > 0)
                {
                    // 音频质量后处理
                    pcmData = PostProcessAudio(pcmData);
                    
                    // 创建AudioClip
                    int sampleRate = 16000;
                    int channels = 1;
                    
                    segment.generatedClip = AudioClip.Create(
                        $"UnifiedAudio_{segment.text.GetHashCode()}_{DateTime.Now.Ticks}", 
                        pcmData.Length, channels, sampleRate, false);
                    
                    if (segment.generatedClip != null)
                    {
                        segment.generatedClip.SetData(pcmData, 0);
                        segment.estimatedDuration = (float)pcmData.Length / sampleRate;
                        
                        // 验证AudioClip创建成功
                        if (segment.generatedClip.length > 0)
                        {
                            // 注册AudioClip到内存管理系统
                            RegisterAudioClip(segment.generatedClip);
                            
                            Debug.Log($"✅ 生成AudioClip成功: '{segment.text.Substring(0, Math.Min(segment.text.Length, 20))}...', " +
                                     $"时长: {segment.estimatedDuration:F2}秒, 样本数: {pcmData.Length}");
                            
                            // 定期打印内存统计
                            if (_audioClipCount % 5 == 0)
                            {
                                LogMemoryStats();
                            }
                        }
                        else
                        {
                            Debug.LogError($"❌ AudioClip创建失败: 长度为0");
                            UnityEngine.Object.Destroy(segment.generatedClip);
                            segment.generatedClip = null;
                            SafeGenerateSilenceClipForSegment(segment, 0.5f);
                        }
                    }
                    else
                    {
                        Debug.LogError($"❌ AudioClip.Create返回null");
                        SafeGenerateSilenceClipForSegment(segment, 0.5f);
                    }
                }
                else
                {
                    Debug.LogWarning($"⚠️ 音频段 '{segment.text}' PCM数据转换失败，生成静音段");
                    SafeGenerateSilenceClipForSegment(segment, 1.0f);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 创建AudioClip总体异常: {ex}");
                RecordError("创建AudioClip异常", ex);
                
                // 清理可能的残留AudioClip
                if (segment.generatedClip != null)
                {
                    UnityEngine.Object.Destroy(segment.generatedClip);
                    segment.generatedClip = null;
                }
                
                // 生成备用静音段
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
                int sampleRate = 16000;
                int samples = Mathf.RoundToInt(sampleRate * duration);
                float[] silenceData = new float[samples]; // 默认全为0（静音）
                
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
        private float[] PostProcessAudio(float[] audioData)
        {
            try
            {
                if (audioData == null || audioData.Length == 0)
                    return audioData;

                // 🔧 音量归一化
                float maxAmplitude = 0f;
                for (int i = 0; i < audioData.Length; i++)
                {
                    maxAmplitude = Math.Max(maxAmplitude, Math.Abs(audioData[i]));
                }

                if (maxAmplitude > 0.001f && maxAmplitude < 0.5f)
                {
                    // 如果音量过低，进行适度放大
                    float gainFactor = 0.7f / maxAmplitude; // 放大到70%水平
                    gainFactor = Math.Min(gainFactor, 3.0f); // 限制最大放大倍数
                    
                    for (int i = 0; i < audioData.Length; i++)
                    {
                        audioData[i] *= gainFactor;
                        // 确保不会溢出
                        audioData[i] = Math.Max(-1.0f, Math.Min(1.0f, audioData[i]));
                    }
                    
                    Debug.Log($"🔧 音频音量调整: 放大 {gainFactor:F2} 倍");
                }

                return audioData;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 音频后处理异常: {ex}");
                return audioData; // 返回原始数据
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
        private IEnumerator UnifiedAudioSynchronizationCoroutine()
        {
            Debug.Log("🎵 统一服务音频同步协程启动");
            
            while (!isStopCreate && (unifiedAudioQueue.Count > 0 || !isRequestChatGPTContent))
            {
                if (unifiedAudioQueue.Count == 0)
                {
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                var segment = unifiedAudioQueue.Peek();
                
                // 🔧 等待音频段完全准备好
                if (!segment.isComplete || segment.generatedClip == null)
                {
                    yield return new WaitForSeconds(0.05f);
                    continue;
                }
                
                // 🔧 防重复播放：检查是否已经在播放中
                if (audioModel.IsPlaying && segment.isPlaying) // 修正：IsPlaying是属性
                {
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                // 移除并播放
                segment = unifiedAudioQueue.Dequeue();
                segment.isPlaying = true; // 标记正在播放
                
                float actualDuration = segment.generatedClip.length;
                
                Debug.Log($"🔊 播放音频段: '{segment.text.Substring(0, Math.Min(segment.text.Length, 30))}...', " +
                  $"实际时长: {actualDuration:F2}秒");
                
                audioModel.Play(segment.generatedClip);
                // 🔧 启动字幕协程
                _mono.StartCoroutine(PlaySubtitlesForSegment(segment, actualDuration));
                
                // 🔧 等待音频播放完成
                yield return new WaitForSeconds(actualDuration);
                
                segment.isPlaying = false; // 标记播放完成
            }
            
            Debug.Log("🎵 统一服务音频同步协程结束");
            
            // 🔧 播放完成后调用Finish，以重置状态并开启下一轮语音识别
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
    }
}
