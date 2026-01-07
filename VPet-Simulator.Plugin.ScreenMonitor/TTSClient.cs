using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    /// <summary>
    /// 通义千问语音合成客户端
    /// </summary>
    public class TTSClient
    {
        private readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// DashScope API Key
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// TTS 模型名称，默认 qwen3-tts-flash
        /// </summary>
        public string ModelName { get; set; } = "qwen3-tts-flash";

        /// <summary>
        /// 音色，默认 Cherry
        /// </summary>
        public string Voice { get; set; } = "Cherry";

        /// <summary>
        /// 语言类型，默认 Chinese
        /// </summary>
        public string LanguageType { get; set; } = "Chinese";

        /// <summary>
        /// 是否启用 TTS
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// API 端点
        /// </summary>
        private const string ApiEndpoint = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

        private static void DebugLog(string message) => DebugLogger.Log("[TTS] " + message);

        /// <summary>
        /// 将文本转换为语音并保存到临时文件
        /// </summary>
        /// <param name="text">要合成的文本</param>
        /// <returns>音频文件路径，失败返回 null</returns>
        public async Task<string?> SynthesizeToFileAsync(string text)
        {
            if (!Enabled)
            {
                DebugLog("TTS 未启用");
                return null;
            }

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                DebugLog("API Key 未设置");
                return null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                DebugLog("文本为空");
                return null;
            }

            // 限制文本长度，避免过长
            if (text.Length > 500)
            {
                text = text.Substring(0, 500);
                DebugLog("文本过长，已截断至 500 字符");
            }

            DebugLog($"开始合成语音：文本长度={text.Length} 模型={ModelName} 音色={Voice}");

            try
            {
                var requestBody = new
                {
                    model = ModelName,
                    input = new
                    {
                        text = text,
                        voice = Voice,
                        language_type = LanguageType
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
                request.Headers.Add("Authorization", $"Bearer {ApiKey}");
                var requestJson = JsonSerializer.Serialize(requestBody);
                request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                DebugLog($"发送请求到 {ApiEndpoint}");

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    DebugLog($"API 请求失败：{response.StatusCode} - {responseContent}");
                    return null;
                }

                DebugLog($"收到响应：{responseContent.Substring(0, Math.Min(200, responseContent.Length))}...");

                // 解析响应获取音频数据
                using var doc = JsonDocument.Parse(responseContent);
                var root = doc.RootElement;

                // 响应格式: {"output": {"audio": {"data": "", "url": "...", "id": "...", "expires_at": ...}}, ...}
                if (root.TryGetProperty("output", out var output) &&
                    output.TryGetProperty("audio", out var audioElement))
                {
                    // 尝试从 URL 下载音频
                    if (audioElement.TryGetProperty("url", out var urlElement))
                    {
                        var audioUrl = urlElement.GetString();
                        if (!string.IsNullOrEmpty(audioUrl))
                        {
                            DebugLog($"从 URL 下载音频：{audioUrl.Substring(0, Math.Min(100, audioUrl.Length))}...");

                            var audioBytes = await _httpClient.GetByteArrayAsync(audioUrl);
                            var tempPath = Path.Combine(Path.GetTempPath(), $"vpet_tts_{Guid.NewGuid()}.wav");
                            await File.WriteAllBytesAsync(tempPath, audioBytes);

                            DebugLog($"语音合成成功，保存到：{tempPath} 大小={audioBytes.Length} bytes");
                            return tempPath;
                        }
                    }

                    // 兼容：尝试从 data 字段获取 base64 编码的音频
                    if (audioElement.TryGetProperty("data", out var dataElement))
                    {
                        var audioBase64 = dataElement.GetString();
                        if (!string.IsNullOrEmpty(audioBase64))
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), $"vpet_tts_{Guid.NewGuid()}.wav");
                            var audioBytes = Convert.FromBase64String(audioBase64);
                            await File.WriteAllBytesAsync(tempPath, audioBytes);

                            DebugLog($"语音合成成功（base64），保存到：{tempPath} 大小={audioBytes.Length} bytes");
                            return tempPath;
                        }
                    }
                }

                DebugLog("响应中未找到音频数据");
                return null;
            }
            catch (Exception ex)
            {
                DebugLog($"语音合成异常：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 清理临时音频文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        public static void CleanupTempFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    DebugLog($"已删除临时文件：{filePath}");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"删除临时文件失败：{ex.Message}");
            }
        }
    }
}
