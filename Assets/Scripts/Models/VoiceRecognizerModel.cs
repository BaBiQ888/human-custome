using LKZ.Commands.Voice;
using LKZ.Commands.UnifiedService;
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
using LKZ.UnifiedService;
using System;
using System.Collections;
using System.IO.Compression;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using UnityEngine;

namespace LKZ.Voice
{
    public sealed class VoiceRecognizerModel
    {
        // 移除独立的语音识别服务URL，改为使用统一服务
        // const string url= "ws://47.79.16.41:10199/recognition";

        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        [Inject]
        private UnifiedVoiceServiceManager unifiedServiceManager { get; set; }

        // 保留原有的命令结构用于兼容性，但内部逻辑改为从统一服务获取
        private VoiceRecognitionResultCommand voiceRecognitionResult = new VoiceRecognitionResultCommand();

        public VoiceRecognizerBase voiceRecognizer;

        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        public bool UseUnifiedService { get; set; } = true;
        public void Initialized()
        {
            Debug.Log("🎤 VoiceRecognizerModel初始化 - 统一服务模式");

            // 注册原有命令监听
            RegisterCommand.Register<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);

            // 注册统一服务事件监听
            RegisterCommand.Register<UnifiedASRResultCommand>(OnUnifiedASRResult);
            RegisterCommand.Register<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged);

            voiceRecognizer = CreateVoiceRecognizer();
            if (voiceRecognizer != null)
            {
                if (UseUnifiedService)
                {
                    // 统一服务模式：不连接独立的语音识别服务，改为音频转发模式
#if UNITY_WEBGL && !UNITY_EDITOR
                    // WebGL平台：使用带事件回调的初始化方法（方案1：回调转发机制）
                    if (voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
                    {
                        webglRecognizer.InitializedForUnifiedServiceWithCallbacks(
                            this._mono,
                            this.SendAudioToUnifiedService,
                            this.OnWebGLLLMResultCallback,
                            this.OnWebGLAudioStartCallback,
                            this.OnWebGLAudioEndCallback,
                            this.OnWebGLAudioDataCallback
                        );
                        Debug.Log("✅ 语音识别器初始化完成 - WebGL统一服务模式（带事件回调）");
                    }
                    else
#endif
                    {
                        // 其他平台：使用标准的统一服务初始化
                        voiceRecognizer.InitializedForUnifiedService(this._mono, this.SendAudioToUnifiedService);
                        Debug.Log("✅ 语音识别器初始化完成 - 统一服务模式");
                    }
                }
                else
                {
                    // 兼容模式：保留原有的独立语音识别服务
                    voiceRecognizer.Initialized(this._mono, "ws://47.79.16.41:10199/recognition", this.DisponseRecognition);
                    Debug.Log("✅ 语音识别器初始化完成 - 独立服务模式");
                }
            }
            else
            {
                Debug.LogWarning("⚠️ 当前平台不支持语音识别功能");
            }
        }

        private VoiceRecognizerBase CreateVoiceRecognizer()
        {
#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_ANDROID
            return new VoiceRecognizerNoWebGL();
#elif UNITY_WEBGL && !UNITY_EDITOR
            return new VoiceRecognizerWebGL();
#else
            return null;
#endif
        }
         
        /// <summary>
        /// ��������ʶ������ص�
        /// </summary>
        /// <param name="obj"></param>
        private void SettingVoiceRecognitionCommandCallback(SettingVoiceRecognitionCommand obj)
        {
            voiceRecognizer?.SetIsRecogition(obj.IsStartVoiceRecognition);
            
            if (UseUnifiedService)
            {
                Debug.Log($"🎤 统一服务模式 - 语音识别状态: {(obj.IsStartVoiceRecognition ? "开启" : "关闭")}");
                
                if (obj.IsStartVoiceRecognition && !unifiedServiceManager.IsConversationActive)
                {
                    Debug.LogWarning("⚠️ 统一服务未激活，无法开始语音识别");
                }
            }
        }

        /// <summary>
        /// 音频数据转发到统一服务
        /// </summary>
        /// <param name="audioData">音频数据</param>
        private void SendAudioToUnifiedService(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return;

            if (!unifiedServiceManager.IsConnected)
            {
                Debug.LogWarning("⚠️ 统一服务未连接，无法发送音频数据");
                return;
            }

            // 通过命令系统发送音频数据
            SendCommand.Send(new SendAudioDataCommand
            {
                audioData = audioData
            });

            // 可选：添加调试日志（生产环境可注释）
            // Debug.Log($"📡 音频数据已转发到统一服务: {audioData.Length} bytes");
        }

