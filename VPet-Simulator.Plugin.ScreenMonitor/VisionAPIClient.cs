using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public class VisionAPIClient
    {
        private readonly HttpClient _httpClient = new HttpClient();
        public string ApiKey { get; set; } = string.Empty;
        public string ApiEndpoint { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;

        private static void DebugLog(string message) => DebugLogger.Log("[识图] " + message);

        private static string TruncateForLog(string? s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "...";
        }

        public async Task<string> AnalyzeImageAsync(string base64Image, string windowTitle)
        {
            if (string.IsNullOrEmpty(ApiKey))
            {
                DebugLog("分析图片：密钥为空");
                return "API Key is not set.";
            }

            if (string.IsNullOrWhiteSpace(ApiEndpoint))
            {
                DebugLog("分析图片：接口地址为空");
                return "API Endpoint is not set.";
            }

            if (string.IsNullOrWhiteSpace(ModelName))
            {
                DebugLog("分析图片：模型名为空");
                return "Model Name is not set.";
            }

            if (string.IsNullOrWhiteSpace(base64Image))
            {
                DebugLog("分析图片：图片编码数据为空");
                return "Image data is empty.";
            }

            DebugLog($"分析图片：开始 窗口标题='{TruncateForLog(windowTitle, 120)}' 图片数据长度={base64Image.Length} 接口='{TruncateForLog(ApiEndpoint, 160)}' 模型='{ModelName}' 密钥=<已设置>");

            string endpoint;
            try
            {
                endpoint = BuildGeminiGenerateContentEndpoint(ApiEndpoint, ModelName);
                DebugLog($"分析图片：最终接口='{endpoint}'");
            }
            catch (Exception ex)
            {
                DebugLog($"分析图片：接口构建失败 {ex.GetType().Name}: {ex.Message}");
                return $"API Endpoint invalid: {ex.Message}";
            }

            // Gemini 官方格式：generateContent + inline_data
            // 参考：https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
            object requestBody = new
            {
                system_instruction = new
                {
                    parts = new object[]
                    {
                        new
                        {
                            text = "你是一个可爱的桌宠，正在观察用户的屏幕。请根据用户当前的活动窗口和屏幕截图，给出一段简短、有趣且符合桌宠身份的吐槽或鼓励。使用中文回复，字数控制在30字以内。"
                        }
                    }
                },
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
                                text = $"当前活动窗口是: {windowTitle}" 
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    maxOutputTokens = 100
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-goog-api-key", ApiKey);
            var requestJson = JsonSerializer.Serialize(requestBody);
            DebugLog($"分析图片：请求 JSON 长度={requestJson.Length}");
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            try
            {
                var sw = Stopwatch.StartNew();
                var response = await _httpClient.SendAsync(request);
                sw.Stop();

                DebugLog($"分析图片：HTTP 状态={(int)response.StatusCode} {response.StatusCode} 耗时毫秒={sw.ElapsedMilliseconds}");

                var responseString = await response.Content.ReadAsStringAsync();
                DebugLog($"分析图片：响应长度={responseString.Length} 内容='{TruncateForLog(responseString, 800)}'");

                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(responseString);

                // Gemini 响应：candidates[0].content.parts[].text
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    DebugLog($"分析图片：候选数量={candidates.GetArrayLength()}");
                    var candidate0 = candidates[0];
                    if (candidate0.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        DebugLog($"分析图片：分段数量={parts.GetArrayLength()}");
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                            {
                                var t = textEl.GetString();
                                if (!string.IsNullOrWhiteSpace(t))
                                    sb.Append(t);
                            }
                        }
                        var merged = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(merged))
                        {
                            DebugLog($"分析图片：成功 文本长度={merged.Length} 文本='{TruncateForLog(merged, 200)}'");
                            return merged;
                        }
                    }
                }

                // 错误响应可能为：{ "error": { "message": "..." } }
                if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                {
                    DebugLog($"分析图片：接口返回错误='{TruncateForLog(msg.GetString(), 240)}'");
                    return $"分析失败: {msg.GetString()}";
                }

                DebugLog("分析图片：响应中未找到候选/错误字段，返回兜底文案");
                return "桌宠发呆中...";
            }
            catch (Exception ex)
            {
                DebugLog($"分析图片：异常 {ex.GetType().Name}: {ex.Message}");
                return $"分析失败: {ex.Message}";
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

            // 允许使用模板形式的 ApiEndpoint，例如：.../models/{model}:generateContent
            if (endpoint.Contains("{model}", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Replace("{model}", model, StringComparison.OrdinalIgnoreCase);
                return endpoint;
            }

            endpoint = endpoint.TrimEnd('/');

            // 如果用户已经粘贴了完整的 generateContent 地址，则尽量沿用（若包含模型名则尝试替换为 ModelName）。
            if (endpoint.Contains(":generateContent", StringComparison.OrdinalIgnoreCase))
            {
                var idx = endpoint.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var prefix = endpoint.Substring(0, idx + "/models/".Length);
                    var after = endpoint.Substring(idx + "/models/".Length);
                    var suffixIdx = after.IndexOf(":generateContent", StringComparison.OrdinalIgnoreCase);
                    if (suffixIdx >= 0)
                    {
                        var suffix = after.Substring(suffixIdx);
                        return prefix + model + suffix;
                    }
                }
                return endpoint;
            }

            // 如果 endpoint 指向 /models/{xxx}（不带 :generateContent），则补上 :generateContent 并替换模型名。
            if (endpoint.Contains("/models/", StringComparison.OrdinalIgnoreCase))
            {
                var idx = endpoint.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
                var prefix = endpoint.Substring(0, idx + "/models/".Length);
                return prefix + model + ":generateContent";
            }

            // 如果 endpoint 是基础地址，则拼出官方路径。
            // 支持：
            // - https://generativelanguage.googleapis.com
            // - https://generativelanguage.googleapis.com/v1beta
            // - https://generativelanguage.googleapis.com/v1beta/
            if (endpoint.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
                return endpoint + "/models/" + model + ":generateContent";

            return endpoint + "/v1beta/models/" + model + ":generateContent";
        }
    }
}
