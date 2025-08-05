using System;
using UnityEngine;

namespace LKZ.UnifiedService
{
    /// <summary>
    /// 统一服务消息基类
    /// </summary>
    [Serializable]
    public abstract class UnifiedServiceMessage
    {
        public string type;
        public float timestamp;
    }

    /// <summary>
    /// 发送消息 - 开始对话
    /// </summary>
    [Serializable]
    public class StartConversationMessage
    {
        public string action = "start_conversation";
        public string username;
        public string asr_mode;
        public string conversation_id;
    }

    /// <summary>
    /// 发送消息 - 文本输入（绕过ASR）
    /// </summary>
    [Serializable]
    public class TextInputMessage
    {
        public string action = "text_input";
        public string text;
        public float timestamp;
    }

    /// <summary>
    /// 发送消息 - 停止对话
    /// </summary>
    [Serializable]
    public class StopConversationMessage
    {
        public string action = "stop_conversation";
    }

    /// <summary>
    /// 发送消息 - 获取状态
    /// </summary>
    [Serializable]
    public class GetStatusMessage
    {
        public string action = "get_status";
    }

    /// <summary>
    /// 接收消息 - ASR识别结果
    /// </summary>
    [Serializable]
    public class ASRResultMessage : UnifiedServiceMessage
    {
        public string text;
        public bool is_final;
    }

    /// <summary>
    /// 接收消息 - LLM回复
    /// </summary>
    [Serializable]
    public class LLMResultMessage : UnifiedServiceMessage
    {
        public string text;
        public bool is_first;
        public bool is_end;
    }

    /// <summary>
    /// 接收消息 - 音频开始
    /// </summary>
    [Serializable]
    public class AudioStartMessage : UnifiedServiceMessage
    {
        public string text;
        public string file_name;
    }

    /// <summary>
    /// 接收消息 - 音频结束
    /// </summary>
    [Serializable]
    public class AudioEndMessage : UnifiedServiceMessage
    {
        public string text;
        public int total_size;
    }

    /// <summary>
    /// 接收消息 - 错误消息
    /// </summary>
    [Serializable]
    public class ErrorMessage : UnifiedServiceMessage
    {
        public string message;
    }

    /// <summary>
    /// 接收消息 - 通用状态消息
    /// </summary>
    [Serializable]
    public class StatusMessage : UnifiedServiceMessage
    {
        public string status;
        public string message;
    }

    /// <summary>
    /// 消息类型常量
    /// </summary>
    public static class MessageTypes
    {
        // 接收消息类型
        public const string ASR_RESULT = "asr_result";
        public const string LLM_RESULT = "llm_result";
        public const string AUDIO_START = "audio_start";
        public const string AUDIO_END = "audio_end";
        public const string ERROR = "error";
        
        // 发送动作类型
        public const string START_CONVERSATION = "start_conversation";
        public const string TEXT_INPUT = "text_input";
        public const string STOP_CONVERSATION = "stop_conversation";
        public const string GET_STATUS = "get_status";
    }

    /// <summary>
    /// 服务状态枚举
    /// </summary>
    public enum UnifiedServiceState
    {
        Disconnected,       // 未连接
        Connecting,         // 连接中
        Connected,          // 已连接
        ConversationActive, // 对话活跃
        Error,              // 错误状态
        Reconnecting        // 重连中
    }
}