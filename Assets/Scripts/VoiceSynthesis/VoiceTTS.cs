using LKZ.Utilitys;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace LKZ.VoiceSynthesis
{ 

    /// <summary>
    ///  语音合成
    /// </summary>
    public static class VoiceTTS
    {
        public const string ErrorMess = "语音识别出错了";

        static int voiceID;
        public static int VoiceID
        {
            get => voiceID; set
            {
                voiceID = value;
                DataSave.SetVoiceID(voiceID);
            }
        }

        static VoiceTTS()
        {
            voiceID = DataSave.GetVoiceID();
        }

        public static IEnumerator Synthesis(string content)
        {
            // 构建URL，添加URL编码处理中文字符
            string url = $"http://47.79.16.41:5001/tts?content={UnityWebRequest.EscapeURL(content)}&id='abin'";

            Debug.Log($"请求TTS: {url}");

            using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
            {
                // 设置30秒超时
                request.timeout = 30;

                var result = request.SendWebRequest();
                while (!result.isDone)
                {
                    yield return null;
                }

                // 使用新的错误检查方式
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip _audioClip = DownloadHandlerAudioClip.GetContent(request);

                    if (_audioClip != null)
                    {
                        Debug.Log($"TTS合成成功，音频长度: {_audioClip.length}秒");
                        yield return _audioClip;
                    }
                    else
                    {
                        Debug.LogError("TTS返回的音频剪辑为空");
                        yield return ErrorMess;
                    }
                }
                else
                {
                    // 详细的错误日志，便于调试
                    Debug.LogError($"TTS请求失败: {request.error}");
                    Debug.LogError($"响应代码: {request.responseCode}");
                    Debug.LogError($"响应内容: {request.downloadHandler.text}");
                    yield return ErrorMess;
                }
            }
        }
    }
}
