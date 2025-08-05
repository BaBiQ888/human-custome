using LKZ.UnifiedService;
using System;

namespace LKZ.Commands.UnifiedService
{
    /// <summary>
    /// 统一服务连接命令
    /// </summary>
    public struct ConnectUnifiedServiceCommand
    {
        public string serverUrl;
        public string username;
        public string asrMode;
    }

    /// <summary>
    /// 统一服务断开连接命令
    /// </summary>
    public struct DisconnectUnifiedServiceCommand
    {
    }

    /// <summary>
    /// 发送音频数据命令
    /// </summary>
    public struct SendAudioDataCommand
    {
        public byte[] audioData;
    }

    /// <summary>
    /// 发送文本命令（绕过ASR）
    /// </summary>
    public struct SendTextDirectlyCommand
    {
        public string text;
    }

    /// <summary>
    /// 统一服务状态变化通知
    /// </summary>
    public struct UnifiedServiceStateChangedCommand
    {
        public UnifiedServiceState previousState;
        public UnifiedServiceState currentState;
        public string message;
    }

    /// <summary>
    /// ASR识别结果通知
    /// </summary>
    public struct UnifiedASRResultCommand
    {
        public string text;
        public bool isFinal;
    }

    /// <summary>
    /// LLM回复内容通知
    /// </summary>
    public struct UnifiedLLMResultCommand
    {
        public string text;
        public bool isFirst;
        public bool isEnd;
    }

    /// <summary>
    /// 音频流开始通知
    /// </summary>
    public struct UnifiedAudioStartCommand
    {
        public string text;
        public string fileName;
    }

    /// <summary>
    /// 音频数据接收通知
    /// </summary>
    public struct UnifiedAudioDataCommand
    {
        public byte[] audioData;
        public string associatedText;
    }

    /// <summary>
    /// 音频流结束通知
    /// </summary>
    public struct UnifiedAudioEndCommand
    {
        public string text;
        public int totalSize;
    }

    /// <summary>
    /// 统一服务错误通知
    /// </summary>
    public struct UnifiedServiceErrorCommand
    {
        public string errorMessage;
        public Exception exception;
    }
}