        /// <summary>
        /// 处理统一服务的ASR识别结果
        /// </summary>
        /// <param name="command">ASR结果命令</param>
        private void OnUnifiedASRResult(UnifiedASRResultCommand command)
        {
            // 将统一服务的ASR结果转换为原有的格式，保持兼容性
            voiceRecognitionResult.text = command.text;
            voiceRecognitionResult.IsComplete = command.isFinal;

            Debug.Log($"🎤 统一服务ASR结果: '{command.text}' (最终: {command.isFinal})");

            // 发送原有的语音识别结果命令，保持与其他模块的兼容
            SendCommand.Send(new VoiceRecognitionResultCommand
            {
                text = command.text,
                IsComplete = command.isFinal
            });
        }

        /// <summary>
        /// 处理统一服务状态变化
        /// </summary>
        /// <param name="command">状态变化命令</param>
        private void OnUnifiedServiceStateChanged(UnifiedServiceStateChangedCommand command)
        {
            Debug.Log($"🔄 统一服务状态变化: {command.previousState} → {command.currentState}");

            // 根据服务状态调整语音识别行为
            switch (command.currentState)
            {
                case UnifiedServiceState.ConversationActive:
                    Debug.Log("✅ 统一服务对话激活，语音识别已就绪");
                    break;
                case UnifiedServiceState.Disconnected:
                case UnifiedServiceState.Error:
                    // 服务断开时停止语音识别
                    voiceRecognizer?.SetIsRecogition(false);
                    Debug.LogWarning("⚠️ 统一服务断开，已停止语音识别");
                    break;
            }
        }

        #region WebGL回调处理方法（方案1：回调转发机制）
        
        /// <summary>
        /// 处理来自WebGL的LLM结果回调
        /// </summary>
        private void OnWebGLLLMResultCallback(string llmData)
        {
            try
            {
                Debug.Log($"📨 VoiceRecognizerModel收到LLM结果回调: {llmData}");
                
                // 解析JSON数据
                var data = JsonUtility.FromJson<LLMResultData>(llmData);
                
                // 发送UnifiedLLMResultCommand
                SendCommand.Send(new UnifiedLLMResultCommand
                {
                    text = data.text,
                    isFirst = data.is_first,
                    isEnd = data.is_end
                });
                
                Debug.Log($"✅ 成功发送UnifiedLLMResultCommand: '{data.text}' (首次: {data.is_first}, 结束: {data.is_end})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理LLM结果回调异常: {ex.Message}\n原始数据: {llmData}");
            }
        }

        /// <summary>
        /// 处理来自WebGL的音频开始回调
        /// </summary>
        private void OnWebGLAudioStartCallback(string audioStartData)
        {
            try
            {
                Debug.Log($"📨 VoiceRecognizerModel收到音频开始回调: {audioStartData}");
                
                // 解析JSON数据
                var data = JsonUtility.FromJson<AudioStartData>(audioStartData);
                
                // 发送UnifiedAudioStartCommand
                SendCommand.Send(new UnifiedAudioStartCommand
                {
                    text = data.text,
                    fileName = data.file_name ?? ""
                });
                
                Debug.Log($"✅ 成功发送UnifiedAudioStartCommand: '{data.text}' (文件: {data.file_name})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频开始回调异常: {ex.Message}\n原始数据: {audioStartData}");
            }
        }

        /// <summary>
        /// 处理来自WebGL的音频结束回调
        /// </summary>
        private void OnWebGLAudioEndCallback(string audioEndData)
        {
            try
            {
                Debug.Log($"📨 VoiceRecognizerModel收到音频结束回调: {audioEndData}");
                
                // 解析JSON数据
                var data = JsonUtility.FromJson<AudioEndData>(audioEndData);
                
                // 发送UnifiedAudioEndCommand
                SendCommand.Send(new UnifiedAudioEndCommand
                {
                    text = data.text,
                    totalSize = data.total_size
                });
                
                Debug.Log($"✅ 成功发送UnifiedAudioEndCommand: '{data.text}' (总大小: {data.total_size} bytes)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频结束回调异常: {ex.Message}\n原始数据: {audioEndData}");
            }
        }

