using System;
using System.Collections;
using UnityEngine;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// WebSocket适配器接口
    /// 为不同平台提供统一的WebSocket抽象
    /// </summary>
    public interface IWebSocketAdapter
    {
        /// <summary>
        /// 连接状态
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event Action<bool> OnConnectionChanged;

        /// <summary>
        /// 接收到文本消息事件
        /// </summary>
        event Action<string> OnTextMessageReceived;

        /// <summary>
        /// 接收到二进制消息事件
        /// </summary>
        event Action<byte[]> OnBinaryMessageReceived;

        /// <summary>
        /// 连接错误事件
        /// </summary>
        event Action<string> OnError;

        /// <summary>
        /// 连接到WebSocket服务器
        /// </summary>
        /// <param name="url">服务器URL</param>
        /// <returns>连接协程</returns>
        IEnumerator ConnectAsync(string url);

        /// <summary>
        /// 断开WebSocket连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 发送文本消息
        /// </summary>
        /// <param name="message">文本内容</param>
        /// <returns>发送协程</returns>
        IEnumerator SendTextAsync(string message);

        /// <summary>
        /// 发送二进制消息
        /// </summary>
        /// <param name="data">二进制数据</param>
        /// <returns>发送协程</returns>
        IEnumerator SendBinaryAsync(byte[] data);

        /// <summary>
        /// 清理资源
        /// </summary>
        void Dispose();
    }
}