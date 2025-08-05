using UnityEngine;
using LKZ.UnifiedService;
using LKZ.Commands.UnifiedService;
using LKZ.TypeEventSystem;
using LKZ.DependencyInject;

namespace LKZ.Tests
{
    /// <summary>
    /// 统一服务测试脚本
    /// 用于测试UnifiedVoiceServiceManager的基础功能
    /// </summary>
    public class UnifiedServiceTest : MonoBehaviour
    {
        [Header("测试配置")]
        [SerializeField] private string testServerUrl = "ws://127.0.0.1:10004";
        [SerializeField] private string testUsername = "TestUser";
        [SerializeField] private string testASRMode = "ali";
        [SerializeField] private bool autoStartTest = false;

        [Header("测试控制")]
        [SerializeField] private bool enableDetailedLogs = true;

        private UnifiedVoiceServiceManager serviceManager;
        private ISendCommand sendCommand;
        private IRegisterCommand registerCommand;

        void Start()
        {
            if (enableDetailedLogs)
            {
                Debug.Log("🧪 UnifiedServiceTest 开始初始化");
            }

            // 获取依赖注入的服务
            var diManager = FindObjectOfType<SceneDependencyInjectContextManager>();
            if (diManager != null)
            {
                // 通过依赖注入获取服务
                SceneDependencyInjectContextManager.Instance.InjectProperty(this);
            }
            else
            {
                Debug.LogError("❌ 未找到依赖注入管理器，无法进行测试");
                return;
            }

            // 注册事件监听
            RegisterEventListeners();

            if (autoStartTest)
            {
                Invoke(nameof(StartConnectionTest), 2.0f);
            }

            if (enableDetailedLogs)
            {
                Debug.Log("✅ UnifiedServiceTest 初始化完成");
            }
        }

        /// <summary>
        /// 注册事件监听
        /// </summary>
        private void RegisterEventListeners()
        {
            var eventManager = FindObjectOfType<TypeEventSystemManager>();
            if (eventManager != null)
            {
                eventManager.Register<UnifiedServiceStateChangedCommand>(OnServiceStateChanged);
                eventManager.Register<UnifiedServiceErrorCommand>(OnServiceError);
                eventManager.Register<UnifiedASRResultCommand>(OnASRResult);
                eventManager.Register<UnifiedLLMResultCommand>(OnLLMResult);
                eventManager.Register<UnifiedAudioStartCommand>(OnAudioStart);
                eventManager.Register<UnifiedAudioEndCommand>(OnAudioEnd);

                if (enableDetailedLogs)
                {
                    Debug.Log("📝 事件监听器注册完成");
                }
            }
        }

        #region 测试方法

        /// <summary>
        /// 开始连接测试
        /// </summary>
        [ContextMenu("开始连接测试")]
        public void StartConnectionTest()
        {
            Debug.Log("🔗 开始连接测试...");
            
            var connectCommand = new ConnectUnifiedServiceCommand
            {
                serverUrl = testServerUrl,
                username = testUsername,
                asrMode = testASRMode
            };

            // 通过事件系统发送连接命令
            var eventManager = FindObjectOfType<TypeEventSystemManager>();
            if (eventManager != null)
            {
                eventManager.Send(connectCommand);
                Debug.Log($"📤 已发送连接命令: {testServerUrl}");
            }
            else
            {
                Debug.LogError("❌ 未找到事件系统管理器");
            }
        }

        /// <summary>
        /// 断开连接测试
        /// </summary>
        [ContextMenu("断开连接测试")]
        public void StartDisconnectionTest()
        {
            Debug.Log("🔌 开始断开连接测试...");
            
            var disconnectCommand = new DisconnectUnifiedServiceCommand();

            var eventManager = FindObjectOfType<TypeEventSystemManager>();
            if (eventManager != null)
            {
                eventManager.Send(disconnectCommand);
                Debug.Log("📤 已发送断开连接命令");
            }
        }

