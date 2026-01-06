using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VPet_Simulator.Core;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public class VisionAPIClient
    {
        private readonly HttpClient _httpClient = new HttpClient();
        public string ApiKey { get; set; } = string.Empty;
        public string ApiEndpoint { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string SystemPrompt { get; set; } = "你是一个可爱的桌宠,正在观察用户的屏幕。请根据用户当前的活动窗口和屏幕截图,给出一段简短、有趣且符合桌宠身份的吐槽或鼓励。使用中文回复,字数控制在30字以内。";

        private static void DebugLog(string message) => DebugLogger.Log("[识图] " + message);

        private static string TruncateForLog(string? s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "...";
        }



        public async Task AnalyzeImageStreamAsync(string base64Image, string windowTitle, SayInfoWithStream sayInfo)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                DebugLog("分析图片(流式)：密钥为空");
                sayInfo.UpdateText("API 未设置密钥。");
                sayInfo.FinishGenerate();
                return;
            }

            if (string.IsNullOrWhiteSpace(ApiEndpoint))
            {
                DebugLog("分析图片(流式)：接口地址为空");
                sayInfo.UpdateText("API Endpoint 未设置。");
                sayInfo.FinishGenerate();
                return;
            }

            if (string.IsNullOrWhiteSpace(ModelName))
            {
                DebugLog("分析图片(流式)：模型名为空");
                sayInfo.UpdateText("模型名称未设置。");
                sayInfo.FinishGenerate();
                return;
            }

            if (string.IsNullOrWhiteSpace(base64Image))
            {
                DebugLog("分析图片(流式)：图片编码数据为空");
                sayInfo.UpdateText("未能获取到图片数据。");
                sayInfo.FinishGenerate();
                return;
            }

            DebugLog($"分析图片(流式)：开始 窗口标题='{TruncateForLog(windowTitle, 120)}' 图片数据长度={base64Image.Length} 接口='{TruncateForLog(ApiEndpoint, 160)}' 模型='{ModelName}' 密钥=<已设置>");

            string endpoint = BuildGeminiGenerateContentEndpoint(ApiEndpoint, ModelName);

            object requestBody = new
            {
                contents = new object[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "image/jpeg",
                                    data = base64Image
                                }
                            },
                            new
                            {
                                text = SystemPrompt
                            }
                        }
                    }
                },
            };

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-goog-api-key", ApiKey);
            var requestJson = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var json = line.Substring(5).Trim();
                        if (string.IsNullOrEmpty(json) || json == "[DONE]") break;

                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                            {
                                var candidate = candidates[0];
                                if (candidate.TryGetProperty("content", out var content) &&
                                    content.TryGetProperty("parts", out var parts) &&
                                    parts.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var part in parts.EnumerateArray())
                                    {
                                        if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                                        {
                                            var text = textElement.GetString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                sayInfo.UpdateText(text);
                                            }
                                        }
                                    }
                                }

                                if (candidate.TryGetProperty("finishReason", out var finishReason) && finishReason.ValueKind == JsonValueKind.String)
                                {
                                    var reason = finishReason.GetString();
                                    if (!string.IsNullOrEmpty(reason) && reason != "STOP")
                                    {
                                        DebugLog($"分析图片(流式)：FinishReason={reason}");
                                    }
                                }
                            }

                            if (root.TryGetProperty("usageMetadata", out var usageMetadata))
                            {
                                if (usageMetadata.TryGetProperty("totalTokenCount", out var totalTokens))
                                {
                                    DebugLog($"分析图片(流式)：Token usage Total={totalTokens.GetInt32()}");
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore parse errors for partial lines or keep-alives
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"分析图片(流式)：异常 {ex.GetType().Name}: {ex.Message}");
                sayInfo.UpdateText($"\n[Error: {ex.Message}]");
            }
            finally
            {
                sayInfo.FinishGenerate();
            }
        }

        private static string BuildGeminiGenerateContentEndpoint(string apiEndpoint, string modelName)
        {
            var endpoint = (apiEndpoint ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("Empty ApiEndpoint", nameof(apiEndpoint));

            var model = (modelName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Empty ModelName", nameof(modelName));

            string targetSuffix = ":streamGenerateContent?alt=sse";

            endpoint = endpoint.TrimEnd('/');
            return endpoint + "/v1beta/models/" + model + targetSuffix;
        }
    }
}
