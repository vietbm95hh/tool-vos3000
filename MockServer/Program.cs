// Mock API server để test full luồng VOS3000 Agent (healthcheck + job khai số).
// Chạy: dotnet run --project MockServer   (mặc định http://127.0.0.1:5099)

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:5099");

var app = builder.Build();

// ---- Trạng thái giả lập ----
var state = new MockState();

// Mock data: trải đều 4 nghiệp vụ để thấy đủ các nhánh xử lý
state.EnqueueJob("add_number_section", "84901234000", "84901234099", "DEMO001");
state.EnqueueJob("add_number_section", "84907654000", "84907654099", "DEMO002");
state.EnqueueJob("add_number_section", "84908881000", "84908881049", "DEMO003");
state.EnqueueJob("lock_number", "84901234000", "84901234099", "DEMO001");
state.EnqueueJob("lock_number", "84907654000", "84907654099", "DEMO002");
state.EnqueueJob("open_limit", "1000", "", "DEMO003");
state.EnqueueJob("open_limit", "5000", "", "DEMO004");
state.EnqueueJob("lock_limit", "0", "", "DEMO004");
// Job lỗi cố ý: type không hỗ trợ -> phải trả "failed"
state.EnqueueJob("unknown_type", "", "", "DEMO999");

var jsonOpts = new JsonSerializerOptions
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

static IResult Json(object obj) => Results.Json(obj);

// Healthcheck
app.MapGet("/health", (HttpContext ctx) =>
{
    state.HealthPings++;
    Console.WriteLine($"[health] ping #{state.HealthPings}");
    return Json(new
    {
        status = "ok",
        time = DateTime.Now.ToString("o"),
        server = "vos3000-mock",
        uptime_seconds = (long)(DateTime.Now - MockState.StartedAt).TotalSeconds
    });
});

// Lấy job kế tiếp
app.MapGet("/api/v1/vos-agent/jobs/next", (HttpContext ctx) =>
{
    var agentId = ctx.Request.Query["agent_id"].ToString();
    var job = state.TakeNextJob();
    Console.WriteLine($"[next] agent={agentId} -> {(job == null ? "no job" : job.job_id)}");
    return Json(new { data = new { job } });
});

// Claim
app.MapPost("/api/v1/vos-agent/jobs/{id}/claim", (string id) =>
{
    var ok = state.Claim(id);
    Console.WriteLine($"[claim] {id} -> {ok}");
    return Json(new { data = new { claimed = ok, job_id = id } });
});

// Báo kết quả
app.MapPost("/api/v1/vos-agent/jobs/{id}/result", async (string id, HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    state.RecordResult(id, body);
    Console.WriteLine($"[result] {id} -> {body}");
    return Json(new { data = new { accepted = true } });
});

// Heartbeat
app.MapPost("/api/v1/vos-agent/heartbeat", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    Console.WriteLine($"[heartbeat] {body}");
    return Json(new { data = new { ok = true } });
});

// Xem kết quả đã nhận (tiện kiểm tra)
app.MapGet("/debug/results", () => Json(new { data = state.Results }));
app.MapGet("/debug/stats", () => Json(new { data = state.Stats() }));

// Nạp job mới lúc đang chạy (để thấy app xử lý realtime)
// POST /debug/enqueue?type=add_number_section&start=84911111000&end=84911111099&account=DEMO007
app.MapPost("/debug/enqueue", (HttpContext ctx) =>
{
    var q = ctx.Request.Query;
    string type = q["type"].ToString();
    if (string.IsNullOrWhiteSpace(type)) return Results.BadRequest(new { error = "thieu type" });
    state.EnqueueJob(type, q["start"].ToString(), q["end"].ToString(), q["account"].ToString());
    Console.WriteLine($"[enqueue] {type} {q["start"]}-{q["end"]} {q["account"]}");
    return Json(new { data = state.Stats() });
});

Console.WriteLine("=== MOCK SERVER: http://127.0.0.1:5099 ===");
Console.WriteLine("  GET  /health                             (healthcheck)");
Console.WriteLine("  GET  /api/v1/vos-agent/jobs/next         (lấy job)");
Console.WriteLine("  POST /api/v1/vos-agent/jobs/{id}/claim   (claim)");
Console.WriteLine("  POST /api/v1/vos-agent/jobs/{id}/result  (báo kết quả)");
Console.WriteLine("  GET  /debug/results                      (xem kết quả)");
Console.WriteLine("  GET  /debug/stats                        (thống kê)");
Console.WriteLine("  POST /debug/enqueue?type=..&start=..&end=..&account=..  (nạp job)");
app.Run();

// ---------------------------------------------------------------------------

public class Job
{
    public string job_id { get; set; }
    public string type { get; set; }
    public string account_id { get; set; }
    public string number_start { get; set; }
    public string number_end { get; set; }
}

public class MockState
{
    public static readonly DateTime StartedAt = DateTime.Now;

    public int HealthPings;

    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly HashSet<string> _claimed = new();
    private readonly HashSet<string> _delivered = new();

    public List<object> Results { get; } = new();

    public void EnqueueJob(string type, string start, string end, string account)
    {
        lock (_lock)
        {
            _queue.Enqueue(new Job
            {
                job_id = "job-" + Guid.NewGuid().ToString("N")[..8],
                type = type,
                account_id = account,
                number_start = start,
                number_end = end
            });
        }
    }

    public Job TakeNextJob()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return null;
            var job = _queue.Dequeue();
            _delivered.Add(job.job_id);
            return job;
        }
    }

    public bool Claim(string id)
    {
        lock (_lock)
        {
            if (_claimed.Contains(id)) return false;
            _claimed.Add(id);
            return true;
        }
    }

    public void RecordResult(string id, string body)
    {
        lock (_lock) Results.Add(new { job_id = id, at = DateTime.Now.ToString("o"), body });
    }

    public object Stats()
    {
        lock (_lock)
        {
            return new
            {
                remaining = _queue.Count,
                claimed = _claimed.Count,
                delivered = _delivered.Count,
                results = Results.Count,
                health_pings = HealthPings
            };
        }
    }
}
