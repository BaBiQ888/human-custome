using UnityEngine;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// WebSocket适配器工厂
    /// 根据当前平台创建合适的WebSocket适配器
    /// </summary>
    public static class WebSocketAdapterFactory
    {
        /// <summary>
        /// 创建适合当前平台的WebSocket适配器
        /// </summary>
        /// <param name="mono">MonoBehaviour实例，用于协程管理</param>
        /// <returns>IWebSocketAdapter实现</returns>
        public static IWebSocketAdapter CreateAdapter(MonoBehaviour mono)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL平台：使用JavaScript WebSocket
            Debug.Log("🌐 创建WebGL WebSocket适配器");
            var webglAdapter = mono.gameObject.AddComponent<WebGLWebSocketAdapter>();
            return webglAdapter;
#else
            // 其他平台：使用.NET ClientWebSocket
            Debug.Log("🖥️ 创建标准WebSocket适配器");
            return new StandardWebSocketAdapter(mono);
#endif
        }

        /// <summary>
        /// 获取当前平台的WebSocket实现名称
        /// </summary>
        /// <returns>平台名称</returns>
        public static string GetPlatformName()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "WebGL";
#else
            return "Standard";
#endif
        }

        /// <summary>
        /// 检查当前平台是否支持WebSocket
        /// </summary>
        /// <returns>是否支持</returns>
        public static bool IsPlatformSupported()
        {
            // 所有支持的平台都返回true
            // 未来如果有不支持的平台可以在这里添加检查
            return true;
        }
    }
}