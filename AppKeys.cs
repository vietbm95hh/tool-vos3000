
namespace Vos3000Agent
{
    public static class FileKeys
    {
        public const string Settings = "settings.json";
        public const string Profiles = "gui_profiles.json";
        public const string AgentLog = "agent.log";
        public const string History = "history.jsonl";
        public const string BootstrapLog = "bootstrap.log";
        public const string LogoFile = "logo.png";
        public const string IconFile = "app.ico";
        public const string SessionFile = "vos_session_capture.bin";
        // Ban session da doi user/pass login (dat o %TEMP%\\Vos3000AgentCSharp)
        public const string LoginSessionFile = "vos_session_login.bin";
        public const string InstalledStamp = ".installed";
        public const string WriteProbe = ".write_test";
        // Mốc last_created_at của API nguồn (gọi tuần tự) — dùng khi DB chưa cấu hình/lỗi
        public const string SyncWatermark = "sync_watermark.json";

        // Thư mục con
        public const string AssetsDir = "assets";
        public const string DataDir = "Data";
    }

    
    public static class Resources
    {
        public const string Logo = "Vos3000Agent.Assets.logo.png";
        public const string Icon = "Vos3000Agent.Assets.app.ico";
        public const string SessionCapture = "Vos3000Agent.Data.vos_session_capture.bin";
        public const string RoutingCapture = "Vos3000Agent.Data.vos_routing_c2s.bin";
    }

    
    public static class Tables
    {
        public const string Log = "t_log";
        public const string Operation = "t_operation";
        public const string NumberAction = "t_number_action";
        public const string SyncRun = "t_sync_run";
        public const string Config = "t_config";
        public const string MappedNumber = "t_mapped_number";
        public const string MappedHistory = "t_mapped_history";
        public const string MappedActive = "t_mapped_active";
        public const string CustomerLog = "t_customer_log";
        public const string AccountNumberSet = "t_account_number_set";
        public const string MissingNumber = "t_number_missing";

        
        public static readonly string[] All =
        {
            Log, Operation, NumberAction, SyncRun, Config, MappedNumber, MappedHistory, MappedActive, CustomerLog,
            AccountNumberSet, MissingNumber
        };
    }

    
    public static class ConfigKeys
    {
       
        public const string AppSettings = "app_settings";
    }

    
    public static class LogModule
    {
        public const string Mapping = "mapping";
        public const string NumberLock = "khoaso";
        public const string Job = "job";
    }

    public static class LogLevel
    {
        public const string Info = "INFO";
        public const string Warn = "WARN";
        public const string Error = "ERROR";
    }

    
    public static class Status
    {
        public const string Success = "success";
        public const string Failed = "failed";
        public const string Unknown = "unknown";
        public const string Blocked = "blocked";
        public const string Simulated = "simulated";
        public const string NeedsReconciliation = "needs_reconciliation";

       
        public const string Online = "online";
        public const string Polling = "Polling";
    }

    
    public static class OperationType
    {
        public const string MappingLoad = "mapping_load";
        public const string LockLoad = "lock_load";
        public const string Lock = "lock";
        public const string Unlock = "unlock";
        public const string SyncPrefix = "sync_prefix";
    }

    
    public static class MappedSource
    {
        public const string Read = "read";
        public const string Api = "api";
    }

    
    public static class JobType
    {
        public const string AddNumber = "add_number";
        public const string AddNumberSection = "add_number_section";
        public const string LockNumber = "lock_number";
        public const string OpenLimit = "open_limit";
        public const string UnlockLimit = "unlock_limit";
        public const string LockLimit = "lock_limit";
    }

    
    public static class JobField
    {
        public const string Type = "type";
        public const string JobId = "job_id";
        public const string AccountId = "account_id";
        public const string NumberStart = "number_start";
        public const string NumberEnd = "number_end";
        public const string LimitValue = "limit_value";
        public const string LockMode = "lock_mode";
        public const string LimitType = "limit_type";
    }

    
    public static class RowField
    {
        public const string Time = "time";
        public const string Type = "type";
        public const string Job = "job";
        public const string Account = "account";
        public const string Status = "status";
        public const string Message = "message";
    }

    
    public static class HttpKey
    {
        public const string Authorization = "Authorization";
        public const string AgentId = "X-Agent-ID";
        public const string ApiKey = "api-key";
        public const string BearerPrefix = "Bearer ";
        public const string ContentTypeJson = "application/json";

        
        public const string JobIdPlaceholder = "{job_id}";
        public const string AgentIdQuery = "agent_id";
    }

    
    public static class ApiField
    {
        public const string Data = "data";
        public const string Job = "job";
        public const string Claimed = "claimed";
        public const string Phone = "data.phone";
        public const string LastCreatedAt = "last_created_at";
    }

    
    public static class GridColumn
    {
        public const string Stt = "stt";
        public const string Pick = "pick";     // checkbox chọn nhiều dòng (tab Khóa số)
        public const string Orig = "orig";
        public const string Num = "num";
        public const string Status = "status";
        public const string OpenForm = "openform";

        // Tab Customer (VOS)
        public const string CustId = "cid";
        public const string CustAccount = "cacc";
        public const string CustName = "cname";
        public const string CustDirection = "cdir";
        public const string CustType = "ctype";
        public const string CustStatus = "cstatus";
        public const string CustLock = "clock";
        public const string CustMoney = "cmoney";
        public const string CustLimit = "climit";
        public const string CustValid = "cvalid";
        public const string CustGateway = "cgw";
        public const string CustMemo = "cmemo";

        // Tab "Số chưa có (VOS)"
        public const string MissStt = "mstt";
        public const string MissNum = "mnum";
        public const string MissStatus = "mstatus";
        public const string MissFirst = "mfirst";
        public const string MissLast = "mlast";
        public const string MissHits = "mhits";

        // Tab "Số theo account (VOS)"
        public const string AcctStt = "astt";
        public const string AcctAccount = "aacc";
        public const string AcctDirection = "adir";
        public const string AcctNum = "anum";
        public const string AcctStatus = "astatus";
        public const string AcctUpdated = "aupd";
    }
}
