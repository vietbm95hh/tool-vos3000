using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace Vos3000Agent
{
    public class HealthResult
    {
        public DateTime Time { get; set; }
        public bool Ok { get; set; }
        public int? StatusCode { get; set; }
        public long ElapsedMs { get; set; }
        public string Url { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Healthcheck API nguồn: ping domain + endpoint định kỳ (mặc định 5 giây/lần).
    /// Không phụ thuộc ApiClient để có thể kiểm tra cả khi chưa có token.
    /// </summary>
    public class HealthChecker
    {
        private CancellationTokenSource _cts;
        private Task _task;
        private volatile bool _running;

        public bool Running => _running;
        public string Domain { get; private set; } = "";
        public string Endpoint { get; private set; } = "/health";        public int IntervalSeconds { get; private set; } = 5;
        public int TimeoutSeconds { get; private set; } = 5;
        public bool VerifyTls { get; private set; } = true;

        /// <summary>Bắn kết quả về UI. Được gọi trên thread nền.</summary>
        public event Action<HealthResult> Result;

        public void Configure(Settings s)
        {
            Domain = (s.health_domain ?? "").Trim().TrimEnd('/');
            Endpoint = string.IsNullOrWhiteSpace(s.health_endpoint) ? "/health" : s.health_endpoint.Trim();
            if (!Endpoint.StartsWith("/")) Endpoint = "/" + Endpoint;
            IntervalSeconds = Math.Clamp(s.health_interval_seconds <= 0 ? 5 : s.health_interval_seconds, 1, 3600);
            TimeoutSeconds = Math.Clamp(s.health_timeout_seconds <= 0 ? 5 : s.health_timeout_seconds, 1, 120);
            VerifyTls = s.verify_tls;
        }

        public static string BuildUrl(string domain, string endpoint)
        {
            domain = (domain ?? "").Trim().TrimEnd('/');
            endpoint = (endpoint ?? "").Trim();
            if (endpoint.Length > 0 && !endpoint.StartsWith("/")) endpoint = "/" + endpoint;
            return domain + endpoint;
        }

        public string CurrentUrl => BuildUrl(Domain, Endpoint);

        public void Start()
        {
            if (_running) return;
            if (string.IsNullOrWhiteSpace(Domain))
            {
                Result?.Invoke(new HealthResult
                {
                    Time = DateTime.Now,
                    Ok = false,
                    Url = "",
                    Message = Msg.Health.DomainNotSet
                });
                return;
            }

            _cts = new CancellationTokenSource();
            _running = true;
            _task = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _running = false;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    VerifyTls ? null : (sender, cert, chain, errors) => true
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            string url = CurrentUrl;

            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                HealthResult r;
                try
                {
                    using var resp = await http.GetAsync(url, ct);
                    sw.Stop();
                    r = new HealthResult
                    {
                        Time = DateTime.Now,
                        Ok = resp.IsSuccessStatusCode,
                        StatusCode = (int)resp.StatusCode,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Url = url,
                        Message = resp.IsSuccessStatusCode
                            ? string.Format(Msg.Health.Ok, (int)resp.StatusCode, resp.ReasonPhrase)
                            : string.Format(Msg.Health.HttpError, (int)resp.StatusCode, resp.ReasonPhrase)
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    sw.Stop();
                    r = new HealthResult
                    {
                        Time = DateTime.Now,
                        Ok = false,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Url = url,
                        Message = e is TaskCanceledException
                            ? string.Format(Msg.Health.Timeout, TimeoutSeconds)
                            : e.Message
                    };
                }

                Result?.Invoke(r);

                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }

            _running = false;
        }
    }
}
