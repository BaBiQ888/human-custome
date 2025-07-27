using LKZ.Chat.Commands;
using LKZ.Commands.Chat;
using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.Logics;
using LKZ.TypeEventSystem;
using LKZ.Voice;
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

        [Inject]
        private ISendCommand SendCommand { get; set; }

        public void DIRegisterBinding(IRegisterBinding registerBinding)
        {
            registerBinding.Binding<MonoBehaviour>().To(this);


            voiceRecognizer = new VoiceRecognizerModel();
            registerBinding.Binding<VoiceRecognizerModel>().To(voiceRecognizer);

            llmLogic = new LLMLogic();
            registerBinding.Binding<LLMLogic>().To(llmLogic);

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
            SceneDependencyInjectContextManager.Instance.InjectProperty(voiceRecognizer);
            SceneDependencyInjectContextManager.Instance.InjectProperty(llmLogic);
            voiceRecognizer.Initialized();
            llmLogic.Initialized();

            SendCommand.Send(new AddChatContentCommand { infoType = Enum.InfoType.ChatGPT, _addTextAction = value => value.Invoke(StartContent) });
            SendCommand.Send(new GenerateFinishCommand { });//�����������
             
            SendCommand.Send(new SettingVoiceRecognitionCommand { IsStartVoiceRecognition = true });//��ʼ����ʶ��

        }


        private void OnDestroy()
        {
            voiceRecognizer.OnDestroy();
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
#endif
    }
}
