using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Vos3000Agent
{
    public class GuiResult
    {
        public string Status { get; set; }      // simulated | success | failed | unknown | blocked
        public string Message { get; set; }
        public Dictionary<string, object> Details { get; set; }

        public GuiResult(string status, string message, Dictionary<string, object> details = null)
        {
            Status = status;
            Message = message;
            Details = details;
        }
    }

    /// <summary>
    /// Driver điều khiển client VOS3000 bằng Windows UI Automation.
    /// Tương đương gui.py (pywinauto) trong app gốc.
    /// </summary>
    public class GuiDriver
    {
        private readonly Settings _settings;
        private readonly Dictionary<string, GuiProfile> _profiles;
        private readonly Action<string> _log;

        private static readonly Dictionary<string, string> Aliases = new()
        {
            [JobType.AddNumber] = JobType.AddNumberSection,
            [JobType.AddNumberSection] = JobType.AddNumberSection,
            [JobType.LockNumber] = JobType.LockNumber,
            [JobType.OpenLimit] = JobType.OpenLimit,
            [JobType.UnlockLimit] = JobType.OpenLimit,
            [JobType.LockLimit] = JobType.LockLimit
        };

        public GuiDriver(Settings settings, Dictionary<string, GuiProfile> profiles, Action<string> log)
        {
            _settings = settings;
            _profiles = profiles;
            _log = log ?? (_ => { });
        }

        public GuiResult Execute(Dictionary<string, object> job, bool simulate = true)
        {
            string kind = job != null && job.TryGetValue(JobField.Type, out var t) ? Convert.ToString(t) : "";
            string profileName = Aliases.TryGetValue(kind, out var alias) ? alias : null;

            if (profileName == null)
                return new GuiResult(Status.Failed, string.Format(Msg.Gui.UnsupportedJob, kind));

            _profiles.TryGetValue(profileName, out var profile);
            profile ??= new GuiProfile();

            var steps = profile.steps ?? new List<GuiStep>();

            var plan = new List<string>
            {
                $"Job type: {kind}",
                $"Profile: {profileName}",
                $"Account: {GetJob(job, JobField.AccountId)}",
                $"Payload: {JsonSerializer.Serialize(job ?? new Dictionary<string, object>())}"
            };

            if (simulate)
            {
                for (int i = 0; i < steps.Count; i++)
                    plan.Add(string.Format(Msg.Gui.SimulateStep, i + 1, steps[i]));
                if (steps.Count == 0)
                    plan.Add(Msg.Gui.NoControlSteps);
                foreach (var x in plan) _log(x);
                return new GuiResult(Status.Simulated, Msg.Gui.SimulatedDone,
                    new Dictionary<string, object> { ["plan"] = plan });
            }

            if (!_settings.live_gui_enabled)
                return new GuiResult(Status.Blocked, Msg.Gui.LiveLocked);
            if (steps.Count == 0)
                return new GuiResult(Status.Blocked, string.Format(Msg.Gui.NoSteps, profileName));
            if (profile.verify_steps == null || profile.verify_steps.Count == 0)
                return new GuiResult(Status.Blocked, Msg.Gui.NoVerifySteps);

            try
            {
                string regex = string.IsNullOrEmpty(profile.window_title_regex) ? "VOS3000" : profile.window_title_regex;
                var root = AutomationElement.RootElement;
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
                var wins = root.FindAll(TreeScope.Children, cond);
                var matched = new List<AutomationElement>();
                foreach (AutomationElement w in wins)
                {
                    try
                    {
                        string title = w.Current.Name;
                        if (!string.IsNullOrEmpty(title) && Regex.IsMatch(title, regex, RegexOptions.IgnoreCase))
                            matched.Add(w);
                    }
                    catch { }
                }

                if (matched.Count != 1)
                    return new GuiResult(Status.Unknown,
                        string.Format(Msg.Gui.WindowCountMismatch, regex, matched.Count));

                var win = matched[0];
                try { win.SetFocus(); } catch { }

                foreach (var step in steps)
                    RunStep(win, step, job);

                bool verified = false;
                foreach (var step in profile.verify_steps)
                    verified = RunVerify(win, step, job) || verified;

                if (!verified)
                    return new GuiResult(Status.Unknown,
                        Msg.Gui.VerifyFailed,
                        new Dictionary<string, object> { ["profile"] = profileName });

                return new GuiResult(Status.Success, Msg.Gui.Completed,
                    new Dictionary<string, object> { ["profile"] = profileName });
            }
            catch (Exception e)
            {
                return new GuiResult(Status.Unknown, string.Format(Msg.Gui.GuiError, e.Message));
            }
        }

        // ---------- Helpers ----------

        private static string GetJob(Dictionary<string, object> job, string key)
            => job != null && job.TryGetValue(key, out var v) ? Convert.ToString(v) ?? "" : "";

        private string ResolveValue(object v, Dictionary<string, object> job)
        {
            var s = Convert.ToString(v) ?? "";
            if (s.StartsWith("$job."))
                return GetJob(job, s.Substring("$job.".Length));
            return s;
        }

        private AutomationElement FindControl(AutomationElement win, GuiSelector sel)
        {
            if (sel == null) return null;
            var conds = new List<Condition>();
            if (sel.title != null)
                conds.Add(new PropertyCondition(AutomationElement.NameProperty, sel.title));
            if (sel.auto_id != null)
                conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, sel.auto_id));
            if (sel.control_type != null && int.TryParse(sel.control_type, out var ctId))
                conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.LookupById(ctId)));

            if (conds.Count == 0) return null;
            Condition cond = conds.Count == 1 ? conds[0] : new AndCondition(conds.ToArray());

            if (sel.found_index.HasValue)
            {
                var all = win.FindAll(TreeScope.Descendants, cond);
                int idx = sel.found_index.Value;
                if (idx >= 0 && idx < all.Count) return all[idx];
                return null;
            }
            return win.FindFirst(TreeScope.Descendants, cond);
        }

        private void RunStep(AutomationElement win, GuiStep s, Dictionary<string, object> job)
        {
            string action = s.action ?? "";
            _log($"LIVE {action}: {s}");

            switch (action)
            {
                case "wait":
                    Thread.Sleep(TimeSpan.FromSeconds(s.seconds > 0 ? s.seconds : 1));
                    return;

                case "send_keys":
                    SendKeys.SendWait(ResolveValue(s.keys, job));
                    return;
            }

            var ctl = FindControl(win, s.selector);
            if (ctl == null)
                throw new InvalidOperationException(string.Format(Msg.Gui.ControlNotFound, s.selector));

            switch (action)
            {
                case "click":
                    ClickControl(ctl);
                    break;
                case "set_text":
                    SetText(ctl, ResolveValue(s.value, job));
                    break;
                case "select":
                    SelectControl(ctl, ResolveValue(s.value, job));
                    break;
                case "check":
                    ctl.SetFocus();
                    ClickControl(ctl);
                    break;
                default:
                    throw new InvalidOperationException(string.Format(Msg.Gui.UnsupportedAction, action));
            }
        }

        private bool RunVerify(AutomationElement win, GuiStep s, Dictionary<string, object> job)
        {
            try
            {
                switch (s.assert)
                {
                    case "exists":
                        return FindControl(win, s.selector) != null;
                    case "text_contains":
                        var c1 = FindControl(win, s.selector);
                        return c1 != null && (c1.Current.Name ?? "").Contains(s.value ?? "");
                    case "text_equals":
                        var c2 = FindControl(win, s.selector);
                        return c2 != null && (c2.Current.Name ?? "") == (s.value ?? "");
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        // ---------- UIA patterns ----------

        private static void ClickControl(AutomationElement el)
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object p))
            {
                ((InvokePattern)p).Invoke();
                return;
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object tp))
            {
                ((TogglePattern)tp).Toggle();
                return;
            }
            el.SetFocus();
        }

        private static void SetText(AutomationElement el, string text)
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object p))
            {
                ((ValuePattern)p).SetValue(text ?? "");
                return;
            }
            el.SetFocus();
            SendKeys.SendWait(text ?? "");
        }

        private static void SelectControl(AutomationElement el, string value)
        {
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object p))
            {
                ((SelectionItemPattern)p).Select();
                return;
            }
            el.SetFocus();
            SendKeys.SendWait(value ?? "");
        }

        // ---------- Khảo sát client (tương đương inspect_client) ----------

        public static string InspectClient()
        {
            var sb = new StringBuilder();
            try
            {
                var root = AutomationElement.RootElement;
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
                var wins = root.FindAll(TreeScope.Children, cond);

                foreach (AutomationElement w in wins)
                {
                    string title;
                    try { title = w.Current.Name ?? ""; }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string lower = title.ToLower();
                    if (!lower.Contains("vos") && !lower.Contains("number")
                        && !lower.Contains("section") && !lower.Contains("account"))
                        continue;

                    sb.AppendLine($"Window: \"{title}\"  (handle={w.Current.NativeWindowHandle})");
                    DumpControls(w, sb, 1);
                    sb.AppendLine();
                }

                if (sb.Length == 0)
                    sb.AppendLine(Msg.Gui.NoWindow);
            }
            catch (Exception e)
            {
                sb.AppendLine(string.Format(Msg.Gui.InspectError, e.Message));
            }
            return sb.ToString();
        }

        private static void DumpControls(AutomationElement parent, StringBuilder sb, int depth)
        {
            if (depth > 5) return;
            try
            {
                var all = parent.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                int count = 0;
                foreach (AutomationElement el in all)
                {
                    if (count++ >= 200) { sb.AppendLine(string.Format(Msg.Gui.InspectTruncated, 200)); break; }
                    string pad = new string(' ', depth * 2);
                    sb.AppendLine($"{pad}[{el.Current.ControlType.ProgrammaticName}] " +
                        $"name=\"{el.Current.Name}\" autoId=\"{el.Current.AutomationId}\"");
                }
            }
            catch { }
        }
    }
}
