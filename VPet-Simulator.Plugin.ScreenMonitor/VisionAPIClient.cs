using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet_Simulator.Plugin.ScreenMonitor
{
    public class VisionAPIClient
    {
        private readonly HttpClient _httpClient = new HttpClient();
        public string ApiKey { get; set; } = string.Empty;
        public string ApiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string ModelName { get; set; } = "gpt-4o";

        public async Task<string> AnalyzeImageAsync(string base64Image, string windowTitle)
        {
            if (string.IsNullOrEmpty(ApiKey))
                return "API Key is not set.";

            object requestBody = new
            {
                model = ModelName,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是一个可爱的桌宠，正在观察用户的屏幕。请根据用户当前的活动窗口和屏幕截图，给出一段简短、有趣且符合桌宠身份的吐槽或鼓励。回复字数控制在30字以内。"
                    },
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = $"当前活动窗口是: {windowTitle}" },
                            new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64Image}" } }
                        }
                    }
                },
                max_tokens = 100
            };

            var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
            request.Headers.Add("Authorization", $"Bearer {ApiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "桌宠发呆中...";
            }
            catch (Exception ex)
            {
                return $"分析失败: {ex.Message}";
            }
        }
    }
}