        /// <summary>
        /// 发送测试文本
        /// </summary>
        [ContextMenu("发送测试文本")]
        public void SendTestText()
        {
            Debug.Log("📝 发送测试文本...");
            
            var textCommand = new SendTextDirectlyCommand
            {
                text = "你好，这是一个测试消息"
            };

            var eventManager = FindObjectOfType<TypeEventSystemManager>();
            if (eventManager != null)
            {
                eventManager.Send(textCommand);
                Debug.Log("📤 已发送测试文本命令");
            }
        }

        #endregion

        #region 事件处理

        private void OnServiceStateChanged(UnifiedServiceStateChangedCommand command)
        {
            if (enableDetailedLogs)
            {
                Debug.Log($"🔄 服务状态变化: {command.previousState} → {command.currentState}");
                Debug.Log($"   消息: {command.message}");
            }

            // 根据状态变化执行相应的UI更新或其他逻辑
            switch (command.currentState)
            {
                case UnifiedServiceState.Connected:
                    Debug.Log("✅ 统一服务连接成功");
                    break;
                case UnifiedServiceState.ConversationActive:
                    Debug.Log("🗣️ 对话会话已激活");
                    break;
                case UnifiedServiceState.Error:
                    Debug.LogWarning("⚠️ 统一服务出现错误");
                    break;
                case UnifiedServiceState.Disconnected:
                    Debug.Log("🔌 统一服务已断开");
                    break;
            }
        }

        private void OnServiceError(UnifiedServiceErrorCommand command)
        {
            Debug.LogError($"❌ 统一服务错误: {command.errorMessage}");
            if (command.exception != null)
            {
                Debug.LogError($"   异常详情: {command.exception}");
            }
        }

        private void OnASRResult(UnifiedASRResultCommand command)
        {
            string status = command.isFinal ? "[最终]" : "[临时]";
            Debug.Log($"🎤 ASR识别 {status}: {command.text}");
        }

        private void OnLLMResult(UnifiedLLMResultCommand command)
        {
            string status = "";
            if (command.isFirst) status += "[首次]";
            if (command.isEnd) status += "[结束]";
            
            Debug.Log($"🤖 LLM回复 {status}: {command.text}");
        }

        private void OnAudioStart(UnifiedAudioStartCommand command)
        {
            Debug.Log($"🔊 音频开始: '{command.text}' (文件: {command.fileName})");
        }

        private void OnAudioEnd(UnifiedAudioEndCommand command)
        {
            Debug.Log($"✅ 音频结束: '{command.text}' (总大小: {command.totalSize} bytes)");
        }

        #endregion

        #region UI控制方法

        void OnGUI()
        {
            if (!enableDetailedLogs) return;

            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            
            GUILayout.Label("统一服务测试控制面板", GUI.skin.box);
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("连接统一服务"))
            {
                StartConnectionTest();
            }
            
            if (GUILayout.Button("断开连接"))
            {
                StartDisconnectionTest();
            }
            
            if (GUILayout.Button("发送测试文本"))
            {
                SendTestText();
            }

            GUILayout.Space(10);
            
            GUILayout.Label($"服务器: {testServerUrl}");
            GUILayout.Label($"用户: {testUsername}");
            GUILayout.Label($"ASR模式: {testASRMode}");
            
            GUILayout.EndArea();
        }

        #endregion

        void OnDestroy()
        {
            // 取消事件监听
            var eventManager = FindObjectOfType<TypeEventSystemManager>();
            if (eventManager != null)
            {
                eventManager.UnRegister<UnifiedServiceStateChangedCommand>(OnServiceStateChanged);
                eventManager.UnRegister<UnifiedServiceErrorCommand>(OnServiceError);
                eventManager.UnRegister<UnifiedASRResultCommand>(OnASRResult);
                eventManager.UnRegister<UnifiedLLMResultCommand>(OnLLMResult);
                eventManager.UnRegister<UnifiedAudioStartCommand>(OnAudioStart);
                eventManager.UnRegister<UnifiedAudioEndCommand>(OnAudioEnd);
            }
        }
    }
}