        /// <summary>
        /// 处理来自WebGL的音频数据回调
        /// </summary>
        private void OnWebGLAudioDataCallback(byte[] audioData)
        {
            try
            {
                Debug.Log($"📨 VoiceRecognizerModel收到音频数据回调: {audioData.Length} bytes");
                
                // 发送UnifiedAudioDataCommand
                SendCommand.Send(new UnifiedAudioDataCommand
                {
                    audioData = audioData,
                    associatedText = "" // WebGL层可能无法提供具体的关联文本
                });
                
                Debug.Log($"✅ 成功发送UnifiedAudioDataCommand: {audioData.Length} bytes");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频数据回调异常: {ex.Message}");
            }
        }
        
        #endregion

        /// <summary>
        /// ����ʶ����
        /// </summary>
        /// <param name="count"></param>
        private void DisponseRecognition(string text1)
        {
            if (text1 == " N" || text1 == "N" || text1 == "A" || text1 == " A")
                return;

            if (!string.IsNullOrEmpty(text1))
            {
                try
                {
                    // 尝试解析JSON格式的识别结果
                    if (text1.StartsWith("{") && text1.Contains("\"text\""))
                    {
                        var resultData = JsonUtility.FromJson<VoiceRecognitionResultData>(text1);

                        Debug.Log($"解析语音识别结果 - 文本: '{resultData.text}', 是否最终: {resultData.is_final}");

                        voiceRecognitionResult.text = resultData.text;
                        voiceRecognitionResult.IsComplete = resultData.is_final;

                        if (resultData.is_final)
                        {
                            Debug.Log($"收到最终语音识别结果: '{resultData.text}'");
                        }
                        else
                        {
                            Debug.Log($"收到中间语音识别结果: '{resultData.text}'");
                        }
                    }
                    else
                    {
                        // 兼容原有的纯文本格式
                        Debug.Log($"收到纯文本语音识别结果: '{text1}'");

                        if (text1 == "\n")
                        {
                            voiceRecognitionResult.IsComplete = true;
                            voiceRecognitionResult.text = string.Empty;
                        }
                        else
                        {
                            voiceRecognitionResult.IsComplete = false;
                            voiceRecognitionResult.text = text1;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"解析语音识别结果失败: {ex.Message}, 原始数据: '{text1}'");

                    // 解析失败时按纯文本处理
                    voiceRecognitionResult.IsComplete = false;
                    voiceRecognitionResult.text = text1;
                }

                SendCommand.Send(voiceRecognitionResult);
            }
        }

        public void OnDestroy()
        {
            Debug.Log("🗑️ VoiceRecognizerModel 清理资源");
            
            // 取消注册事件监听
            if (RegisterCommand != null)
            {
                try
                {
                    RegisterCommand.UnRegister<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);
                    
                    if (UseUnifiedService)
                    {
                        RegisterCommand.UnRegister<UnifiedASRResultCommand>(OnUnifiedASRResult);
                        RegisterCommand.UnRegister<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged);
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"⚠️ 取消注册命令监听时出现异常: {ex.Message}");
                }
            }
            
            // 清理语音识别器资源
            voiceRecognizer?.SetIsRecogition(false);
        }
    }


    public abstract class VoiceRecognizerBase
    {
        /// <summary>
        /// 传统模式初始化（连接独立语音识别服务）
        /// </summary>
        public abstract void Initialized(MonoBehaviour _mono,string websocketUrl, Action<string> _recognizerCallback);

        /// <summary>
        /// 统一服务模式初始化（音频转发模式）
        /// </summary>
        public virtual void InitializedForUnifiedService(MonoBehaviour _mono, Action<byte[]> audioCallback)
        {
            // 默认实现为兼容模式，子类需要重写
            Debug.LogWarning("⚠️ 当前平台暂不支持统一服务模式，使用兼容模式");
        }

        public abstract void SetIsRecogition(bool IsRecogition);
    } 

