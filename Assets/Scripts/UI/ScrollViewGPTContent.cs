using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.UnifiedService; // Added for unified service
using LKZ.DependencyInject;
using LKZ.Enum;
using LKZ.TypeEventSystem;
using LKZ.UnifiedService; // Added for unified service
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LKZ.UI
{
    public sealed class ScrollViewGPTContent : MonoBehaviour, DIStartInterface, DIAwakeInterface, IBeginDragHandler, IEndDragHandler
    {
        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }

        [Inject]
        private UnifiedVoiceServiceManager unifiedServiceManager { get; set; } // Added

        /// <summary>
        /// 是否使用统一服务模式
        /// </summary>
        public bool UseUnifiedService { get; set; } = true; // Added

        ScrollRect _scrollRect;

        private RectTransform _scrollRect_Content;

        [SerializeField]
        private GameObject _my_Go, _gpt_go;

        [SerializeField, Tooltip("���")]
        private float interval = 15f;

        [SerializeField, Tooltip("���������Ƿ������ƶ�����")]
        private float GPTGenerateContentUpMove = 30f;

        private ShowContent currentShowContent;

        private bool isCurrentUserInput = false;

        /// <summary>
        /// �������ݺͲ��������ݹ�����ͼ��λ��
        /// </summary>
        private Vector3 defaultPos, GPTGenerateContentPos;
         

        private bool isSetScrollRectNormalizedPosition;

        /// <summary>
        /// �Ƿ�������GPT����
        /// </summary>
        private bool isGenerateGPTContent;

        private RectTransform thisRect;

        void DIAwakeInterface.OnAwake()
        {
            thisRect = base.transform as RectTransform;

            _scrollRect = GetComponent<ScrollRect>();
            _scrollRect_Content = _scrollRect.content;

            defaultPos = thisRect.anchoredPosition;
            GPTGenerateContentPos = defaultPos;
            GPTGenerateContentPos.y += GPTGenerateContentUpMove;
             
            isSetScrollRectNormalizedPosition = true;
        }

        void DIStartInterface.OnStart()
        {
            Debug.Log($"🎨 ScrollViewGPTContent初始化 - 模式: {(UseUnifiedService ? "统一服务" : "传统")}");
            
            // 注册传统命令监听
            RegisterCommand.Register<AddChatContentCommand>(AddChatContentCommandCallback);
            RegisterCommand.Register<GenerateFinishCommand>(GenerateFinishCommandCallback);
            
            // 注册统一服务事件监听
            if (UseUnifiedService)
            {
                RegisterCommand.Register<UnifiedLLMResultCommand>(OnUnifiedLLMResult); // Added
                RegisterCommand.Register<UnifiedServiceStateChangedCommand>(OnUnifiedServiceStateChanged); // Added
                Debug.Log("✅ ScrollView统一服务事件监听注册完成");
            }
        }
         

        private void GenerateFinishCommandCallback(GenerateFinishCommand obj)
        {
            isGenerateGPTContent = false;
        }

        void IEndDragHandler.OnEndDrag(PointerEventData eventData)
        {
            isSetScrollRectNormalizedPosition = true;

        }

        void IBeginDragHandler.OnBeginDrag(PointerEventData eventData)
        {
            isSetScrollRectNormalizedPosition = false;

        }

        private void OnDestroy()
        {
            Debug.Log("🗑️ ScrollViewGPTContent 清理资源");
            
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
            catch (Exception ex)
            {
                Debug.LogWarning($"⚠️ ScrollViewGPTContent清理资源时出现异常: {ex.Message}");
            }
        }

        private void AddChatContentCommandCallback(AddChatContentCommand obj)
        {
            Vector2 pos = new Vector2(0, -_scrollRect_Content.sizeDelta.y);

            if (!object.ReferenceEquals(null, currentShowContent))
                currentShowContent.Enabled = false;

            switch (obj.infoType)
            {
                case InfoType.My:
                    currentShowContent = Instantiate(_my_Go, _scrollRect_Content).GetComponent<ShowContent>();
                    //  pos.x = ScreenWidth;
                    isCurrentUserInput = true; 
                    break;
                case InfoType.ChatGPT:
                    currentShowContent = Instantiate(_gpt_go, _scrollRect_Content).GetComponent<ShowContent>();
                    isGenerateGPTContent = true;
                    isCurrentUserInput = false;
                    break;
            }

            currentShowContent.Initialized(pos);

            lastHeight = 0;

            _scrollRect_Content.sizeDelta += new Vector2(0, interval);

            obj._addTextAction(AddShowText);
        }

        float lastHeight = default;

        private void AddShowText(string c)
        {
            if (isCurrentUserInput)
            {
                // 用户输入：使用替换显示
                currentShowContent.SetText(c);
            }
            else
            {
                // LLM响应：使用累积显示
                currentShowContent.AddText(c);
            }
        }

        private void LateUpdate()
        {
            if (object.ReferenceEquals(null, currentShowContent))
                return;

            if (currentShowContent.Height != lastHeight)
            {
                _scrollRect_Content.sizeDelta += new Vector2(0, currentShowContent.Height - lastHeight);

                lastHeight = currentShowContent.Height;
            }

            if (isSetScrollRectNormalizedPosition)
                _scrollRect.verticalNormalizedPosition = Mathf.Lerp(_scrollRect.verticalNormalizedPosition, 0, 0.05f);

#if !UNITY_STANDALONE_WIN
            //���������ƶ�
            thisRect.anchoredPosition = Vector3.Lerp(this.thisRect.anchoredPosition, isGenerateGPTContent ? this.GPTGenerateContentPos : this.defaultPos, 0.05f);
#endif
        }

        #region 统一服务事件处理
        /// <summary>
        /// 处理统一服务LLM结果（用于优化UI响应）
        /// </summary>
        private void OnUnifiedLLMResult(UnifiedLLMResultCommand command) // Added
        {
            try
            {
                if (command.isFirst)
                {
                    // 第一次回复，确保ChatGPT UI已准备好
                    Debug.Log("🎨 收到LLM首次回复，UI准备显示");
                    isGenerateGPTContent = true;
                }
                else if (command.isEnd)
                {
                    // LLM回复结束，开始准备下一轮
                    Debug.Log("🎨 LLM回复结束，UI准备下一轮对话");
                    // 注意：不在这里设置isGenerateGPTContent=false，仍需等待音频播放完成
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ ScrollView处理LLM结果异常: {ex}");
            }
        }

        /// <summary>
        /// 处理统一服务状态变化
        /// </summary>
        private void OnUnifiedServiceStateChanged(UnifiedServiceStateChangedCommand command) // Added
        {
            Debug.Log($"🔄 ScrollView收到服务状态变化: {command.previousState} → {command.currentState}");
            
            switch (command.currentState)
            {
                case UnifiedServiceState.Connected:
                    Debug.Log("✅ 统一服务已连接，UI准备就绪");
                    break;
                case UnifiedServiceState.ConversationActive:
                    Debug.Log("🗣️ 对话已激活，UI进入对话模式");
                    break;
                case UnifiedServiceState.Disconnected:
                case UnifiedServiceState.Error:
                    Debug.LogWarning("⚠️ 统一服务连接异常，UI停止生成状态");
                    isGenerateGPTContent = false;
                    break;
            }
        }

        /// <summary>
        /// 优化的滚动性能（针对流式文本）
        /// </summary>
        private void OptimizeScrollPerformance() // Added
        {
            try
            {
                // 在统一服务模式下，使用更平滑的滚动
                if (UseUnifiedService && isGenerateGPTContent)
                {
                    _scrollRect.verticalNormalizedPosition = Mathf.Lerp(_scrollRect.verticalNormalizedPosition, 0, 0.1f);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 优化滚动性能异常: {ex}");
            }
        }
        #endregion


    }
}