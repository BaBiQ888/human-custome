using System;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// 标准WebSocket适配器 (非WebGL平台)
    /// 使用.NET的ClientWebSocket实现
    /// </summary>
    public class StandardWebSocketAdapter : IWebSocketAdapter
    {
        private ClientWebSocket webSocket;
        private CancellationTokenSource cancellationTokenSource;
        private MonoBehaviour mono;
        private Coroutine receiveCoroutine;
        private readonly object lockObject = new object();

        public bool IsConnected => webSocket?.State == WebSocketState.Open;

        public event Action<bool> OnConnectionChanged;
        public event Action<string> OnTextMessageReceived;
        public event Action<byte[]> OnBinaryMessageReceived;
        public event Action<string> OnError;

        public StandardWebSocketAdapter(MonoBehaviour mono)
        {
            this.mono = mono;
        }

        public IEnumerator ConnectAsync(string url)
        {
            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return mono.StartCoroutine(ExecuteConnectionProcess(url));
        }

        /// <summary>
        /// 执行连接处理的内部协程
        /// </summary>
        private IEnumerator ExecuteConnectionProcess(string url)
        {
            System.Threading.Tasks.Task connectTask = null;
            Exception caughtException = null;

            try
            {
                Debug.Log($"🔗 StandardWebSocket开始连接: {url}");

                lock (lockObject)
                {
                    cancellationTokenSource?.Cancel();
                    cancellationTokenSource = new CancellationTokenSource();
                    
                    webSocket?.Dispose();
                    webSocket = new ClientWebSocket();
                }

                var uri = new Uri(url);
                connectTask = webSocket.ConnectAsync(uri, cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建连接任务时就发生异常，直接处理
            if (caughtException != null)
            {
                Debug.LogError($"❌ StandardWebSocket连接异常: {caughtException}");
                OnError?.Invoke(caughtException.Message);
                yield break;
            }

            // 等待连接完成或超时（在try-catch外部使用yield return）
            float timeout = 10.0f;
            float elapsed = 0f;
            
            while (!connectTask.IsCompleted && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!connectTask.IsCompleted)
            {
                OnError?.Invoke("连接超时");
                yield break;
            }

            if (connectTask.Exception != null)
            {
                OnError?.Invoke($"连接失败: {connectTask.Exception}");
                yield break;
            }

            if (webSocket.State == WebSocketState.Open)
            {
                Debug.Log("✅ StandardWebSocket连接成功");
                OnConnectionChanged?.Invoke(true);
                
                // 启动消息接收协程
                StartReceiving();
            }
            else
            {
                OnError?.Invoke($"连接失败，状态: {webSocket.State}");
            }
        }

        public void Disconnect()
        {
            try
            {
                Debug.Log("🔌 StandardWebSocket断开连接");

                StopReceiving();

                lock (lockObject)
                {
                    cancellationTokenSource?.Cancel();
                    
                    if (webSocket?.State == WebSocketState.Open)
                    {
                        try
                        {
                            webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "客户端主动断开", CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"⚠️ 关闭WebSocket时出现异常: {ex.Message}");
                        }
                    }

                    webSocket?.Dispose();
                    webSocket = null;
                    cancellationTokenSource?.Dispose();
                    cancellationTokenSource = null;
                }

                OnConnectionChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ StandardWebSocket断开异常: {ex}");
            }
        }

        public IEnumerator SendTextAsync(string message)
        {
            if (!IsConnected)
            {
                OnError?.Invoke("WebSocket未连接");
                yield break;
            }

            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return mono.StartCoroutine(ExecuteSendTextOperation(message));
        }

        /// <summary>
        /// 执行发送文本操作的内部协程
        /// </summary>
        private IEnumerator ExecuteSendTextOperation(string message)
        {
            System.Threading.Tasks.Task sendTask = null;
            Exception caughtException = null;

            try
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                sendTask = webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationTokenSource.Token
                );
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建任务时就发生异常，直接处理
            if (caughtException != null)
            {
                OnError?.Invoke($"发送文本异常: {caughtException}");
                yield break;
            }

            // 等待发送完成（在try-catch外部使用yield return）
            while (!sendTask.IsCompleted)
            {
                yield return null;
            }

            // 处理发送结果
            if (sendTask.Exception != null)
            {
                OnError?.Invoke($"发送文本失败: {sendTask.Exception}");
            }
        }

        public IEnumerator SendBinaryAsync(byte[] data)
        {
            if (!IsConnected)
            {
                OnError?.Invoke("WebSocket未连接");
                yield break;
            }

            // 分离异步等待逻辑，避免在try-catch中使用yield return
            yield return mono.StartCoroutine(ExecuteSendBinaryOperation(data));
        }

        /// <summary>
        /// 执行发送二进制数据操作的内部协程
        /// </summary>
        private IEnumerator ExecuteSendBinaryOperation(byte[] data)
        {
            System.Threading.Tasks.Task sendTask = null;
            Exception caughtException = null;

            try
            {
                sendTask = webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    cancellationTokenSource.Token
                );
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建任务时就发生异常，直接处理
            if (caughtException != null)
            {
                OnError?.Invoke($"发送二进制数据异常: {caughtException}");
                yield break;
            }

            // 等待发送完成（在try-catch外部使用yield return）
            while (!sendTask.IsCompleted)
            {
                yield return null;
            }

            // 处理发送结果
            if (sendTask.Exception != null)
            {
                OnError?.Invoke($"发送二进制数据失败: {sendTask.Exception}");
            }
        }

        private void StartReceiving()
        {
            if (receiveCoroutine != null)
            {
                mono.StopCoroutine(receiveCoroutine);
            }
            receiveCoroutine = mono.StartCoroutine(ReceiveLoop());
        }

        private void StopReceiving()
        {
            if (receiveCoroutine != null)
            {
                mono.StopCoroutine(receiveCoroutine);
                receiveCoroutine = null;
            }
        }

        private IEnumerator ReceiveLoop()
        {
            var buffer = new byte[1024 * 4]; // 4KB缓冲区

            while (IsConnected)
            {
                if (webSocket?.State != WebSocketState.Open)
                {
                    break;
                }

                // 分离异步接收，避免在try-catch中使用yield return
                yield return mono.StartCoroutine(ProcessSingleReceiveOperation(buffer));
                
                // 如果在处理过程中连接状态发生变化，退出循环
                if (!IsConnected || webSocket?.State != WebSocketState.Open)
                {
                    break;
                }
            }

            OnConnectionChanged?.Invoke(false);
        }

        /// <summary>
        /// 处理单个接收操作的内部协程
        /// </summary>
        private IEnumerator ProcessSingleReceiveOperation(byte[] buffer)
        {
            System.Threading.Tasks.Task<WebSocketReceiveResult> receiveTask = null;
            Exception caughtException = null;

            try
            {
                receiveTask = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // 如果创建接收任务时就发生异常，直接处理
            if (caughtException != null)
            {
                OnError?.Invoke($"创建接收任务异常: {caughtException}");
                yield break;
            }

            // 等待接收完成（在try-catch外部使用yield return）
            while (!receiveTask.IsCompleted)
            {
                yield return null;
            }

            // 处理接收结果
            if (receiveTask.Exception != null)
            {
                OnError?.Invoke($"接收消息异常: {receiveTask.Exception}");
                yield break;
            }

            var result = receiveTask.Result;
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string jsonMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                OnTextMessageReceived?.Invoke(jsonMessage);
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                byte[] audioData = new byte[result.Count];
                Array.Copy(buffer, 0, audioData, 0, result.Count);
                OnBinaryMessageReceived?.Invoke(audioData);
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                Debug.Log("📪 WebSocket连接关闭");
                // 这里不能使用break，而是通过修改连接状态来结束循环
                OnConnectionChanged?.Invoke(false);
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}