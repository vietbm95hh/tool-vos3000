using System.Text.Json;

namespace Vos3000Agent
{
    /// <summary>
    /// Worker polling job từ API và xử lý qua GuiDriver. Tương đương worker.py.
    /// </summary>
    public class PollWorker
    {
        public enum JobEventKind
        {
            Log, Job, Success, Failed, Unknown, Simulated, Error, Worker, Poll
        }

        public class JobEvent
        {
            public JobEventKind Kind { get; set; }
            public string Message { get; set; }
            public Dictionary<string, object> Job { get; set; }
        }

        private readonly Settings _settings;
        private readonly Dictionary<string, GuiProfile> _profiles;
        private readonly Action<JobEvent> _onEvent;

        private CancellationTokenSource _cts;
        private Task _task;
        private volatile bool _running;
        private readonly ApiClient _api;
        private readonly GuiDriver _gui;

        public bool Running => _running;

        public PollWorker(Settings settings, Dictionary<string, GuiProfile> profiles, Action<JobEvent> onEvent)
        {
            _settings = settings;
            _profiles = profiles;
            _onEvent = onEvent ?? (_ => { });
            _api = new ApiClient(settings);
            _gui = new GuiDriver(settings, profiles, msg => _onEvent(new JobEvent { Kind = JobEventKind.Log, Message = msg }));
        }

        public void Start()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private void Emit(JobEventKind kind, string message, Dictionary<string, object> job = null)
            => _onEvent(new JobEvent { Kind = kind, Message = message, Job = job });

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _api.HeartbeatAsync(Status.Online, Status.Polling);
                    var jobElement = await _api.NextJobAsync();

                    if (jobElement.HasValue && jobElement.Value.ValueKind == JsonValueKind.Object)
                    {
                        var job = ToDict(jobElement.Value);
                        await ProcessAsync(job);
                    }
                    else
                    {
                        Emit(JobEventKind.Poll, Msg.Worker.NoJob);
                    }
                }
                catch (Exception e)
                {
                    Emit(JobEventKind.Error, string.Format(Msg.Worker.PollError, e.Message));
                }

                int interval = Math.Clamp(_settings.poll_interval_seconds, 3, 300);
                try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
                catch (OperationCanceledException) { break; }
            }

            _running = false;
            Emit(JobEventKind.Worker, Msg.Worker.Stopped);
        }

        private async Task ProcessAsync(Dictionary<string, object> job)
        {
            string jobId = Convert.ToString(job.GetValueOrDefault(JobField.JobId)) ?? "";
            if (string.IsNullOrEmpty(jobId))
            {
                Emit(JobEventKind.Failed, Msg.Worker.NoJobId, job);
                return;
            }

            Emit(JobEventKind.Job, string.Format(Msg.Worker.JobReceived, jobId, job.GetValueOrDefault(JobField.Type)), job);

            try
            {
                var claim = await _api.ClaimAsync(jobId);
                if (claim.ValueKind == JsonValueKind.Object
                    && claim.TryGetProperty(ApiField.Claimed, out var claimed)
                    && claimed.ValueKind == JsonValueKind.False)
                {
                    Emit(JobEventKind.Log, string.Format(Msg.Worker.JobClaimedByOther, jobId));
                    return;
                }

                var result = _gui.Execute(job, simulate: _settings.simulation_mode);

                string status = result.Status == Status.Simulated ? Status.Simulated : result.Status;

                await _api.ReportAsync(jobId, status, result.Message, result.Details);

                JobEventKind kind = status switch
                {
                    Status.Success => JobEventKind.Success,
                    Status.Failed => JobEventKind.Failed,
                    Status.Unknown or Status.Blocked => JobEventKind.Unknown,
                    _ => JobEventKind.Simulated
                };
                Emit(kind, string.Format(Msg.Worker.Result, jobId, status, result.Message), job);
            }
            catch (Exception e)
            {
                Emit(JobEventKind.Error, string.Format(Msg.Worker.ProcessError, jobId, e.Message), job);
            }
        }

        private static Dictionary<string, object> ToDict(JsonElement el)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in el.EnumerateObject())
                dict[prop.Name] = PropValue(prop.Value);
            return dict;
        }

        private static object PropValue(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object: return ToDict(el);
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var x in el.EnumerateArray()) list.Add(PropValue(x));
                    return list;
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    return el.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
