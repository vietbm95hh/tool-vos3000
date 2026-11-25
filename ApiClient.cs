using System.Net;
using System.Text;
using System.Text.Json;

namespace Vos3000Agent
{
    public class ApiError : Exception
    {
        public ApiError(string message) : base(message) { }
        public ApiError(string message, Exception inner) : base(message, inner) { }
    }


    public class ApiClient
    {
        private readonly Settings _settings;
        private readonly HttpClient _http;

        public ApiClient(Settings settings)
        {
            _settings = settings;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    settings.verify_tls
                        ? null
                        : (sender, cert, chain, errors) => true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            _http.DefaultRequestHeaders.TryAddWithoutValidation(HttpKey.Authorization, HttpKey.BearerPrefix + (settings.agent_token ?? ""));
            _http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(HttpKey.ContentTypeJson));
            _http.DefaultRequestHeaders.TryAddWithoutValidation(HttpKey.AgentId, settings.agent_id ?? "");
        }

        private string Url(string path)
        {
            string baseUrl = (_settings.api_base_url ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                throw new ApiError(Msg.Api.BaseUrlNotConfigured);
            bool localhost = baseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                          || baseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !localhost)
                throw new ApiError(Msg.Api.RequireHttps);
            return baseUrl + path;
        }

        private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object body = null)
        {
            if (string.IsNullOrWhiteSpace(_settings.agent_token))
                throw new ApiError(Msg.Api.TokenNotConfigured);

            using var req = new HttpRequestMessage(method, Url(path));
            if (body != null)
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8, HttpKey.ContentTypeJson);

            try
            {
                using var resp = await _http.SendAsync(req);
                string content = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    throw new ApiError(string.Format(Msg.Api.HttpError, (int)resp.StatusCode, Truncate(content, 300)));

                if (string.IsNullOrWhiteSpace(content)) return default;

                JsonElement root;
                try { root = JsonSerializer.Deserialize<JsonElement>(content); }
                catch { throw new ApiError(Msg.Api.InvalidJson); }

                // Trả về root.data nếu có, ngược lại trả về chính root.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(ApiField.Data, out var data))
                    return data;
                return root;
            }
            catch (ApiError) { throw; }
            catch (Exception e) { throw new ApiError(string.Format(Msg.Api.ConnectionError, e.Message), e); }
        }

        public async Task<JsonElement?> NextJobAsync()
        {
            string path = _settings.api_paths.next_job;
            var data = await RequestAsync(HttpMethod.Get, path + "?" + HttpKey.AgentIdQuery + "=" + Uri.EscapeDataString(_settings.agent_id ?? ""));
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(ApiField.Job, out var job))
                return job;
            if (data.ValueKind == JsonValueKind.Null || data.ValueKind == JsonValueKind.Undefined)
                return null;
            return data;
        }

        public async Task<JsonElement> ClaimAsync(string jobId)
        {
            string path = _settings.api_paths.claim.Replace(HttpKey.JobIdPlaceholder, jobId);
            return await RequestAsync(HttpMethod.Post, path, new { agent_id = _settings.agent_id });
        }

        public async Task<JsonElement> ReportAsync(string jobId, string status, string message, object details = null)
        {
            string path = _settings.api_paths.result.Replace(HttpKey.JobIdPlaceholder, jobId);
            return await RequestAsync(HttpMethod.Post, path,
                new { agent_id = _settings.agent_id, status, message, details = details ?? new { } });
        }

        public async Task<JsonElement> HeartbeatAsync(string status = Status.Online, string message = "")
        {
            string path = _settings.api_paths.heartbeat;
            return await RequestAsync(HttpMethod.Post, path,
                new { agent_id = _settings.agent_id, status, message });
        }

        private static string Truncate(string s, int max)
            => s == null ? "" : s.Length <= max ? s : s.Substring(0, max);
    }
}