#if UNITY_EDITOR ||UNITY_STANDALONE || UNITY_ANDROID

    public sealed class VoiceRecognizerNoWebGL : VoiceRecognizerBase
    {
        ClientWebSocket webSocket;
        AudioClip microphoneClip;

        /// <summary>
        /// 采样间隔时间
        /// </summary>
        WaitForSeconds samplingInterval = new WaitForSeconds(1 / 5f);

        private MonoBehaviour _mono;

        // 传统模式的回调
        private Action<string> recognizerCallback;
        
        // 统一服务模式的音频回调
        private Action<byte[]> audioCallback;
        
        private bool IsRecogition;
        
        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        private bool isUnifiedServiceMode = false;

        public override async void Initialized(MonoBehaviour _mono, string websocketUrl, Action<string> _recognizerCallback)
        {
            this._mono = _mono;
            recognizerCallback = _recognizerCallback;
            isUnifiedServiceMode = false;

            Debug.Log($"🔗 VoiceRecognizerNoWebGL 传统模式初始化: {websocketUrl}");

            webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(websocketUrl), default);

            _mono.StartCoroutine(InitializedMicrophone());
            byte[] p = new byte[1024 * 1024];
            int count = 0;
            while (true)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(p, count, p.Length - count), default);
                count += result.Count;
                if (result.EndOfMessage)
                {
                    var str = Encoding.UTF8.GetString(p, 0, count);
                    recognizerCallback?.Invoke(str);
                    count = 0;
                }
            }
        }

        /// <summary>
        /// 统一服务模式初始化
        /// </summary>
        public override void InitializedForUnifiedService(MonoBehaviour _mono, Action<byte[]> audioCallback)
        {
            this._mono = _mono;
            this.audioCallback = audioCallback;
            isUnifiedServiceMode = true;

            Debug.Log("🎤 VoiceRecognizerNoWebGL 统一服务模式初始化");

            // 只初始化麦克风，不连接WebSocket
            _mono.StartCoroutine(InitializedMicrophone());
        }

        public override void SetIsRecogition(bool IsRecogition)
        {
            this.IsRecogition = IsRecogition;
            if (IsRecogition)
                this.lastSampling = Microphone.GetPosition(null);

        }

        IEnumerator InitializedMicrophone()
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                do
                {
                    microphoneClip = Microphone.Start(null, true, 1, 16000);
                    yield return null;
                } while (!Microphone.IsRecording(null));

                _mono.StartCoroutine(MicrophoneSamplingRecognition());
            }
            else
            {
                Debug.Log("����Ȩ��˷�Ȩ�ޣ�");
            }
        }


        /// <summary>
        /// ��һ�β���λ��
        /// </summary>
        int lastSampling;

        float[] f = new float[16000];
        IEnumerator MicrophoneSamplingRecognition()
        {
            while (true)
            {
                yield return samplingInterval;
                if (!IsRecogition)
                    continue;

                int currentPos = Microphone.GetPosition(null);
                bool isSucceed = microphoneClip.GetData(f, 0);

                if (isSucceed)
                    if (lastSampling != currentPos)
                    {
                        int count = 0;
                        float[] p = default;
                        if (currentPos > lastSampling)
                        {
                            count = currentPos - lastSampling;
                            p = new float[count]; 
                            Array.Copy(f, lastSampling, p, 0, count);
                        }
                        else
                        {
                            count = 16000 - lastSampling;
                            p = new float[count + currentPos]; 
                            Array.Copy(f, lastSampling, p, 0, count);
                             
                            Array.Copy(f, 0, p, count, currentPos);

                            count += currentPos;
                        }

                        lastSampling = currentPos;
                        DisponseRecognition(p);
                    }

            }
        }

        private void DisponseRecognition(float[] p)
        {
            var buffer = FloatArrayToByteArray(p);

            if (isUnifiedServiceMode)
            {
                // 统一服务模式：通过回调转发音频数据
                audioCallback?.Invoke(buffer);
            }
            else
            {
                // 传统模式：直接发送到独立的语音识别服务
                this.webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, default);
            }
        }


        byte[] FloatArrayToByteArray(in float[] floatArray)
        {
            int byteCount = floatArray.Length * sizeof(float);
            byte[] byteArray = new byte[byteCount];

            Buffer.BlockCopy(floatArray, 0, byteArray, 0, byteCount);

            return byteArray;
        }

        static byte[] Compress(in byte[] data)
        {
            using (MemoryStream compressedStream = new MemoryStream())
            {
                using (GZipStream gzipStream = new GZipStream(compressedStream, CompressionMode.Compress))
                {
                    gzipStream.Write(data, 0, data.Length);
                }
                return compressedStream.ToArray();
            }
        }
    }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
    public sealed class VoiceRecognizerWebGL : VoiceRecognizerBase
    {
        private MonoBehaviour _mono;
        
        // 传统模式的回调
        private Action<string> recognizerCallback;
        
        // 统一服务模式的音频回调
        private Action<byte[]> audioCallback;
        
        private bool IsRecogition;
        private string websocketUrl;

        // 会话状态管理
        private bool permissionRequested = false;
        private bool sessionActive = false;
        
        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        private bool isUnifiedServiceMode = false;

        // 统一服务事件回调委托（方案1：回调转发机制）
        private Action<string> onLLMResultCallback;
        private Action<string> onAudioStartCallback;
        private Action<string> onAudioEndCallback;
        private Action<byte[]> onAudioDataCallback;

        public override void Initialized(MonoBehaviour _mono, string websocketUrl, Action<string> _recognizerCallback)
        {
            this._mono = _mono;
            this.websocketUrl = websocketUrl;
            this.recognizerCallback = _recognizerCallback;
            isUnifiedServiceMode = false;

            Debug.Log($"🔗 VoiceRecognizerWebGL 传统模式初始化: {websocketUrl}");

            // 初始化WebGL WebSocket连接
            InitializeWebGLWebSocket(websocketUrl);

            // 初始化WebGL麦克风（延迟模式，不立即请求权限）
            InitializeWebGLMicrophone();
        }

        /// <summary>
        /// 统一服务模式初始化
        /// </summary>
        public override void InitializedForUnifiedService(MonoBehaviour _mono, Action<byte[]> audioCallback)
        {
            this._mono = _mono;
            this.audioCallback = audioCallback;
            isUnifiedServiceMode = true;

            Debug.Log("🎤 VoiceRecognizerWebGL 统一服务模式初始化");

            // 统一服务模式：不初始化独立的WebSocket连接，只初始化麦克风
            // 音频数据将通过audioCallback转发到统一服务
            InitializeWebGLMicrophone();
        }

        /// <summary>
        /// 统一服务模式初始化（带事件回调）
        /// </summary>
        public void InitializedForUnifiedServiceWithCallbacks(
            MonoBehaviour _mono, 
            Action<byte[]> _audioCallback,
            Action<string> _onLLMResultCallback,
            Action<string> _onAudioStartCallback,
            Action<string> _onAudioEndCallback,
            Action<byte[]> _onAudioDataCallback)
        {
            this._mono = _mono;
            this.audioCallback = _audioCallback;
            this.onLLMResultCallback = _onLLMResultCallback;
            this.onAudioStartCallback = _onAudioStartCallback;
            this.onAudioEndCallback = _onAudioEndCallback;
            this.onAudioDataCallback = _onAudioDataCallback;
            isUnifiedServiceMode = true;

            Debug.Log("🔗 VoiceRecognizerWebGL 统一服务模式（带事件回调）初始化完成");

            // 统一服务模式：不初始化独立的WebSocket连接，只初始化麦克风
            InitializeWebGLMicrophone();
        }

        public override void SetIsRecogition(bool IsRecogition)
        {
            this.IsRecogition = IsRecogition;
            Debug.Log($"WebGL语音识别状态请求: {IsRecogition}");

            if (IsRecogition && !permissionRequested)
            {
                // 第一次请求开始识别时，请求麦克风权限
                Debug.Log("首次请求语音识别，开始请求麦克风权限");
                RequestMicrophonePermission();
                permissionRequested = true;
            }
            else
            {
                // 后续的开始/停止请求通过JavaScript处理
                SetWebGLRecording(IsRecogition);
            }
        }

        // JavaScript插件接口 - WebSocket连接
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void InitializeWebGLWebSocket(string url);

        // JavaScript插件接口 - 麦克风初始化（延迟模式）
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void InitializeWebGLMicrophone();

        // JavaScript插件接口 - 请求麦克风权限
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void RequestWebGLMicrophonePermission();

        // JavaScript插件接口 - 录音控制
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void SetWebGLRecording(bool isRecording);

        // 请求麦克风权限
        private void RequestMicrophonePermission()
        {
            Debug.Log("请求WebGL麦克风权限");
            RequestWebGLMicrophonePermission();
        }

        // 供JavaScript回调的方法 - 识别结果
        public void OnWebGLRecognitionResult(string result)
        {
            Debug.Log($"WebGL语音识别结果: {result}");
            
            if (isUnifiedServiceMode)
            {
                // 统一服务模式：这个回调不应该被调用，因为识别结果从统一服务获取
                Debug.LogWarning("⚠️ 统一服务模式下收到WebGL识别结果，可能配置有误");
            }
            else
            {
                // 传统模式：正常处理识别结果
                recognizerCallback?.Invoke(result);
            }
        }

        // 供JavaScript回调的方法 - 音频数据（统一服务模式）
        public void OnWebGLAudioData(string base64AudioData) // Modified for WebGL compatibility
        {
            try
            {
                if (isUnifiedServiceMode)
                {
                    // 将Base64数据转换为字节数组
                    byte[] audioData = System.Convert.FromBase64String(base64AudioData);
                    
                    // 通过回调转发给主模块处理
                    onAudioDataCallback?.Invoke(audioData);
                    
                    // 保持原有回调机制（兼容性）
                    audioCallback?.Invoke(audioData);
                    
                    Debug.Log($"📡 收到音频数据: {audioData.Length} bytes，已转发到主模块");
                }
                else
                {
                    Debug.LogWarning("⚠️ 传统模式下收到音频数据回调，可能配置有误");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ WebGL音频数据处理异常: {ex}");
            }
        }

        // 统一服务WebGL回调方法
        public void OnWebGLUnifiedServiceConnected(string data) // Added
        {
            Debug.Log("🔗 WebGL统一服务连接成功");
        }

        public void OnWebGLUnifiedServiceDisconnected(string data) // Added
        {
            Debug.Log("🔌 WebGL统一服务连接断开");
        }

        public void OnWebGLUnifiedServiceError(string errorMessage) // Added
        {
            Debug.LogError($"❌ WebGL统一服务错误: {errorMessage}");
        }

        public void OnWebGLLLMResult(string llmData) // Added
        {
            Debug.Log($"🤖 WebGL收到LLM结果: {llmData}");
            onLLMResultCallback?.Invoke(llmData);
        }

        public void OnWebGLAudioStart(string audioStartData) // Added
        {
            Debug.Log($"🔊 WebGL音频开始: {audioStartData}");
            onAudioStartCallback?.Invoke(audioStartData);
        }

        public void OnWebGLAudioEnd(string audioEndData) // Added
        {
            Debug.Log($"✅ WebGL音频结束: {audioEndData}");
            onAudioEndCallback?.Invoke(audioEndData);
        }

        public void OnWebGLUnifiedServiceStatus(string statusData) // Added
        {
            Debug.Log($"📊 WebGL统一服务状态: {statusData}");
        }

        // 供JavaScript回调的方法 - 连接状态
        public void OnWebGLConnectionStatus(string status)
        {
            Debug.Log($"WebGL状态变化: {status}");

            switch (status)
            {
                case "connected":
                    Debug.Log("WebSocket连接成功");
                    break;
                case "requesting_permission":
                    Debug.Log("正在请求麦克风权限...");
                    break;
                case "permission_granted":
                    Debug.Log("麦克风权限获取成功");
                    break;
                case "session_active":
                    Debug.Log("语音识别会话已激活");
                    sessionActive = true;
                    break;
                case "waiting_result":
                    Debug.Log("用户停止说话，等待识别结果");
                    break;
                case "result_timeout":
                    Debug.Log("识别结果超时，重置音频发送状态");
                    break;
                case "recognition_complete":
                    Debug.Log("语音识别周期完成");
                    sessionActive = false;
                    break;
                case "disconnected":
                    Debug.Log("WebSocket连接断开");
                    sessionActive = false;
                    break;
            }
        }

        // 供JavaScript回调的方法 - 错误处理
        public void OnWebGLError(string error)
        {
            Debug.LogError($"WebGL语音识别错误: {error}");

            // 根据错误类型进行处理
            if (error.Contains("权限被拒绝") || error.Contains("权限"))
            {
                Debug.LogWarning("麦克风权限被拒绝，请在浏览器中允许麦克风访问");
                // 可以在这里触发UI提示用户重新授权
            }
            else if (error.Contains("WebSocket"))
            {
                Debug.LogWarning("WebSocket连接错误，尝试重连");
                // 可以在这里实现重连逻辑
            }
        }
    }
#endif

    /// <summary>
    /// 语音识别结果数据结构（用于JSON解析）
    /// </summary>
    [System.Serializable]
    public class VoiceRecognitionResultData
    {
        public string text;
        public bool is_final;
        public long timestamp;
    }

    /// <summary>
    /// LLM结果数据结构（用于JSON解析）
    /// </summary>
    [System.Serializable]
    public class LLMResultData
    {
        public string text;
        public bool is_first;
        public bool is_end;
        public long timestamp;
    }

    /// <summary>
    /// 音频开始数据结构（用于JSON解析）
    /// </summary>
    [System.Serializable]
    public class AudioStartData
    {
        public string text;
        public string file_name;
        public long timestamp;
    }

    /// <summary>
    /// 音频结束数据结构（用于JSON解析）
    /// </summary>
    [System.Serializable]
    public class AudioEndData
    {
        public string text;
        public int total_size;
        public long timestamp;
    }
}