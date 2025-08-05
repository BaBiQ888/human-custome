using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.UnifiedService; // Added for unified service
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
using LKZ.UnifiedService; // Added for unified service
using UnityEngine;
using UnityEngine.UI;

namespace LKZ.UI
{
    /// <summary>
    /// ui停止生成按钮
    /// </summary>
    public sealed class StopGenerateButton : MonoBehaviour, DIStartInterface
    {

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private UnifiedVoiceServiceManager unifiedServiceManager { get; set; } // Added

        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        public bool UseUnifiedService { get; set; } = true; // Added

        void DIStartInterface.OnStart()
        {
            Debug.Log($"🔴 StopGenerateButton初始化 - 模式: {(UseUnifiedService ? "统一服务" : "传统")}");
            
            // 注册传统命令监听
            RegisterCommand.Register<AddChatContentCommand>(AddChatContentCommandCallback);
            RegisterCommand.Register<GenerateFinishCommand>(GenerateFinishCommandCallback);
            
            // 注册统一服务事件监听
            if (UseUnifiedService)
            {
                RegisterCommand.Register<UnifiedLLMResultCommand>(OnUnifiedLLMResult); // Added
                RegisterCommand.Register<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged); // Added
            }

            // 设置按钮点击事件（支持统一服务）
            GetComponent<Button>().onClick.AddListener(OnStopButtonClick); // Modified
        }

        private void GenerateFinishCommandCallback(GenerateFinishCommand obj)
        {
            gameObject.SetActive(false);
        }

        private void AddChatContentCommandCallback(AddChatContentCommand obj)
        {
            if (obj.infoType == Enum.InfoType.ChatGPT)
            {
                gameObject.SetActive(true);
            }
        }

        #region 统一服务支持
        /// <summary>
        /// 停止按钮点击处理（支持统一服务）
        /// </summary>
        private void OnStopButtonClick() // Added
        {
            try
            {
                Debug.Log("🔴 用户点击停止生成按钮");
                
                if (UseUnifiedService && unifiedServiceManager != null && unifiedServiceManager.IsConnected)
                {
                    // 统一服务模式：发送停止命令到统一服务（如果服务端支持）
                    Debug.Log("🔴 统一服务模式 - 停止生成");
                    // 注意：这里可以添加向统一服务发送停止请求的逻辑
                    // 目前先发送传统的停止命令
                }
                
                // 发送传统停止命令（两种模式都支持）
                SendCommand.Send(new StopGenerateCommand());
                
                Debug.Log("✅ 停止生成命令已发送");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ 停止按钮点击处理异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务LLM结果（用于按钮状态管理）
        /// </summary>
        private void OnUnifiedLLMResult(UnifiedLLMResultCommand command) // Added
        {
            try
            {
                if (command.isFirst)
                {
                    // LLM开始回复，确保停止按钮可见
                    Debug.Log("🔴 LLM开始回复，显示停止按钮");
                    gameObject.SetActive(true);
                }
                else if (command.isEnd)
                {
                    // LLM回复结束，但不立即隐藏按钮，等待音频播放完成
                    Debug.Log("🔴 LLM回复结束，等待音频播放完成");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ StopButton处理LLM结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务状态变化
        /// </summary>
        private void OnUnifiedServiceStateChanged(UnifiedServiceStateChangedCommand command) // Added
        {
            Debug.Log($"🔄 StopButton收到服务状态变化: {command.previousState} → {command.currentState}");
            
            switch (command.currentState)
            {
                case UnifiedServiceState.Disconnected:
                case UnifiedServiceState.Error:
                    // 服务断开，隐藏停止按钮
                    Debug.Log("🔴 服务断开，隐藏停止按钮");
                    gameObject.SetActive(false);
                    break;
            }
        }

        /// <summary>
        /// 资源清理
        /// </summary>
        private void OnDestroy() // Added
        {
            Debug.Log("🗑️ StopGenerateButton 清理资源");
            
            try
            {
                if (RegisterCommand != null)
                {
                    RegisterCommand.UnRegister<AddChatContentCommand>(AddChatContentCommandCallback);
                    RegisterCommand.UnRegister<GenerateFinishCommand>(GenerateFinishCommandCallback);
                    
                    if (UseUnifiedService)
                    {
                        RegisterCommand.UnRegister<UnifiedLLMResultCommand>(OnUnifiedLLMResult);
                        RegisterCommand.UnRegister<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"⚠️ StopGenerateButton清理资源时出现异常: {ex.Message}");
            }
        }
        #endregion
    }
}
