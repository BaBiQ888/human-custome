using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.Commands.UnifiedService; // Added for unified service
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
using LKZ.UnifiedService; // Added for unified service
using System;
using System.Collections;
using UnityEngine;

namespace LKZ.Rolle
{
    public interface IRolleManager
    {
        public int RolleCount { get; }
        public int CurrentIndex { get; }

    }

    public interface IRollePosition
    {
        public Vector3 Position { get; }

        /// <summary>
        /// 角色显示的位置
        /// </summary>
        public Vector3 RolleShowPosition { get; }
    }

    public sealed class RolleManager : MonoBehaviour, IRolleManager, IRollePosition, IDRegisterBindingInterface, DIAwakeInterface
    { 
        [SerializeField]
        private Vector3 rolleMiddlePos; 

        [SerializeField]
        private GameObject[] RolleGames;

        public float switchRolleSpeed = 1f;

        private RolleControl[] RolleControls;

        private int rolleIndex = 0;

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        [Inject]
        private UnifiedVoiceServiceManager unifiedServiceManager { get; set; } // Added

        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        public bool UseUnifiedService { get; set; } = true; // Added

        public AudioSource AudioSource => RolleControls[rolleIndex].AudioSource;

        public int RolleCount => RolleControls.Length;

        public int CurrentIndex => rolleIndex;

        public Vector3 Position => RolleControls[rolleIndex].transform.position;

        public Vector3 RolleShowPosition => rolleMiddlePos;
          

        public float switchDancePosResetSpeed = 2f;


        static readonly Quaternion defaultRotation = Quaternion.Euler(0, 180, 0);
        public void OnAwake()
        {
            RolleControls = new RolleControl[RolleGames.Length];

            for (int i = 0; i < RolleControls.Length; i++)
            {
                RolleControls[i] = Instantiate(this.RolleGames[i], this.rolleMiddlePos, Quaternion.Euler(0, 180, 0), base.transform).GetComponent<RolleControl>();
                RolleControls[i].RolleDisable();
            }

            RolleControls[0].RolleEnable();
             

            // 注册传统命令监听
            RegisterCommand.Register<ChatGPTStartTalkCommand>(ChatGPTStartTalkCommandCallback);
            RegisterCommand.Register<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);
            
            // 注册统一服务事件监听
            if (UseUnifiedService)
            {
                RegisterCommand.Register<UnifiedAudioStartCommand>(OnUnifiedAudioStart); // Added
                RegisterCommand.Register<UnifiedAudioEndCommand>(OnUnifiedAudioEnd); // Added
                RegisterCommand.Register<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged); // Added
                RegisterCommand.Register<UnifiedLLMResultCommand>(OnUnifiedLLMResult); // Added
                Debug.Log("✅ RolleManager统一服务事件监听注册完成");
            } 
        }

        public float PlaySaluteAnimationDelay = 1f;
        private IEnumerator Start()
        {
            yield return new WaitForSeconds(PlaySaluteAnimationDelay);
            RolleControls[0].TriggerSaluteAnimation();
        }

         
        void IDRegisterBindingInterface.DIRegisterBinding(IRegisterBinding registerBinding)
        {
            registerBinding.Binding<IRolleManager>().To(this);
            registerBinding.Binding<IRollePosition>().To(this);
        }

        private bool isSwitch;
          
        private void SettingVoiceRecognitionCommandCallback(SettingVoiceRecognitionCommand obj)
        {
            if (obj.IsStartVoiceRecognition)
                RolleControls[this.rolleIndex].SetAnimationTalk(false);
        }

        private void ChatGPTStartTalkCommandCallback(ChatGPTStartTalkCommand obj)
        {
            RolleControls[this.rolleIndex].SetAnimationTalk(true);
        }

        #region 统一服务事件处理
        /// <summary>
        /// 处理统一服务LLM结果（用于提前启动对话动画）
        /// </summary>
        private void OnUnifiedLLMResult(UnifiedLLMResultCommand command) // Added
        {
            try
            {
                if (command.isFirst)
                {
                    // 第一次LLM回复，立即启动对话动画
                    Debug.Log("🎭 收到LLM首次回复，启动对话动画");
                    RolleControls[this.rolleIndex].SetAnimationTalk(true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ RolleManager处理LLM结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务音频开始通知
        /// </summary>
        private void OnUnifiedAudioStart(UnifiedAudioStartCommand command) // Added
        {
            try
            {
                Debug.Log($"🎭 音频开始播放，确保对话动画激活: '{command.text}'");
                
                // 确保对话动画处于激活状态
                if (!IsCurrentRolleTalking())
                {
                    RolleControls[this.rolleIndex].SetAnimationTalk(true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ RolleManager处理音频开始异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务音频结束通知
        /// </summary>
        private void OnUnifiedAudioEnd(UnifiedAudioEndCommand command) // Added
        {
            try
            {
                Debug.Log($"🎭 音频播放结束: '{command.text}'");
                // 注意：不在这里停止动画，因为可能有多个音频段连续播放
                // 动画停止将由LLMLogic的PlayFinish方法通过SettingVoiceRecognitionCommand触发
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ RolleManager处理音频结束异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务状态变化
        /// </summary>
        private void OnUnifiedServiceStateChanged(UnifiedServiceStateChangedCommand command) // Added
        {
            Debug.Log($"🔄 RolleManager收到服务状态变化: {command.previousState} → {command.currentState}");
            
            switch (command.currentState)
            {
                case UnifiedServiceState.Connected:
                    Debug.Log("✅ 统一服务已连接，数字人准备就绪");
                    break;
                case UnifiedServiceState.ConversationActive:
                    Debug.Log("🗣️ 对话已激活，数字人进入对话模式");
                    break;
                case UnifiedServiceState.Disconnected:
                case UnifiedServiceState.Error:
                    Debug.LogWarning("⚠️ 统一服务连接异常，停止对话动画");
                    RolleControls[this.rolleIndex].SetAnimationTalk(false);
                    break;
            }
        }

        /// <summary>
        /// 检查当前角色是否正在说话动画中
        /// </summary>
        private bool IsCurrentRolleTalking() // Added
        {
            try
            {
                var animator = RolleControls[this.rolleIndex].GetComponent<Animator>();
                if (animator != null)
                {
                    return animator.GetBool("talk");
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 检查角色说话状态异常: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 资源清理
        /// </summary>
        public void OnDestroy() // Added
        {
            Debug.Log("🗑️ RolleManager 清理资源");
            
            try
            {
                if (RegisterCommand != null)
                {
                    RegisterCommand.UnRegister<ChatGPTStartTalkCommand>(ChatGPTStartTalkCommandCallback);
                    RegisterCommand.UnRegister<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);
                    
                    if (UseUnifiedService)
                    {
                        RegisterCommand.UnRegister<UnifiedAudioStartCommand>(OnUnifiedAudioStart);
                        RegisterCommand.UnRegister<UnifiedAudioEndCommand>(OnUnifiedAudioEnd);
                        RegisterCommand.UnRegister<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged);
                        RegisterCommand.UnRegister<UnifiedLLMResultCommand>(OnUnifiedLLMResult);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"⚠️ RolleManager清理资源时出现异常: {ex.Message}");
            }
        }
        #endregion
           
    }
}
