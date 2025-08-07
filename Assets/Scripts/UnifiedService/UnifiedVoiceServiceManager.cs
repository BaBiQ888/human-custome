using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using UnityEngine;
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
using LKZ.Commands.UnifiedService;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LKZ.Commands.Chat;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// 统一语音服务管理器
    /// 负责管理与一体化ASR+LLM+TTS服务的WebSocket连接
    /// </summary>
    public class UnifiedVoiceServiceManager : IDRegisterBindingInterface
    {
        #region 依赖注入属性
        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }
        #endregion

        #region 配置参数
        /// <summary>
        /// 服务器URL
        /// </summary>
        public string ServerUrl { get; set; } = "ws://127.0.0.1:10004";

        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; } = "UnityUser";

        /// <summary>
        /// ASR模式
        /// </summary>
        public string ASRMode { get; set; } = "ali";

        /// <summary>
        /// 自动重连
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// 重连间隔（秒）
        /// </summary>
        public float ReconnectInterval { get; set; } = 3.0f;

        /// <summary>
        /// 最大重连次数
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 5;
        #endregion

        #region 状态管理
        /// <summary>
        /// 当前服务状态
        /// </summary>
        public UnifiedServiceState CurrentState { get; private set; } = UnifiedServiceState.Disconnected;

        /// <summary>
        /// 连接状态变化回调
        /// </summary>
        public event Action<UnifiedServiceState, UnifiedServiceState> OnStateChanged;

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => webSocketAdapter?.IsConnected == true && (CurrentState == UnifiedServiceState.Connected || CurrentState == UnifiedServiceState.ConversationActive);

        /// <summary>
        /// 对话是否活跃
        /// </summary>
        public bool IsConversationActive => CurrentState == UnifiedServiceState.ConversationActive;
        #endregion

        #region WebSocket相关
        private IWebSocketAdapter webSocketAdapter;
        private readonly object webSocketLock = new object();

        /// <summary>
        /// 当前对话ID
        /// </summary>
        private string currentConversationId;

        /// <summary>
        /// 重连尝试次数
        /// </summary>
        private int reconnectAttempts = 0;
        #endregion

        #region 音频缓冲管理
        /// <summary>
        /// 音频接收缓冲队列
        /// </summary>
        private readonly Queue<byte[]> audioReceiveBuffer = new Queue<byte[]>();

        /// <summary>
        /// 当前音频接收上下文
        /// </summary>
        private AudioReceiveContext currentAudioContext;

        /// <summary>
        /// 音频缓冲锁
        /// </summary>
        private readonly object audioBufferLock = new object();
        #endregion

        #region 协程管理
        private Coroutine connectCoroutine;
        private Coroutine messageReceiveCoroutine;
        private Coroutine reconnectCoroutine;
        #endregion

        #region 依赖注入接口实现
        public void DIRegisterBinding(IRegisterBinding registerBinding)
        {
            registerBinding.Binding<UnifiedVoiceServiceManager>().To(this);
        }
        #endregion

        #region 初始化
        /// <summary>
        /// 初始化统一服务管理器
        /// </summary>
        public void Initialize()
        {
            Debug.Log($"🔧 统一语音服务管理器初始化 - 平台: {WebSocketAdapterFactory.GetPlatformName()}");

            // 注册命令监听
            RegisterCommands();

            // 初始化音频上下文
            currentAudioContext = new AudioReceiveContext();

            // 创建平台适配的WebSocket适配器
            InitializeWebSocketAdapter();

            Debug.Log("✅ 统一语音服务管理器初始化完成");
        }

        /// <summary>
        /// 初始化WebSocket适配器
        /// </summary>
        private void InitializeWebSocketAdapter()
        {
            try
            {
                // 创建适合当前平台的WebSocket适配器
                webSocketAdapter = WebSocketAdapterFactory.CreateAdapter(_mono);

                // 注册事件监听
                webSocketAdapter.OnConnectionChanged += OnWebSocketConnectionChanged;
                webSocketAdapter.OnTextMessageReceived += OnWebSocketTextMessage;
                webSocketAdapter.OnBinaryMessageReceived += OnWebSocketBinaryMessage;
                webSocketAdapter.OnError += OnWebSocketError;

                Debug.Log($"✅ WebSocket适配器初始化完成: {WebSocketAdapterFactory.GetPlatformName()}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ WebSocket适配器初始化失败: {ex}");
            }
        }

        /// <summary>
        /// 注册命令监听
        /// </summary>
        private void RegisterCommands()
        {
            RegisterCommand.Register<ConnectUnifiedServiceCommand>(OnConnectCommand);
            RegisterCommand.Register<DisconnectUnifiedServiceCommand>(OnDisconnectCommand);
            RegisterCommand.Register<SendAudioDataCommand>(OnSendAudioDataCommand);
            RegisterCommand.Register<SendTextDirectlyCommand>(OnSendTextDirectlyCommand);
            RegisterCommand.Register<GenerateFinishCommand>(OnGenerateFinish); // 🔧 [新增] 监听生成结束命令
        }
        #endregion

        #region 连接管理
        /// <summary>
        /// 连接到统一服务
        /// </summary>
        public void Connect(string serverUrl = null, string username = null, string asrMode = null)
        {
            if (!string.IsNullOrEmpty(serverUrl)) ServerUrl = serverUrl;
            if (!string.IsNullOrEmpty(username)) Username = username;
            if (!string.IsNullOrEmpty(asrMode)) ASRMode = asrMode;

            if (IsConnected)
            {
                Debug.LogWarning("⚠️ 服务已连接，无需重复连接");
                return;
            }

            if (webSocketAdapter == null)
            {
                Debug.LogError("❌ WebSocket适配器未初始化");
                HandleConnectionError("WebSocket适配器未初始化");
                return;
            }

            Debug.Log($"🔗 开始连接统一服务: {ServerUrl}");
            ChangeState(UnifiedServiceState.Connecting);

            if (connectCoroutine != null)
            {
                _mono.StopCoroutine(connectCoroutine);
            }

            connectCoroutine = _mono.StartCoroutine(ConnectCoroutine());
        }

        /// <summary>
        /// 连接协程
        /// </summary>
        private IEnumerator ConnectCoroutine()
        {
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return _mono.StartCoroutine(ExecuteConnectOperation());
        }

        /// <summary>
        /// 执行连接操作的内部协程
        /// </summary>
        private IEnumerator ExecuteConnectOperation()
        {
            IEnumerator connectOperation = null;
            Exception caughtException = null;

            try
            {
                // 使用WebSocket适配器进行连接
                connectOperation = webSocketAdapter.ConnectAsync(ServerUrl);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建连接操作时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ 连接异常: {caughtException}");
                HandleConnectionError(caughtException.Message);
                yield break;
            }

            // 执行连接操作（在try-catch外部使用yield return）
            yield return connectOperation;

            // 连接成功的处理将在OnWebSocketConnectionChanged回调中进行
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            Debug.Log("🔌 断开统一服务连接");
            
            ChangeState(UnifiedServiceState.Disconnected);
            
            StopAllCoroutines();
            
            if (webSocketAdapter != null)
            {
                webSocketAdapter.Disconnect();
            }
        }

        #region WebSocket事件处理

        /// <summary>
        /// WebSocket连接状态变化处理
        /// </summary>
        private void OnWebSocketConnectionChanged(bool isConnected)
        {
            Debug.Log($"📡 WebSocket连接状态变化: {isConnected}");

            if (isConnected)
            {
                Debug.Log("✅ WebSocket连接成功");
                ChangeState(UnifiedServiceState.Connected);
                reconnectAttempts = 0;

                // 自动开始对话
                _mono.StartCoroutine(StartConversationCoroutine());
            }
            else
            {
                Debug.Log("📪 WebSocket连接断开");
                if (CurrentState != UnifiedServiceState.Disconnected)
                {
                    ChangeState(UnifiedServiceState.Disconnected);
                }
            }
        }

        /// <summary>
        /// WebSocket文本消息处理
        /// </summary>
        private void OnWebSocketTextMessage(string message)
        {
            try
            {
                _mono.StartCoroutine(ProcessJsonMessage(message));
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理WebSocket文本消息异常: {ex}");
            }
        }

        /// <summary>
        /// WebSocket二进制消息处理
        /// </summary>
        private void OnWebSocketBinaryMessage(byte[] data)
        {
            try
            {
                ProcessAudioData(data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理WebSocket二进制消息异常: {ex}");
            }
        }

        /// <summary>
        /// WebSocket错误处理
        /// </summary>
        private void OnWebSocketError(string error)
        {
            Debug.LogError($"❌ WebSocket错误: {error}");
            HandleConnectionError(error);
        }

        #endregion

        /// <summary>
        /// 处理连接错误
        /// </summary>
        private void HandleConnectionError(string errorMessage)
        {
            ChangeState(UnifiedServiceState.Error);
            
            SendCommand.Send(new UnifiedServiceErrorCommand
            {
                errorMessage = errorMessage,
                exception = null
            });

            if (AutoReconnect && reconnectAttempts < MaxReconnectAttempts)
            {
                StartReconnect();
            }
        }

        /// <summary>
        /// 开始重连
        /// </summary>
        private void StartReconnect()
        {
            if (reconnectCoroutine != null)
            {
                _mono.StopCoroutine(reconnectCoroutine);
            }

            reconnectCoroutine = _mono.StartCoroutine(ReconnectCoroutine());
        }

        /// <summary>
        /// 重连协程
        /// </summary>
        private IEnumerator ReconnectCoroutine()
        {
            reconnectAttempts++;
            ChangeState(UnifiedServiceState.Reconnecting);
            
            Debug.Log($"🔄 开始重连 ({reconnectAttempts}/{MaxReconnectAttempts})");
            
            yield return new WaitForSeconds(ReconnectInterval);
            
            Connect();
        }
        #endregion

        #region 状态管理
        /// <summary>
        /// 改变服务状态
        /// </summary>
        private void ChangeState(UnifiedServiceState newState)
        {
            var previousState = CurrentState;
            CurrentState = newState;

            Debug.Log($"🔄 状态变化: {previousState} → {newState}");

            OnStateChanged?.Invoke(previousState, newState);
            
            SendCommand.Send(new UnifiedServiceStateChangedCommand
            {
                previousState = previousState,
                currentState = newState,
                message = $"状态从 {previousState} 变更为 {newState}"
            });
        }
        #endregion

        #region 命令处理
        /// <summary>
        /// 处理连接命令
        /// </summary>
        private void OnConnectCommand(ConnectUnifiedServiceCommand command)
        {
            Connect(command.serverUrl, command.username, command.asrMode);
        }

        /// <summary>
        /// 处理断开连接命令
        /// </summary>
        private void OnDisconnectCommand(DisconnectUnifiedServiceCommand command)
        {
            Disconnect();
        }

        /// <summary>
        /// 处理发送音频数据命令
        /// </summary>
        private void OnSendAudioDataCommand(SendAudioDataCommand command)
        {
            // 🔧 [修复] 核心逻辑修改：根据状态决定是直接发送音频还是开启新对话
            if (CurrentState == UnifiedServiceState.ConversationActive)
            {
                // 如果对话已激活，直接发送音频
                SendAudioData(command.audioData);
            }
            else if (CurrentState == UnifiedServiceState.Connected)
            {
                // 如果只是已连接（空闲），则这次音频输入被视为开启新对话的信号
                Debug.Log("🎤 检测到音频输入，自动开启新对话...");
                StartConversationAndSendAudio(command.audioData);
            }
            else
            {
                Debug.LogWarning($"⚠️ 当前状态为 {CurrentState}，无法发送音频数据。");
            }
        }

        /// <summary>
        /// 开启新对话并发送第一个音频包
        /// </summary>
        private void StartConversationAndSendAudio(byte[] initialAudioData)
        {
            _mono.StartCoroutine(StartConversationAndSendAudioCoroutine(initialAudioData));
        }

        private IEnumerator StartConversationAndSendAudioCoroutine(byte[] initialAudioData)
        {
            // 1. 发送开始对话请求
            yield return _mono.StartCoroutine(StartConversationCoroutine());

            // 2. 等待服务端确认对话已激活
            float timeout = 5f; // 等待5秒
            while (CurrentState != UnifiedServiceState.ConversationActive && timeout > 0)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            // 3. 发送第一个音频包
            if (CurrentState == UnifiedServiceState.ConversationActive)
            {
                Debug.Log("✅ 新对话已成功开启，发送首个音频包...");
                SendAudioData(initialAudioData);
            }
            else
            {
                Debug.LogError("❌ 开启新对话失败或超时，音频数据未能发送。");
            }
        }

        /// <summary>
        /// 处理生成完成命令（由LLMLogic发送）
        /// </summary>
        private void OnGenerateFinish(GenerateFinishCommand command)
        {
            if (CurrentState == UnifiedServiceState.ConversationActive)
            {
                Debug.Log("🏁 收到生成完成信号，对话状态重置为空闲（Connected）。");
                ChangeState(UnifiedServiceState.Connected);
            }
        }

        /// <summary>
        /// 处理发送文本命令
        /// </summary>
        private void OnSendTextDirectlyCommand(SendTextDirectlyCommand command)
        {
            SendTextDirectly(command.text);
        }
        #endregion

        #region 工具方法
        /// <summary>
        /// 停止所有协程
        /// </summary>
        private void StopAllCoroutines()
        {
            if (connectCoroutine != null)
            {
                _mono.StopCoroutine(connectCoroutine);
                connectCoroutine = null;
            }

            if (messageReceiveCoroutine != null)
            {
                _mono.StopCoroutine(messageReceiveCoroutine);
                messageReceiveCoroutine = null;
            }

            if (reconnectCoroutine != null)
            {
                _mono.StopCoroutine(reconnectCoroutine);
                reconnectCoroutine = null;
            }
        }


        #endregion

        #region 消息收发实现

        /// <summary>
        /// 处理JSON消息
        /// </summary>
        private IEnumerator ProcessJsonMessage(string jsonMessage)
        {
            try
            {
                Debug.Log($"📨 收到JSON消息: {jsonMessage}");

                var jsonObject = JObject.Parse(jsonMessage);
                string messageType = jsonObject["type"]?.ToString();

                switch (messageType)
                {
                    case MessageTypes.ASR_RESULT:
                        ProcessASRResult(jsonObject);
                        break;
                    case MessageTypes.LLM_RESULT:
                        ProcessLLMResult(jsonObject);
                        break;
                    case MessageTypes.AUDIO_START:
                        ProcessAudioStart(jsonObject);
                        break;
                    case MessageTypes.AUDIO_END:
                        ProcessAudioEnd(jsonObject);
                        break;
                    case MessageTypes.ERROR:
                        ProcessErrorMessage(jsonObject);
                        break;
                    default:
                        ProcessGeneralMessage(jsonObject);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理JSON消息异常: {ex}, 消息: {jsonMessage}");
            }

            yield return null;
        }

        /// <summary>
        /// 开始对话协程
        /// </summary>
        private IEnumerator StartConversationCoroutine()
        {
            Debug.Log("🗣️ 发送开始对话请求");

            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return _mono.StartCoroutine(SendStartConversationMessage());
        }

        /// <summary>
        /// 发送开始对话消息的内部协程
        /// </summary>
        private IEnumerator SendStartConversationMessage()
        {
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return _mono.StartCoroutine(ExecuteStartConversationSend());
        }

        /// <summary>
        /// 执行开始对话发送操作的内部协程
        /// </summary>
        private IEnumerator ExecuteStartConversationSend()
        {
            IEnumerator sendOperation = null;
            Exception caughtException = null;
            string jsonMessage = null;

            try
            {
                currentConversationId = $"unity_conv_{DateTime.Now:yyyyMMddHHmmss}";

                var startMessage = new StartConversationMessage
                {
                    username = Username,
                    asr_mode = ASRMode,
                    conversation_id = currentConversationId
                };

                jsonMessage = JsonConvert.SerializeObject(startMessage);
                Debug.Log($"📝 发送开始对话消息: {jsonMessage}");

                // 使用WebSocket适配器发送文本消息
                sendOperation = webSocketAdapter.SendTextAsync(jsonMessage);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建发送操作时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ 发送开始对话消息异常: {caughtException}");
                HandleConnectionError($"开始对话异常: {caughtException.Message}");
                yield break;
            }

            // 执行发送操作（在try-catch外部使用yield return）
            yield return sendOperation;

            Debug.Log($"✅ 开始对话请求已发送: {currentConversationId}");
            ChangeState(UnifiedServiceState.ConversationActive);
        }

        /// <summary>
        /// 发送音频数据
        /// </summary>
        private void SendAudioData(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                Debug.LogWarning("⚠️ 音频数据为空，跳过发送");
                return;
            }

            if (!IsConnected)
            {
                Debug.LogWarning("⚠️ WebSocket未连接，无法发送音频数据");
                return;
            }

            try
            {
                // 使用WebSocket适配器异步发送音频数据
                _mono.StartCoroutine(SendAudioDataCoroutine(audioData));
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 发送音频数据异常: {ex}");
            }
        }

        /// <summary>
        /// 发送音频数据协程
        /// </summary>
        private IEnumerator SendAudioDataCoroutine(byte[] audioData)
        {
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return _mono.StartCoroutine(ExecuteAudioSendOperation(audioData));
        }

        /// <summary>
        /// 执行音频发送操作的内部协程
        /// </summary>
        private IEnumerator ExecuteAudioSendOperation(byte[] audioData)
        {
            IEnumerator sendOperation = null;
            Exception caughtException = null;

            try
            {
                sendOperation = webSocketAdapter.SendBinaryAsync(audioData);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建操作时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ 音频数据发送失败: {caughtException}");
                yield break;
            }

            // 执行发送操作（在try-catch外部使用yield return）
            yield return sendOperation;
            
            // Debug.Log($"📡 音频数据发送成功: {audioData.Length} bytes");
        }



        /// <summary>
        /// 直接发送文本（绕过ASR）
        /// </summary>
        private void SendTextDirectly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("⚠️ 文本内容为空，跳过发送");
                return;
            }

            if (!IsConnected)
            {
                Debug.LogWarning("⚠️ WebSocket未连接，无法发送文本");
                return;
            }

            try
            {
                _mono.StartCoroutine(SendTextDirectlyCoroutine(text));
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 发送文本消息异常: {ex}");
            }
        }

        /// <summary>
        /// 发送文本消息协程
        /// </summary>
        private IEnumerator SendTextDirectlyCoroutine(string text)
        {
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return _mono.StartCoroutine(ExecuteTextSendOperation(text));
        }

        /// <summary>
        /// 执行文本发送操作的内部协程
        /// </summary>
        private IEnumerator ExecuteTextSendOperation(string text)
        {
            IEnumerator sendOperation = null;
            Exception caughtException = null;
            string jsonMessage = null;

            try
            {
                var textMessage = new TextInputMessage
                {
                    text = text,
                    timestamp = Time.time
                };

                jsonMessage = JsonConvert.SerializeObject(textMessage);
                Debug.Log($"📝 发送文本消息: {text}");

                sendOperation = webSocketAdapter.SendTextAsync(jsonMessage);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建操作时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ 文本发送失败: {caughtException}");
                yield break;
            }

            // 执行发送操作（在try-catch外部使用yield return）
            yield return sendOperation;
            
            Debug.Log($"✅ 文本发送成功: {text}");
        }


        #endregion

        #region 消息处理方法
        /// <summary>
        /// 处理ASR识别结果
        /// </summary>
        private void ProcessASRResult(JObject jsonObject)
        {
            try
            {
                string text = jsonObject["text"]?.ToString() ?? "";
                bool isFinal = jsonObject["is_final"]?.ToObject<bool>() ?? false;

                Debug.Log($"🎤 ASR识别结果: '{text}' (最终: {isFinal})");

                // 添加详细调试信息
                Debug.Log($"🔧 准备发送UnifiedASRResultCommand - SendCommand是否为null: {SendCommand == null}");

                var command = new UnifiedASRResultCommand
                {
                    text = text,
                    isFinal = isFinal
                };

                Debug.Log($"🔧 UnifiedASRResultCommand创建成功: text='{command.text}', isFinal={command.isFinal}");

                SendCommand.Send(command);

                Debug.Log($"🔧 UnifiedASRResultCommand已发送");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理ASR结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理LLM回复结果
        /// </summary>
        private void ProcessLLMResult(JObject jsonObject)
        {
            try
            {
                string text = jsonObject["text"]?.ToString() ?? "";
                bool isFirst = jsonObject["is_first"]?.ToObject<bool>() ?? false;
                bool isEnd = jsonObject["is_end"]?.ToObject<bool>() ?? false;

                Debug.Log($"🤖 LLM回复: '{text}' (首次: {isFirst}, 结束: {isEnd})");

                SendCommand.Send(new UnifiedLLMResultCommand
                {
                    text = text,
                    isFirst = isFirst,
                    isEnd = isEnd
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理LLM结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理音频开始通知
        /// </summary>
        private void ProcessAudioStart(JObject jsonObject)
        {
            try
            {
                string text = jsonObject["text"]?.ToString() ?? "";
                string fileName = jsonObject["file_name"]?.ToString() ?? "";

                Debug.Log($"🔊 音频开始: '{text}' (文件: {fileName})");

                // 重置音频接收上下文
                lock (audioBufferLock)
                {
                    currentAudioContext.associatedText = text;
                    currentAudioContext.fileName = fileName;
                    currentAudioContext.audioChunks.Clear();
                    currentAudioContext.isReceiving = true;
                    currentAudioContext.expectedTotalSize = 0;
                }

                SendCommand.Send(new UnifiedAudioStartCommand
                {
                    text = text,
                    fileName = fileName
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频开始异常: {ex}");
            }
        }

        /// <summary>
        /// 处理音频结束通知
        /// </summary>
        private void ProcessAudioEnd(JObject jsonObject)
        {
            try
            {
                string text = jsonObject["text"]?.ToString() ?? "";
                int totalSize = jsonObject["total_size"]?.ToObject<int>() ?? 0;

                Debug.Log($"✅ 音频结束: '{text}' (总大小: {totalSize} bytes)");

                // 完成音频接收
                lock (audioBufferLock)
                {
                    currentAudioContext.isReceiving = false;
                    currentAudioContext.expectedTotalSize = totalSize;
                }

                SendCommand.Send(new UnifiedAudioEndCommand
                {
                    text = text,
                    totalSize = totalSize
                });

                // 处理完整的音频数据
                ProcessCompleteAudioData();
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理音频结束异常: {ex}");
            }
        }

        /// <summary>
        /// 处理错误消息
        /// </summary>
        private void ProcessErrorMessage(JObject jsonObject)
        {
            try
            {
                string message = jsonObject["message"]?.ToString() ?? "未知错误";
                Debug.LogError($"❌ 服务端错误: {message}");

                SendCommand.Send(new UnifiedServiceErrorCommand
                {
                    errorMessage = message,
                    exception = null
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理错误消息异常: {ex}");
            }
        }

        /// <summary>
        /// 处理通用状态消息
        /// </summary>
        private void ProcessGeneralMessage(JObject jsonObject)
        {
            try
            {
                string status = jsonObject["status"]?.ToString() ?? "";
                string message = jsonObject["message"]?.ToString() ?? "";

                Debug.Log($"📨 通用消息: 状态={status}, 消息={message}");

                if (status == "success" && message.Contains("conversation"))
                {
                    // 对话启动成功
                    ChangeState(UnifiedServiceState.ConversationActive);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理通用消息异常: {ex}");
            }
        }

        /// <summary>
        /// 处理音频数据
        /// </summary>
        private void ProcessAudioData(byte[] audioData)
        {
            lock (audioBufferLock)
            {
                if (currentAudioContext.isReceiving)
                {
                    currentAudioContext.audioChunks.Add(audioData);
                    
                    // 将音频数据放入播放队列
                    audioReceiveBuffer.Enqueue(audioData);

                    // 发送音频数据通知
                    SendCommand.Send(new UnifiedAudioDataCommand
                    {
                        audioData = audioData,
                        associatedText = currentAudioContext.associatedText
                    });

                    // 显示接收进度（每10个块显示一次）
                    if (currentAudioContext.audioChunks.Count % 10 == 0)
                    {
                        int totalReceived = currentAudioContext.audioChunks.Sum(chunk => chunk.Length);
                        Debug.Log($"📡 音频接收中: {totalReceived} bytes ({currentAudioContext.audioChunks.Count} 块)");
                    }
                }
                else
                {
                    Debug.LogWarning("⚠️ 收到音频数据但当前未在接收状态");
                }
            }
        }

        /// <summary>
        /// 处理完整的音频数据
        /// </summary>
        private void ProcessCompleteAudioData()
        {
            lock (audioBufferLock)
            {
                if (currentAudioContext.audioChunks.Count > 0)
                {
                    int totalSize = currentAudioContext.audioChunks.Sum(chunk => chunk.Length);
                    Debug.Log($"🎵 音频接收完成: '{currentAudioContext.associatedText}', 总大小: {totalSize} bytes, 分块数: {currentAudioContext.audioChunks.Count}");

                    // 可选：合并音频块并保存或进一步处理
                    // byte[] completeAudio = CombineAudioChunks(currentAudioContext.audioChunks);
                    // 这里可以添加音频保存或其他处理逻辑
                }
            }
        }

        /// <summary>
        /// 合并音频块（可选功能）
        /// </summary>
        private byte[] CombineAudioChunks(List<byte[]> audioChunks)
        {
            int totalSize = audioChunks.Sum(chunk => chunk.Length);
            byte[] combinedAudio = new byte[totalSize];
            int offset = 0;

            foreach (var chunk in audioChunks)
            {
                Array.Copy(chunk, 0, combinedAudio, offset, chunk.Length);
                offset += chunk.Length;
            }

            return combinedAudio;
        }
        #endregion

        #region 音频接收上下文
        /// <summary>
        /// 音频接收上下文
        /// </summary>
        private class AudioReceiveContext
        {
            public string associatedText;
            public string fileName;
            public List<byte[]> audioChunks = new List<byte[]>();
            public bool isReceiving = false;
            public int expectedTotalSize = 0;
        }
        #endregion

        #region 析构和清理
        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            Debug.Log("🗑️ 统一语音服务管理器清理资源");
            
            Disconnect();
            
            // 清理WebSocket适配器
            if (webSocketAdapter != null)
            {
                try
                {
                    // 取消注册事件监听
                    webSocketAdapter.OnConnectionChanged -= OnWebSocketConnectionChanged;
                    webSocketAdapter.OnTextMessageReceived -= OnWebSocketTextMessage;
                    webSocketAdapter.OnBinaryMessageReceived -= OnWebSocketBinaryMessage;
                    webSocketAdapter.OnError -= OnWebSocketError;
                    
                    // 释放适配器资源
                    webSocketAdapter.Dispose();
                    webSocketAdapter = null;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠️ 清理WebSocket适配器时出现异常: {ex.Message}");
                }
            }
            
            // 取消注册命令监听
            if (RegisterCommand != null)
            {
                try
                {
                    RegisterCommand.UnRegister<ConnectUnifiedServiceCommand>(OnConnectCommand);
                    RegisterCommand.UnRegister<DisconnectUnifiedServiceCommand>(OnDisconnectCommand);
                    RegisterCommand.UnRegister<SendAudioDataCommand>(OnSendAudioDataCommand);
                    RegisterCommand.UnRegister<SendTextDirectlyCommand>(OnSendTextDirectlyCommand);
                    RegisterCommand.UnRegister<GenerateFinishCommand>(OnGenerateFinish); // 🔧 [新增] 取消注册生成结束命令
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠️ 取消注册命令时出现异常: {ex.Message}");
                }
            }

            // 清理音频缓冲
            lock (audioBufferLock)
            {
                audioReceiveBuffer.Clear();
            }
        }
        #endregion
    }
}