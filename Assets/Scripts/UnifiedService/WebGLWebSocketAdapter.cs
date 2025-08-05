using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// WebGL平台WebSocket适配器
    /// 使用JavaScript WebSocket通过jslib实现
    /// </summary>
    public class WebGLWebSocketAdapter : MonoBehaviour, IWebSocketAdapter
    {
        // JavaScript接口导入
        [DllImport("__Internal")]
        private static extern void InitializeUnifiedWebSocket(string url, string gameObjectName);

        [DllImport("__Internal")]
        private static extern void DisconnectUnifiedWebSocket();

        [DllImport("__Internal")]
        private static extern void SendUnifiedTextMessage(string message);

        [DllImport("__Internal")]
        private static extern void SendUnifiedBinaryMessage(string base64Data);

        [DllImport("__Internal")]
        private static extern bool IsUnifiedWebSocketConnected();

        private bool isConnected = false;
        private string gameObjectName;

        public bool IsConnected => isConnected;

        public event Action<bool> OnConnectionChanged;
        public event Action<string> OnTextMessageReceived;
        public event Action<byte[]> OnBinaryMessageReceived;
        public event Action<string> OnError;

        private void Awake()
        {
            // 🔧 修复：使用宿主GameObject的原始名称，不要改变它
            // JavaScript需要通过原始的GameObject名称（如"GameApp"）来发送回调消息
            gameObjectName = gameObject.name;
            Debug.Log($"🔗 WebGLWebSocketAdapter附加到GameObject: {gameObjectName}");
        }

        public IEnumerator ConnectAsync(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return StartCoroutine(ExecuteWebGLConnectionProcess(url));
#else
            Debug.LogWarning("WebGLWebSocketAdapter只能在WebGL平台运行");
            yield return null;
#endif
        }

        /// <summary>
        /// 执行WebGL连接处理的内部协程
        /// </summary>
        private IEnumerator ExecuteWebGLConnectionProcess(string url)
        {
            Exception caughtException = null;

            try
            {
                Debug.Log($"🔗 WebGLWebSocket开始连接: {url}");
                
                // 调用JavaScript函数初始化WebSocket
                InitializeUnifiedWebSocket(url, gameObjectName);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果初始化WebSocket时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ WebGLWebSocket连接异常: {caughtException}");
                OnError?.Invoke(caughtException.Message);
                yield break;
            }

            // 等待连接建立（通过回调通知）- 在try-catch外部使用yield return
            float timeout = 10.0f;
            float elapsed = 0f;
            
            while (!isConnected && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!isConnected)
            {
                OnError?.Invoke("WebGL WebSocket连接超时");
            }
        }

        public void Disconnect()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                Debug.Log("🔌 WebGLWebSocket断开连接");
                DisconnectUnifiedWebSocket();
                SetConnectionStatus(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ WebGLWebSocket断开异常: {ex}");
            }
#endif
        }

        public IEnumerator SendTextAsync(string message)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!isConnected)
            {
                OnError?.Invoke("WebSocket未连接");
                yield break;
            }

            try
            {
                SendUnifiedTextMessage(message);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"发送文本异常: {ex}");
            }
#endif
            yield return null;
        }

        public IEnumerator SendBinaryAsync(byte[] data)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!isConnected)
            {
                OnError?.Invoke("WebSocket未连接");
                yield break;
            }

            try
            {
                // 将二进制数据转换为Base64字符串
                string base64Data = Convert.ToBase64String(data);
                SendUnifiedBinaryMessage(base64Data);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"发送二进制数据异常: {ex}");
            }
#endif
            yield return null;
        }

        public void Dispose()
        {
            Disconnect();
        }

        // JavaScript回调方法
        public void OnUnifiedWebSocketConnected()
        {
            Debug.Log("✅ WebGLWebSocket连接成功");
            SetConnectionStatus(true);
        }

        public void OnUnifiedWebSocketDisconnected()
        {
            Debug.Log("📪 WebGLWebSocket连接断开");
            SetConnectionStatus(false);
        }

        public void OnUnifiedWebSocketError(string error)
        {
            Debug.LogError($"❌ WebGLWebSocket错误: {error}");
            OnError?.Invoke(error);
            SetConnectionStatus(false);
        }

        public void OnUnifiedWebSocketTextMessage(string message)
        {
            try
            {
                OnTextMessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理WebGL文本消息异常: {ex}");
            }
        }

        public void OnUnifiedWebSocketBinaryMessage(string base64Data)
        {
            try
            {
                byte[] binaryData = Convert.FromBase64String(base64Data);
                OnBinaryMessageReceived?.Invoke(binaryData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ 处理WebGL二进制消息异常: {ex}");
            }
        }

        private void SetConnectionStatus(bool connected)
        {
            if (isConnected != connected)
            {
                isConnected = connected;
                OnConnectionChanged?.Invoke(connected);
            }
        }

        private void OnDestroy()
        {
            Dispose();
        }
    }
}