using LKZ.Commands.Voice;
using LKZ.DependencyInject;
using LKZ.TypeEventSystem;
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
        const string url= "ws://47.79.16.41:10199/recognition";

        [Inject]
        private MonoBehaviour _mono { get; set; }

        [Inject]
        private ISendCommand SendCommand { get; set; }

        [Inject]
        private IRegisterCommand RegisterCommand { get; set; }


        private VoiceRecognitionResultCommand voiceRecognitionResult = new VoiceRecognitionResultCommand();

        public VoiceRecognizerBase voiceRecognizer;
        public void Initialized()
        {
            RegisterCommand.Register<SettingVoiceRecognitionCommand>(SettingVoiceRecognitionCommandCallback);

            voiceRecognizer = CreateVoiceRecognizer();
            if (voiceRecognizer != null)
            {
                voiceRecognizer.Initialized(this._mono, url, this.DisponseRecognition);
            }
            else
            {
                Debug.LogWarning("当前平台不支持语音识别功能");
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
        }

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
        }
    }


    public abstract class VoiceRecognizerBase
    {
        public abstract void Initialized(MonoBehaviour _mono,string websocketUrl, Action<string> _recognizerCallback);

        public abstract void SetIsRecogition(bool IsRecogition);
    } 

#if UNITY_EDITOR ||UNITY_STANDALONE || UNITY_ANDROID

    public sealed class VoiceRecognizerNoWebGL : VoiceRecognizerBase
    {
        ClientWebSocket webSocket;

        AudioClip microphoneClip;


        /// <summary>
        /// ������˷���ʱ��
        /// </summary>
        WaitForSeconds samplingInterval = new WaitForSeconds(1 / 5f);

        private MonoBehaviour _mono;

        private Action<string> recognizerCallback;
        private bool IsRecogition;

        public override async void Initialized(MonoBehaviour _mono, string websocketUrl, Action<string> _recognizerCallback)
        {
            this._mono = _mono;
            recognizerCallback = _recognizerCallback;

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

            
            this.webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, default);
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
        private Action<string> recognizerCallback;
        private bool IsRecogition;
        private string websocketUrl;

        // 会话状态管理
        private bool permissionRequested = false;
        private bool sessionActive = false;

        public override void Initialized(MonoBehaviour _mono, string websocketUrl, Action<string> _recognizerCallback)
        {
            this._mono = _mono;
            this.websocketUrl = websocketUrl;
            this.recognizerCallback = _recognizerCallback;

            Debug.Log($"WebGL语音识别初始化: {websocketUrl}");

            // 初始化WebGL WebSocket连接
            InitializeWebGLWebSocket(websocketUrl);

            // 初始化WebGL麦克风（延迟模式，不立即请求权限）
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
            recognizerCallback?.Invoke(result);
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
}