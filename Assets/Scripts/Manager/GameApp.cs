using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.Logics;
using LKZ.TypeEventSystem;
using LKZ.Voice;
using LKZ.UnifiedService;
using LKZ.Commands.UnifiedService;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LKZ.Manager
{

    public sealed class GameApp : MonoBehaviour, DIAwakeInterface, IDRegisterBindingInterface
    {
        [SerializeField, TextArea]
        private string StartContent ="欢迎欢迎";


        private VoiceRecognizerModel voiceRecognizer;
        private LLMLogic  llmLogic;
        private UnifiedVoiceServiceManager unifiedServiceManager;

        [Inject]
        private ISendCommand SendCommand { get; set; }

        public void DIRegisterBinding(IRegisterBinding registerBinding)
        {
            registerBinding.Binding<MonoBehaviour>().To(this);

            voiceRecognizer = new VoiceRecognizerModel();
            registerBinding.Binding<VoiceRecognizerModel>().To(voiceRecognizer);

            llmLogic = new LLMLogic();
            registerBinding.Binding<LLMLogic>().To(llmLogic);

            // 注册统一服务管理器
            unifiedServiceManager = new UnifiedVoiceServiceManager();
            registerBinding.Binding<UnifiedVoiceServiceManager>().To(unifiedServiceManager);
        }

        void DIAwakeInterface.OnAwake()
        { 
            Application.runInBackground = true;

            Input.backButtonLeavesApp = true;

            Screen.sleepTimeout = SleepTimeout.NeverSleep;


            Screen.fullScreen = false; 
        }
         

        private void Start()
        {  
            // 依赖注入初始化
            SceneDependencyInjectContextManager.Instance.InjectProperty(voiceRecognizer);
            SceneDependencyInjectContextManager.Instance.InjectProperty(llmLogic);
            SceneDependencyInjectContextManager.Instance.InjectProperty(unifiedServiceManager);
            
            // 模块初始化
            voiceRecognizer.Initialized();
            llmLogic.Initialized();
            unifiedServiceManager.Initialize();

            SendCommand.Send(new AddChatContentCommand { infoType = Enum.InfoType.ChatGPT, _addTextAction = value => value.Invoke(StartContent) });
            SendCommand.Send(new GenerateFinishCommand { });//�����������
             
            SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = true });//开始语音识别

            // 启动统一服务连接（测试用）
            Debug.Log("🚀 GameApp启动完成，统一服务管理器已就绪");
            
            // 🔧 恢复连接逻辑：通过命令系统触发连接，避免直接调用JavaScript
            Debug.Log("🎤 WebGL模式：通过Unity命令系统建立连接");
            StartCoroutine(DelayedConnectToUnifiedService());
        }

        /// <summary>
        /// 延迟连接到统一服务（给其他模块初始化时间）
        /// </summary>
        private System.Collections.IEnumerator DelayedConnectToUnifiedService()
        {
            yield return new WaitForSeconds(2.0f);
            
            Debug.Log("🔗 自动连接到统一服务...");
            SendCommand.Send(new ConnectUnifiedServiceCommand
            {
                serverUrl = "ws://127.0.0.1:10004",
                username = "UnityTestUser",
                asrMode = "ali"
            });
        }


        private void OnDestroy()
        {
            voiceRecognizer.OnDestroy();
            llmLogic?.OnDestroy(); // Added
            unifiedServiceManager?.Dispose();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL平台的语音识别回调桥接方法
        public void OnWebGLRecognitionResult(string result)
        {
            Debug.Log($"GameApp收到WebGL语音识别结果: {result}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLRecognitionResult(result);
            }
            else
            {
                Debug.LogWarning("无法获取VoiceRecognizerWebGL实例");
            }
        }

        public void OnWebGLConnectionStatus(string status)
        {
            Debug.Log($"GameApp收到WebGL连接状态: {status}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLConnectionStatus(status);
            }
            else
            {
                Debug.LogWarning("无法获取VoiceRecognizerWebGL实例");
            }
        }

        public void OnWebGLError(string error)
        {
            Debug.LogError($"GameApp收到WebGL错误: {error}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLError(error);
            }
            else
            {
                Debug.LogWarning("无法获取VoiceRecognizerWebGL实例");
            }
        }

        // 🔧 添加缺失的WebGL桥接方法
        public void OnWebGLAudioData(string base64AudioData)
        {
            Debug.Log($"GameApp收到WebGL音频数据: {base64AudioData.Length} chars");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLAudioData(base64AudioData);
            }
            else
            {
                Debug.LogWarning("无法获取VoiceRecognizerWebGL实例来处理音频数据");
            }
        }

        public void OnWebGLUnifiedServiceConnected(string data)
        {
            Debug.Log($"GameApp收到WebGL统一服务连接: {data}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLUnifiedServiceConnected(data);
            }
        }

        public void OnWebGLUnifiedServiceDisconnected(string data)
        {
            Debug.Log($"GameApp收到WebGL统一服务断开: {data}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLUnifiedServiceDisconnected(data);
            }
        }

        public void OnWebGLUnifiedServiceError(string errorMessage)
        {
            Debug.LogError($"GameApp收到WebGL统一服务错误: {errorMessage}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLUnifiedServiceError(errorMessage);
            }
        }

        public void OnWebGLLLMResult(string llmData)
        {
            Debug.Log($"GameApp收到WebGL LLM结果: {llmData}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLLLMResult(llmData);
            }
        }

        public void OnWebGLAudioStart(string audioStartData)
        {
            Debug.Log($"GameApp收到WebGL音频开始: {audioStartData}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLAudioStart(audioStartData);
            }
        }

        public void OnWebGLAudioEnd(string audioEndData)
        {
            Debug.Log($"GameApp收到WebGL音频结束: {audioEndData}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLAudioEnd(audioEndData);
            }
        }

        public void OnWebGLUnifiedServiceStatus(string statusData)
        {
            Debug.Log($"GameApp收到WebGL统一服务状态: {statusData}");

            if (voiceRecognizer?.voiceRecognizer is VoiceRecognizerWebGL webglRecognizer)
            {
                webglRecognizer.OnWebGLUnifiedServiceStatus(statusData);
            }
        }
#else
        // 非WebGL平台的空实现，避免JavaScript调用错误
        public void OnWebGLRecognitionResult(string result)
        {
            Debug.LogWarning($"WebGL语音识别回调在非WebGL平台被调用: {result}");
        }

        public void OnWebGLConnectionStatus(string status)
        {
            Debug.LogWarning($"WebGL连接状态回调在非WebGL平台被调用: {status}");
        }

        public void OnWebGLError(string error)
        {
            Debug.LogWarning($"WebGL错误回调在非WebGL平台被调用: {error}");
        }

        // 🔧 添加缺失的非WebGL空实现
        public void OnWebGLAudioData(string base64AudioData)
        {
            Debug.LogWarning($"WebGL音频数据回调在非WebGL平台被调用");
        }

        public void OnWebGLUnifiedServiceConnected(string data)
        {
            Debug.LogWarning($"WebGL统一服务连接回调在非WebGL平台被调用");
        }

        public void OnWebGLUnifiedServiceDisconnected(string data)
        {
            Debug.LogWarning($"WebGL统一服务断开回调在非WebGL平台被调用");
        }

        public void OnWebGLUnifiedServiceError(string errorMessage)
        {
            Debug.LogWarning($"WebGL统一服务错误回调在非WebGL平台被调用");
        }

        public void OnWebGLLLMResult(string llmData)
        {
            Debug.LogWarning($"WebGL LLM结果回调在非WebGL平台被调用");
        }

        public void OnWebGLAudioStart(string audioStartData)
        {
            Debug.LogWarning($"WebGL音频开始回调在非WebGL平台被调用");
        }

        public void OnWebGLAudioEnd(string audioEndData)
        {
            Debug.LogWarning($"WebGL音频结束回调在非WebGL平台被调用");
        }

        public void OnWebGLUnifiedServiceStatus(string statusData)
        {
            Debug.LogWarning($"WebGL统一服务状态回调在非WebGL平台被调用");
        }
#endif
    }
}
