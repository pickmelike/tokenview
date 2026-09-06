using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace CodexUsageViewer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--report")
            {
                string outPath = (args.Length > 1) ? args[1] : Path.Combine(Path.GetTempPath(), "codex-usage-report.txt");
                UsageData data = UsageLoader.LoadAll(UsageLoader.DefaultSessionsPath());
                File.WriteAllText(outPath, UsageLoader.BuildReport(data), Encoding.UTF8);
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--shot")
            {
                MainForm.AutoShotPath = (args.Length > 1) ? args[1] : null;
                int sv;
                MainForm.AutoShotView = (args.Length > 2 && Int32.TryParse(args[2], out sv)) ? sv : -1;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------- 数据模型 ----------
    class UsageRecord
    {
        public DateTime Time;          // UTC
        public string Agent;           // "Codex" / "Claude"
        public string ThreadId;
        public string TurnId;
        public string ResponseId;
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public long Reasoning;
        public long Total;
        public string Model;      // 调用的模型，如 deepseek-v4-flash / claude-sonnet-4
    }

    class AgentTask
    {
        public DateTime Time;
        public string Agent;
        public string ThreadId;
        public string TaskId;
    }

    class UsageData
    {
        public List<UsageRecord> Records = new List<UsageRecord>();
        public List<AgentTask> Tasks = new List<AgentTask>();
        public Dictionary<string, string> ThreadNames = new Dictionary<string, string>();
        public List<string> Errors = new List<string>();
        public int FileCount;
        public DateTime MinTime = DateTime.MaxValue;
        public DateTime MaxTime = DateTime.MinValue;
        public int AgentCount;          // 有数据的 agent 种类数
    }

    class DayAgg
    {
        public DateTime Day;
        public long Total, Input, Output, Cached, Reasoning;
        public int Turns;
    }

    class ThreadAgg
    {
        public string Agent;
        public string ThreadId;
        public string Name;
        public long Total, Input, Output, Cached, Reasoning;
        public int Turns;
    }

    class ModelAgg
    {
        public string Model;
        public long Total, Input, Output, Cached, Reasoning;
        public int Calls;
    }

    class TrendPoint
    {
        public DateTime T;
        public long Total, Input, Output, Cached, Reasoning;
        public int Calls;
    }

    // ---------- 数据加载 v1.3：多 AI Agent ----------
    static class UsageLoader
    {
        public static string DefaultSessionsPath()
        {
            return Path.Combine(UserRoot(), ".codex", "sessions");
        }

        static string UserRoot()
        {
            string root = Environment.GetEnvironmentVariable("USERPROFILE");
            if (String.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrEmpty(root)) root = @"C:\Users\" + Environment.UserName;
            return root;
        }

        static string ClaudeProjectsPath()
        {
            return Path.Combine(UserRoot(), ".claude", "projects");
        }

        public static UsageData LoadAll(string codexSessionsDir)
        {
            UsageData data = new UsageData();
            HashSet<string> agentSet = new HashSet<string>();

            // ---- Codex ----
            LoadCodex(codexSessionsDir, data, agentSet);
            // ---- Claude Code ----
            // ---- Claude (Code / Desktop / 3p local-agent) ----
            LoadClaudeAll(data, agentSet);

            data.AgentCount = agentSet.Count;
            if (data.Records.Count > 0)
            {
                data.MinTime = data.Records.Min(r => r.Time);
                data.MaxTime = data.Records.Max(r => r.Time);
            }
            return data;
        }

        // ================= Codex =================
        static void LoadCodex(string sessionsDir, UsageData data, HashSet<string> agentSet)
        {
            if (sessionsDir == null || !Directory.Exists(sessionsDir))
            {
                data.Errors.Add("Codex 目录不存在: " + sessionsDir);
                return;
            }

            // 线程名映射 (session_index.jsonl)
            try
            {
                string idx = Path.Combine(Path.GetDirectoryName(sessionsDir), "session_index.jsonl");
                if (File.Exists(idx))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    foreach (string line in File.ReadLines(idx))
                    {
                        try
                        {
                            Dictionary<string, object> o = ser.DeserializeObject(line) as Dictionary<string, object>;
                            if (o == null) continue;
                            object id; object nm;
                            if (o.TryGetValue("id", out id) && id != null)
                            {
                                string name = (o.TryGetValue("thread_name", out nm) && nm != null) ? nm.ToString() : "";
                                if (!String.IsNullOrEmpty(name)) data.ThreadNames["Codex|" + id.ToString()] = name;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { data.Errors.Add("读取 session_index 失败: " + ex.Message); }

            List<string> files = new List<string>();
            try { files.AddRange(Directory.GetFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception ex) { data.Errors.Add("Codex 扫描失败: " + ex.Message); }
            data.FileCount += files.Count;

            HashSet<string> seen = new HashSet<string>();
            foreach (string file in files)
            {
                HashSet<string> doneTurns = new HashSet<string>();
                string curModel = null;
                try
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            js.MaxJsonLength = int.MaxValue;
                            Dictionary<string, object> root;
                            try { root = js.DeserializeObject(line) as Dictionary<string, object>; }
                            catch { continue; }
                            if (root == null) continue;
                            string type = GetStr(root, "type");
                            string md = null;
                            if (type == "world_state" || type == "session_meta")
                                md = TryExtractModel(root, type);
                            if (!String.IsNullOrEmpty(md)) curModel = md;

                            if (type == "token_usage_record")
                            {
                                UsageRecord rec = ParseCodexRec(root);
                                if (rec == null) continue;
                                rec.Model = curModel ?? "unknown";
                                string key = String.IsNullOrEmpty(rec.ResponseId) ? (rec.TurnId + "|" + rec.Time.Ticks.ToString()) : rec.ResponseId;
                                if (!seen.Add(key)) continue;
                                data.Records.Add(rec);
                                agentSet.Add("Codex");
                            }
                            else if (type == "event_msg")
                            {
                                Dictionary<string, object> payload = GetDict(root, "payload");
                                if (payload == null) continue;
                                if (GetStr(payload, "type") == "task_complete")
                                {
                                    string turnId = GetStr(payload, "turn_id") ?? "";
                                    if (turnId.Length > 0 && doneTurns.Add(turnId))
                                    {
                                        AgentTask t = new AgentTask();
                                        t.Agent = "Codex";
                                        t.ThreadId = GetStr(payload, "thread_id") ?? "";
                                        t.TaskId = turnId;
                                        t.Time = ParseTime(GetStr(root, "timestamp"));
                                        data.Tasks.Add(t);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { data.Errors.Add("读取文件失败: " + Path.GetFileName(file) + " -> " + ex.Message); }
            }
        }

        static UsageRecord ParseCodexRec(Dictionary<string, object> root)
        {
            try
            {
                Dictionary<string, object> payload = GetDict(root, "payload");
                if (payload == null) return null;
                Dictionary<string, object> usage = GetDict(payload, "usage");
                if (usage == null) return null;
                UsageRecord rec = new UsageRecord();
                rec.Time = ParseTime(GetStr(root, "timestamp"));
                rec.Agent = "Codex";
                rec.ThreadId = GetStr(payload, "thread_id") ?? "";
                rec.TurnId = GetStr(payload, "turn_id") ?? "";
                rec.ResponseId = GetStr(payload, "response_id") ?? "";
                rec.Input = GetLong(usage, "input_tokens");
                rec.Cached = GetLong(usage, "cached_input_tokens");
                rec.CacheWrite = GetLong(usage, "cache_write_input_tokens");
                rec.Output = GetLong(usage, "output_tokens");
                rec.Reasoning = GetLong(usage, "reasoning_output_tokens");
                rec.Total = GetLong(usage, "total_tokens");
                return rec;
            }
            catch { return null; }
        }

        // ================= Claude Code =================
        static void LoadClaudeAll(UsageData data, HashSet<string> agentSet)
        {
            List<string> roots = new List<string>();
            roots.Add(Path.Combine(UserRoot(), ".claude", "projects"));
            roots.Add(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? @"C:\Users\" + Environment.UserName + @"\AppData\Local", "Claude-3p", "local-agent-mode-sessions"));
            roots.Add(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? @"C:\Users\" + Environment.UserName + @"\AppData\Local", "Claude"));

            foreach (string root in roots)
            {
                // 目录不可达（无权限/不存在）时静默跳过
                bool ok;
                try { ok = Directory.Exists(root); }
                catch { ok = false; }
                if (!ok) continue;
                ScanClaudeRoot(root, data, agentSet);
            }
        }

        static void ScanClaudeRoot(string scanRoot, UsageData data, HashSet<string> agentSet)
        {
            List<string> files = new List<string>();
            try { files.AddRange(Directory.GetFiles(scanRoot, "*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception ex) { data.Errors.Add("Claude 扫描失败: " + ex.Message); return; }
            // audit.jsonl 是审计日志，会重复记录所有会话，跳过以免重复计数
            files.RemoveAll(f => String.Equals(Path.GetFileName(f), "audit.jsonl", StringComparison.OrdinalIgnoreCase));
            data.FileCount += files.Count;

            foreach (string file in files)
            {
                HashSet<string> seenResp = new HashSet<string>();
                string firstPrompt = null, customTitle = null, lastPrompt = null;
                try
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            JavaScriptSerializer js = new JavaScriptSerializer();
                            js.MaxJsonLength = int.MaxValue;
                            Dictionary<string, object> root;
                            try { root = js.DeserializeObject(line) as Dictionary<string, object>; }
                            catch { continue; }
                            if (root == null) continue;
                            string type = GetStr(root, "type");

                            // ---- 会话名（对话名）提取 ----
                            if (type == "custom-title")
                            {
                                string ct = GetStr(root, "customTitle");
                                if (!String.IsNullOrEmpty(ct)) customTitle = ct;
                            }
                            else if (type == "last-prompt")
                            {
                                string lp = GetStr(root, "lastPrompt");
                                if (!String.IsNullOrEmpty(lp)) lastPrompt = lp;
                            }
                            else if (type == "user" && firstPrompt == null)
                            {
                                firstPrompt = FirstUserText(root);
                            }

                            if (type == "assistant")
                            {
                                UsageRecord rec = ParseClaudeRec(root, file);
                                if (rec == null) continue;
                                string rk = rec.ResponseId.Length > 0 ? rec.ResponseId : (rec.TurnId + "|" + rec.Time.Ticks.ToString());
                                if (!seenResp.Add(rk)) continue;
                                data.Records.Add(rec);
                                agentSet.Add("Claude");
                            }
                            else if (type == "token_usage_record")
                            {
                                // 兼容 Codex 风格 rollout（若 Claude-3p 使用类似结构）
                                UsageRecord rec = ParseCodexRec(root);
                                if (rec == null) continue;
                                rec.Agent = "Claude";
                                rec.ThreadId = file;
                                rec.Model = GetStr(root, "model") ?? "claude";
                                data.Records.Add(rec);
                                agentSet.Add("Claude");
                            }
                            else if (type == "user" && IsUserPrompt(root))
                            {
                                AgentTask t = new AgentTask();
                                t.Agent = "Claude";
                                t.ThreadId = file;
                                t.Time = ParseTime(GetStr(root, "timestamp"));
                                t.TaskId = GetStr(root, "uuid") ?? GetStr(root, "parentUuid") ?? (Path.GetFileName(file) + "|" + t.Time.Ticks.ToString());
                                data.Tasks.Add(t);
                            }
                            else if (type == "event_msg")
                            {
                                Dictionary<string, object> payload = GetDict(root, "payload");
                                if (payload != null && GetStr(payload, "type") == "task_complete")
                                {
                                    string turnId = GetStr(payload, "turn_id") ?? "";
                                    if (turnId.Length > 0)
                                    {
                                        AgentTask t = new AgentTask();
                                        t.Agent = "Claude";
                                        t.ThreadId = file;
                                        t.TaskId = turnId;
                                        t.Time = ParseTime(GetStr(root, "timestamp"));
                                        data.Tasks.Add(t);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { data.Errors.Add("Claude 读取失败: " + Path.GetFileName(file) + " -> " + ex.Message); }

                // 会话名：自定义标题 > 首次用户输入 > 最近提示 > 文件名
                string sname = customTitle ?? firstPrompt ?? lastPrompt;
                if (!String.IsNullOrEmpty(sname))
                {
                    sname = sname.Trim();
                    if (sname.Length > 40) sname = sname.Substring(0, 40) + "…";
                }
                else sname = Path.GetFileNameWithoutExtension(file);
                data.ThreadNames["Claude|" + file] = sname;
            }
        }

        static UsageRecord ParseClaudeRec(Dictionary<string, object> root, string file)
        {
            try
            {
                Dictionary<string, object> msg = GetDict(root, "message");
                Dictionary<string, object> usage = (msg != null) ? GetDict(msg, "usage") : GetDict(root, "usage");
                if (usage == null) return null;
                if (GetLong(usage, "input_tokens") == 0 && GetLong(usage, "output_tokens") == 0) return null;

                UsageRecord rec = new UsageRecord();
                rec.Time = ParseTime(GetStr(root, "timestamp"));
                rec.Agent = "Claude";
                rec.ThreadId = file;
                object mobj;
                string mm = (msg != null && msg.TryGetValue("model", out mobj) && mobj != null) ? mobj.ToString() : GetStr(root, "model");
                rec.Model = String.IsNullOrEmpty(mm) ? "claude" : mm;
                rec.TurnId = GetStr(root, "parentUuid") ?? "";
                object mid;
                rec.ResponseId = (msg != null && msg.TryGetValue("id", out mid) && mid != null) ? mid.ToString() : "";
                rec.Input = GetLong(usage, "input_tokens");
                rec.Cached = GetLong(usage, "cache_read_input_tokens");
                rec.CacheWrite = GetLong(usage, "cache_creation_input_tokens");
                rec.Output = GetLong(usage, "output_tokens");
                rec.Reasoning = 0;
                long total = GetLong(usage, "total_tokens");
                rec.Total = total > 0 ? total : (rec.Input + rec.Output + rec.Cached + rec.CacheWrite);
                return rec;
            }
            catch { return null; }
        }

        static string FirstUserText(Dictionary<string, object> root)
        {
            try
            {
                Dictionary<string, object> msg = GetDict(root, "message");
                if (msg == null) return null;
                object content;
                if (!msg.TryGetValue("content", out content) || content == null) return null;
                if (content is string)
                {
                    string s = ((string)content).Trim();
                    return s.Length > 0 ? s : null;
                }
                return null;
            }
            catch { return null; }
        }

        static bool IsUserPrompt(Dictionary<string, object> root)
        {
            return FirstUserText(root) != null;
        }

        static string TryExtractModel(Dictionary<string, object> root, string type)
        {
            try
            {
                Dictionary<string, object> payload = GetDict(root, "payload");
                if (payload == null) return null;
                if (type == "world_state")
                {
                    Dictionary<string, object> state = GetDict(payload, "state");
                    if (state != null)
                    {
                        Dictionary<string, object> cm = GetDict(state, "collaboration_mode");
                        if (cm != null)
                        {
                            string m = GetStr(cm, "model");
                            if (!String.IsNullOrEmpty(m)) return m;
                        }
                    }
                }
                else if (type == "session_meta")
                {
                    Dictionary<string, object> bi = GetDict(payload, "base_instructions");
                    if (bi != null)
                    {
                        Dictionary<string, object> pv = GetDict(bi, "provenance");
                        if (pv != null)
                        {
                            string m = GetStr(pv, "model");
                            if (!String.IsNullOrEmpty(m)) return m;
                        }
                    }
                }
                return null;
            }
            catch { return null; }
        }

        static DateTime ParseTime(string ts)
        {
            DateTime t;
            if (!String.IsNullOrEmpty(ts) && DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return DateTime.UtcNow;
        }

        static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return v.ToString();
            return null;
        }

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v is Dictionary<string, object>) return (Dictionary<string, object>)v;
            return null;
        }

        static long GetLong(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
                catch { }
            }
            return 0;
        }

        // 按记录来源过滤
        public static UsageData FilterByAgent(UsageData src, string agent)
        {
            if (src == null) return null;
            if (String.IsNullOrEmpty(agent) || agent == "全部") return src;
            UsageData d = new UsageData();
            d.ThreadNames = src.ThreadNames;
            d.FileCount = src.FileCount;
            d.Errors = src.Errors;
            foreach (UsageRecord r in src.Records)
                if (r.Agent == agent) d.Records.Add(r);
            foreach (AgentTask t in src.Tasks)
                if (t.Agent == agent) d.Tasks.Add(t);
            if (d.Records.Count > 0)
            {
                d.MinTime = d.Records.Min(r => r.Time);
                d.MaxTime = d.Records.Max(r => r.Time);
            }
            return d;
        }

        // 聚合
        public static List<DayAgg> AggregateByDay(UsageData d)
        {
            Dictionary<DateTime, DayAgg> map = new Dictionary<DateTime, DayAgg>();
            foreach (UsageRecord r in d.Records)
            {
                DateTime day = r.Time.ToLocalTime().Date;
                DayAgg a;
                if (!map.TryGetValue(day, out a)) { a = new DayAgg(); a.Day = day; map[day] = a; }
                a.Input += r.Input; a.Output += r.Output; a.Cached += r.Cached; a.Reasoning += r.Reasoning;
                a.Total += r.Total; a.Turns++;
            }
            List<DayAgg> list = map.Values.ToList();
            list.Sort((x, y) => x.Day.CompareTo(y.Day));
            return list;
        }

        public static List<ThreadAgg> AggregateByThread(UsageData d)
        {
            Dictionary<string, ThreadAgg> map = new Dictionary<string, ThreadAgg>();
            foreach (UsageRecord r in d.Records)
            {
                string key = r.Agent + "|" + r.ThreadId;
                ThreadAgg a;
                if (!map.TryGetValue(key, out a))
                {
                    a = new ThreadAgg(); a.Agent = r.Agent; a.ThreadId = r.ThreadId;
                    string nm;
                    a.Name = (d.ThreadNames.TryGetValue(key, out nm) && !String.IsNullOrEmpty(nm)) ? nm : ShortId(TrimAgentPrefix(r.ThreadId, r.Agent));
                    map[key] = a;
                }
                a.Input += r.Input; a.Output += r.Output; a.Cached += r.Cached; a.Reasoning += r.Reasoning;
                a.Total += r.Total; a.Turns++;
            }
            List<ThreadAgg> list = map.Values.ToList();
            list.Sort((x, y) => y.Total.CompareTo(x.Total));
            return list;
        }

        public static List<ModelAgg> AggregateByModel(UsageData d)
        {
            Dictionary<string, ModelAgg> map = new Dictionary<string, ModelAgg>();
            foreach (UsageRecord r in d.Records)
            {
                string key = String.IsNullOrEmpty(r.Model) ? "(未知模型)" : r.Model;
                ModelAgg a;
                if (!map.TryGetValue(key, out a)) { a = new ModelAgg(); a.Model = key; map[key] = a; }
                a.Input += r.Input; a.Output += r.Output; a.Cached += r.Cached; a.Reasoning += r.Reasoning;
                a.Total += r.Total; a.Calls++;
            }
            List<ModelAgg> list = map.Values.ToList();
            list.Sort((x, y) => y.Total.CompareTo(x.Total));
            return list;
        }

        public static List<TrendPoint> AggregateTrend(UsageData d)
        {
            List<TrendPoint> list = new List<TrendPoint>();
            if (d == null || d.Records.Count == 0) return list;
            TimeSpan span = d.MaxTime - d.MinTime;
            bool hourly = span <= TimeSpan.FromDays(4);
            Dictionary<long, TrendPoint> map = new Dictionary<long, TrendPoint>();
            foreach (UsageRecord r in d.Records)
            {
                DateTime lt = r.Time.ToLocalTime();
                DateTime key = hourly ? new DateTime(lt.Year, lt.Month, lt.Day, lt.Hour, 0, 0) : lt.Date;
                TrendPoint p;
                if (!map.TryGetValue(key.Ticks, out p)) { p = new TrendPoint(); p.T = key; map[key.Ticks] = p; }
                p.Input += r.Input; p.Output += r.Output; p.Cached += r.Cached; p.Reasoning += r.Reasoning;
                p.Total += r.Total; p.Calls++;
            }
            list = map.Values.ToList();
            list.Sort((x, y) => x.T.CompareTo(y.T));
            return list;
        }

        public static string ShortId(string id)
        {
            if (String.IsNullOrEmpty(id)) return "(unknown)";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        public static string ThreadLabel(UsageData d, string agent, string threadId)
        {
            string key = agent + "|" + threadId;
            string nm;
            if (d.ThreadNames.TryGetValue(key, out nm) && !String.IsNullOrEmpty(nm)) return nm;
            return ShortId(TrimAgentPrefix(threadId, agent));
        }

        // 防御：老数据里 threadId 可能已带 "Agent|" 前缀，显示前剥掉，避免出现 "Claude|C..." 这类截断值
        static string TrimAgentPrefix(string id, string agent)
        {
            if (!String.IsNullOrEmpty(id) && !String.IsNullOrEmpty(agent) && id.StartsWith(agent + "|"))
                return id.Substring(agent.Length + 1);
            return id;
        }

        public static string BuildReport(UsageData d)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== AI Agent Token Usage Report ===");
            sb.AppendLine("扫描文件数: " + d.FileCount.ToString());
            sb.AppendLine("记录数(去重后): " + d.Records.Count.ToString());
            sb.AppendLine("完成任务数: " + d.Tasks.Count.ToString());
            if (d.Records.Count > 0)
            {
                sb.AppendLine("时间范围: " + d.MinTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " ~ " + d.MaxTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                long tIn = d.Records.Sum(r => r.Input), tOut = d.Records.Sum(r => r.Output), tCached = d.Records.Sum(r => r.Cached), tTotal = d.Records.Sum(r => r.Total);
                sb.AppendLine("总 Tokens: " + tTotal.ToString("N0"));
                if (d.Tasks.Count > 0) sb.AppendLine("每任务平均消耗: " + (tTotal / d.Tasks.Count).ToString("N0") + " tokens/任务");
                sb.AppendLine("输入: " + tIn.ToString("N0") + ", 缓存读取: " + tCached.ToString("N0") + ", 输出: " + tOut.ToString("N0"));
                sb.AppendLine();
                sb.AppendLine("--- 按 Agent ---");
                foreach (string agent in d.Records.Select(r => r.Agent).Distinct())
                {
                    long at = d.Records.Where(r => r.Agent == agent).Sum(r => r.Total);
                    int atk = d.Tasks.Count(t => t.Agent == agent);
                    sb.AppendLine(agent + " | 总 " + at.ToString("N0") + " | 任务 " + atk.ToString() + " | 每任务 " + (atk > 0 ? (at / atk).ToString("N0") : "-"));
                }
                sb.AppendLine();
                sb.AppendLine("--- 按天 ---");
                foreach (DayAgg a in AggregateByDay(d))
                    sb.AppendLine(a.Day.ToString("yyyy-MM-dd") + " | 输入 " + a.Input.ToString("N0") + " | 缓存 " + a.Cached.ToString("N0") + " | 输出 " + a.Output.ToString("N0") + " | 总计 " + a.Total.ToString("N0") + " | 轮次 " + a.Turns.ToString());
                sb.AppendLine();
                sb.AppendLine("--- 按会话 ---");
                foreach (ThreadAgg a in AggregateByThread(d))
                    sb.AppendLine((a.Agent ?? "") + " | " + (a.Name ?? "") + " | 输入 " + a.Input.ToString("N0") + " | 缓存 " + a.Cached.ToString("N0") + " | 输出 " + a.Output.ToString("N0") + " | 总计 " + a.Total.ToString("N0") + " | 轮次 " + a.Turns.ToString());
            }
            foreach (string e in d.Errors) sb.AppendLine("[warn] " + e);
            return sb.ToString();
        }
    }
    static class Ui
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void FillRoundGrad(Graphics g, RectangleF r, float rad, Color c1, Color c2, float angle)
        {
            using (GraphicsPath p = Round(r, rad))
            using (LinearGradientBrush b = new LinearGradientBrush(r, c1, c2, angle))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, RectangleF r, float rad, Color c, float width)
        {
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        // 数值缩写: 1234 -> 1.2K, 3.4M ...
        public static string ShortNum(long v)
        {
            if (v >= 1000000000L) return (v / 1000000000.0).ToString("0.##") + "B";
            if (v >= 1000000L) return (v / 1000000.0).ToString("0.##") + "M";
            if (v >= 1000L) return (v / 1000.0).ToString("0.##") + "K";
            return v.ToString();
        }

        public static Font F(float size, bool bold)
        {
            return new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    // ---------- 图表数据项 ----------
    class ChartItem
    {
        public string Label;
        public long Value;
        public ChartItem(string label, long value) { Label = label; Value = value; }
    }

    // ---------- 自绘柱状图 v1.2 ----------
    class ChartView : Control
    {
        List<ChartItem> _items = new List<ChartItem>();
        string _title = "";
        int _hover = -1;
        double _anim = 1.0;
        bool _lineMode = false;
        Timer _timer;
        static readonly Color C_TOP = Color.FromArgb(255, 110, 168, 255);
        static readonly Color C_BOT = Color.FromArgb(255, 79, 124, 255);
        static readonly Color C_HOV_TOP = Color.FromArgb(255, 255, 199, 110);
        static readonly Color C_HOV_BOT = Color.FromArgb(255, 255, 154, 44);

        public ChartView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
            _timer = new Timer();
            _timer.Interval = 16;
            _timer.Tick += delegate
            {
                _anim += 0.09;
                if (_anim >= 1.0) { _anim = 1.0; _timer.Stop(); }
                Invalidate();
            };
        }

        public void SetItems(IEnumerable<ChartItem> items, string title, bool line = false)
        {
            _items = (items == null) ? new List<ChartItem>() : new List<ChartItem>(items);
            _title = title ?? "";
            _hover = -1;
            _lineMode = line;
            _anim = 0.0;
            _timer.Start();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.X, e.Y);
            if (idx != _hover) { _hover = idx; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        int HitTest(int x, int y)
        {
            if (_items.Count == 0) return -1;
            RectangleF plot = PlotRect();
            if (x < plot.X || x > plot.Right || y < plot.Y || y > plot.Bottom) return -1;
            float slot = plot.Width / _items.Count;
            int i = (int)((x - plot.X) / slot);
            if (i < 0 || i >= _items.Count) return -1;
            return i;
        }

        RectangleF PlotRect()
        {
            float left = 84, top = 46, right = 16, bottom = 58;
            return new RectangleF(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            float W = Width, H = Height;

            // 标题
            using (Font tf = Ui.F(11.5f, true))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 31, 41, 55)))
                g.DrawString(_title, tf, tb, 20, 12);

            if (_items.Count == 0)
            {
                using (Font f = Ui.F(10f, false))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 156, 163, 175)))
                {
                    string msg = "暂无数据，点击右上角「刷新数据」";
                    SizeF sz = g.MeasureString(msg, f);
                    g.DrawString(msg, f, b, (W - sz.Width) / 2f, H / 2f - 8);
                }
                return;
            }

            RectangleF plot = PlotRect();
            long maxV = Math.Max(1, _items.Max(i => i.Value));

            // Y 轴刻度（nice numbers）
            double rawStep = maxV / 5.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double norm = rawStep / mag;
            double nice = norm < 1 ? 1 : norm < 2 ? 2 : norm < 5 ? 5 : 10;
            double step = nice * mag;
            long topV = (long)(Math.Ceiling(maxV / step) * step);
            if (topV <= 0) topV = 1;

            using (Font tickFont = Ui.F(8.5f, false))
            using (Pen gridPen = new Pen(Color.FromArgb(60, 226, 232, 240)))
            using (Pen axisPen = new Pen(Color.FromArgb(120, 203, 213, 225)))
            using (SolidBrush tickBrush = new SolidBrush(Color.FromArgb(255, 148, 163, 184)))
            {
                for (long v = 0; v <= topV; v += (long)step)
                {
                    float y = plot.Bottom - (float)((double)v / topV) * plot.Height;
                    if (v == 0)
                        g.DrawLine(axisPen, plot.X, y, plot.Right, y);
                    else
                    {
                        g.DrawLine(gridPen, plot.X, y, plot.Right, y);
                    }
                    string s = Ui.ShortNum(v);
                    SizeF sz = g.MeasureString(s, tickFont);
                    g.DrawString(s, tickFont, tickBrush, plot.X - sz.Width - 7, y - sz.Height / 2f);
                }
            }

            if (_lineMode)
            {
                DrawLineChart(g, plot, topV);
                return;
            }
            // 柱子（含生长动画）
            float slot = plot.Width / _items.Count;
            float barW = Math.Min(slot * 0.62f, 62f);
            int labelEvery = (int)Math.Ceiling(_items.Count / 14.0);
            if (labelEvery < 1) labelEvery = 1;

            using (Font labelFont = Ui.F(8.5f, false))
            using (Font tipFont = Ui.F(9f, false))
            using (SolidBrush labBrush = new SolidBrush(Color.FromArgb(255, 120, 134, 156)))
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    float cx = plot.X + slot * i + slot / 2f;
                    double rawH = ((double)_items[i].Value / topV) * plot.Height * _anim;
                    if (rawH < 1.0 && _items[i].Value > 0 && _anim > 0.05) rawH = 1.0;
                    float h = (float)rawH;
                    float yTop = plot.Bottom - h;
                    float r = Math.Min(6f, Math.Min(barW / 2f, h));
                    RectangleF bar = new RectangleF(cx - barW / 2f, yTop, barW, Math.Max(h, 0.5f));
                    bool hot = (i == _hover);

                    if (hot)
                        Ui.FillRoundGrad(g, bar, r, C_HOV_TOP, C_HOV_BOT, 90f);
                    else
                        Ui.FillRoundGrad(g, bar, r, C_TOP, C_BOT, 90f);
                    if (_anim >= 1.0)
                        Ui.DrawRound(g, bar, r, Color.FromArgb(hot ? 180 : 60, 255, 255, 255), 1f);

                    // X 轴标签
                    if (i % labelEvery == 0)
                    {
                        string lab = _items[i].Label;
                        if (lab.Length > 11) lab = lab.Substring(0, 11) + "…";
                        SizeF sz = g.MeasureString(lab, labelFont);
                        float lx = cx - sz.Width / 2f;
                        lx = Math.Max(plot.X, Math.Min(plot.Right - sz.Width, lx));
                        g.DrawString(lab, labelFont, labBrush, lx, plot.Bottom + 10);
                    }

                    // 悬停气泡
                    if (hot && _items[i].Value > 0)
                    {
                        string t1 = _items[i].Label;
                        string t2 = _items[i].Value.ToString("N0") + " tokens";
                        SizeF s1 = g.MeasureString(t1, tipFont);
                        SizeF s2 = g.MeasureString(t2, tipFont);
                        float tw = Math.Max(s1.Width, s2.Width) + 20;
                        float th = s1.Height + s2.Height + 14;
                        float tx = cx - tw / 2f;
                        tx = Math.Max(plot.X, Math.Min(W - tw - 8, tx));
                        float ty = yTop - th - 8;
                        if (ty < 34) ty = 34;
                        RectangleF tip = new RectangleF(tx, ty, tw, th);
                        using (GraphicsPath p = Ui.Round(tip, 8f))
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(242, 30, 41, 59)))
                        using (SolidBrush wb = new SolidBrush(Color.White))
                        {
                            g.FillPath(bg, p);
                            g.DrawString(t1, tipFont, wb, tx + 10, ty + 7);
                            using (SolidBrush sub = new SolidBrush(Color.FromArgb(255, 255, 214, 150)))
                                g.DrawString(t2, tipFont, sub, tx + 10, ty + 7 + s1.Height);
                        }
                    }
                }
            }
        }
        void DrawLineChart(Graphics g, RectangleF plot, long topV)
        {
            if (_items.Count == 0 || topV <= 0) return;
            float slot = plot.Width / _items.Count;
            PointF[] pts = new PointF[_items.Count];
            float baseY = plot.Bottom;
            float maxX = plot.X;
            for (int i = 0; i < _items.Count; i++)
            {
                float cx = plot.X + slot * i + slot / 2f;
                float h = (float)((double)_items[i].Value / topV) * plot.Height;
                float y = baseY - h;
                pts[i] = new PointF(cx, y);
                maxX = cx;
            }
            // 面积渐变
            using (GraphicsPath area = new GraphicsPath())
            {
                area.AddLine(pts[0].X, baseY, pts[0].X, pts[0].Y);
                for (int i = 1; i < pts.Length; i++) area.AddLine(pts[i - 1], pts[i]);
                area.AddLine(pts[pts.Length - 1], new PointF(maxX, baseY));
                area.CloseFigure();
                RectangleF rc = new RectangleF(plot.X, pts.Min(p => p.Y), plot.Width, baseY - pts.Min(p => p.Y));
                using (LinearGradientBrush br = new LinearGradientBrush(rc, Color.FromArgb(90, 79, 124, 255), Color.FromArgb(10, 79, 124, 255), 90f))
                    g.FillPath(br, area);
            }
            // 折线
            using (Pen linePen = new Pen(Color.FromArgb(255, 79, 124, 255), 2.4f))
                g.DrawLines(linePen, pts);
            // 点 + X 轴标签 + tooltip
            int labelEvery = (int)Math.Ceiling(_items.Count / 12.0);
            if (labelEvery < 1) labelEvery = 1;
            using (Font labelFont = Ui.F(8.5f, false))
            using (Font tipFont = Ui.F(9f, false))
            using (SolidBrush labBrush = new SolidBrush(Color.FromArgb(255, 120, 134, 156)))
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    bool hot = (i == _hover);
                    float r = hot ? 5.5f : 3.5f;
                    using (SolidBrush dot = new SolidBrush(hot ? Color.FromArgb(255, 255, 154, 44) : Color.White))
                    using (Pen dp = new Pen(Color.FromArgb(255, 79, 124, 255), 2f))
                    {
                        g.FillEllipse(dot, pts[i].X - r, pts[i].Y - r, r * 2, r * 2);
                        g.DrawEllipse(dp, pts[i].X - r, pts[i].Y - r, r * 2, r * 2);
                    }
                    if (i % labelEvery == 0)
                    {
                        string lab = _items[i].Label;
                        if (lab.Length > 12) lab = lab.Substring(0, 12) + "…";
                        SizeF sz = g.MeasureString(lab, labelFont);
                        float lx = pts[i].X - sz.Width / 2f;
                        lx = Math.Max(plot.X, Math.Min(plot.Right - sz.Width, lx));
                        g.DrawString(lab, labelFont, labBrush, lx, plot.Bottom + 10);
                    }
                    if (hot)
                    {
                        string t1 = _items[i].Label;
                        string t2 = _items[i].Value.ToString("N0") + " tokens";
                        SizeF s1 = g.MeasureString(t1, tipFont);
                        SizeF s2 = g.MeasureString(t2, tipFont);
                        float tw = Math.Max(s1.Width, s2.Width) + 20;
                        float th = s1.Height + s2.Height + 14;
                        float tx = pts[i].X - tw / 2f;
                        tx = Math.Max(plot.X, Math.Min(Width - tw - 8, tx));
                        float ty = pts[i].Y - th - 12;
                        if (ty < 34) ty = pts[i].Y + 12;
                        RectangleF tip = new RectangleF(tx, ty, tw, th);
                        using (GraphicsPath p = Ui.Round(tip, 8f))
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(242, 30, 41, 59)))
                        {
                            g.FillPath(bg, p);
                            g.DrawString(t1, tipFont, Brushes.White, tx + 10, ty + 7);
                            using (SolidBrush sub = new SolidBrush(Color.FromArgb(255, 255, 214, 150)))
                                g.DrawString(t2, tipFont, sub, tx + 10, ty + 7 + s1.Height);
                        }
                    }
                }
            }
        }
    }
    // ---------- 圆角按钮 v1.2 ----------
    enum BtnKind { Primary, Secondary }

    class ModernButton : Control
    {
        BtnKind _kind;
        bool _hover;
        bool _down;
        public Color Accent = Color.FromArgb(255, 79, 124, 255);
        public Color AccentHover = Color.FromArgb(255, 66, 105, 240);
        public int Radius = 8;

        public ModernButton(string text, BtnKind kind)
        {
            Text = text;
            _kind = kind;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Height = 32;
            BackColor = Color.White;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

            Color fg, bg1, bg2;
            if (_kind == BtnKind.Primary)
            {
                Color baseC = _hover ? AccentHover : Accent;
                Color light = Lighten(baseC, 18);
                bg1 = light; bg2 = baseC;
                fg = Color.White;
            }
            else
            {
                bg1 = _hover ? Color.FromArgb(255, 240, 244, 252) : Color.White;
                bg2 = bg1;
                fg = Color.FromArgb(255, 51, 65, 85);
                Ui.DrawRound(g, rc, Radius, Color.FromArgb(255, 214, 222, 235), 1f);
            }

            using (GraphicsPath p = Ui.Round(rc, Radius))
            using (LinearGradientBrush br = new LinearGradientBrush(rc, bg1, bg2, 90f))
            {
                g.FillPath(br, p);
                if (_down && _kind == BtnKind.Primary)
                {
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(36, 0, 0, 0)))
                        g.FillPath(dim, p);
                }
            }

            using (Font f = Ui.F(9.5f, true))
            using (SolidBrush fb = new SolidBrush(fg))
            {
                SizeF sz = g.MeasureString(Text, f);
                g.DrawString(Text, f, fb, (Width - sz.Width) / 2f, (Height - sz.Height) / 2f - 0.5f);
            }
        }

        static Color Lighten(Color c, int amt)
        {
            int r = Math.Min(255, c.R + amt);
            int g2 = Math.Min(255, c.G + amt);
            int b = Math.Min(255, c.B + amt);
            return Color.FromArgb(c.A, r, g2, b);
        }
    }

    // ---------- 标题栏窗口按钮 ----------
    enum CapType { Min, Max, Close }

    class CaptionButton : Control
    {
        CapType _type;
        bool _hover;

        public CaptionButton(CapType type)
        {
            _type = type;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Size = new Size(46, 32);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover)
            {
                Color bg = (_type == CapType.Close) ? Color.FromArgb(255, 232, 17, 35) : Color.FromArgb(255, 237, 241, 248);
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillRectangle(b, 0, 0, Width, Height);
            }
            Color c = (_hover && _type == CapType.Close) ? Color.White : Color.FromArgb(255, 90, 100, 118);
            using (Pen pen = new Pen(c, 1.4f))
            {
                float cx = Width / 2f, cy = Height / 2f;
                if (_type == CapType.Min)
                {
                    g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
                }
                else if (_type == CapType.Max)
                {
                    g.DrawRectangle(pen, cx - 7, cy - 6, 14, 11);
                    g.DrawLine(pen, cx - 4, cy - 6, cx + 4, cy - 6);
                }
                else
                {
                    g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                    g.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
                }
            }
        }
    }

    // ---------- 统计卡片 v1.2 ----------
    class StatCard : Control
    {
        string _title;
        Color _accent;
        string _value = "-";

        public StatCard(string title, Color accent)
        {
            _title = title;
            _accent = accent;
            DoubleBuffered = true;
            Height = 84;
        }

        public void SetValue(string v)
        {
            _value = v;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

            // 卡片底
            Ui.FillRound(g, rc, 12, Color.White);
            Ui.DrawRound(g, rc, 12, Color.FromArgb(255, 232, 237, 246), 1f);

            // 左上 accent 竖条
            using (GraphicsPath p = Ui.Round(new RectangleF(15, 17, 4, 30), 2f))
            using (SolidBrush ab = new SolidBrush(_accent))
                g.FillPath(ab, p);

            // 标题
            using (Font tf = Ui.F(8.8f, false))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(255, 148, 158, 176)))
                g.DrawString(_title, tf, tb, 27, 13);

            // 数值（自动缩小字号适配宽度）
            float fs = 16f;
            while (fs > 8.5f)
            {
                using (Font vf = Ui.F(fs, true))
                {
                    SizeF sz = g.MeasureString(_value, vf);
                    if (sz.Width <= Width - 34)
                    {
                        using (SolidBrush vb = new SolidBrush(Color.FromArgb(255, 34, 45, 62)))
                            g.DrawString(_value, vf, vb, 27, 43);
                        break;
                    }
                }
                fs -= 0.7f;
            }
        }
    }
    // ---------- Logo 徽标 ----------
    class LogoBadge : Control
    {
        public LogoBadge()
        {
            DoubleBuffered = true;
            Size = new Size(24, 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rc = new RectangleF(0.5f, 0.5f, 23f, 23f);
            Ui.FillRoundGrad(g, rc, 7, Color.FromArgb(255, 110, 168, 255), Color.FromArgb(255, 79, 124, 255), 135f);
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, 5, 13, 4, 6);
                g.FillRectangle(w, 10.5f, 9, 4, 10);
                g.FillRectangle(w, 16, 5, 4, 14);
            }
        }
    }

    // ---------- 主窗口 v1.2 ----------
    class MainForm : Form
    {
        public static string AutoShotPath;
        public static int AutoShotView = -1;

        UsageData _data;
        UsageData _fullData;
        string _dataDir;

        StatCard _cardTotal, _cardTasks, _cardPerTask, _cardInput, _cardOutput, _cardCached, _cardReason, _cardThreads;
        ComboBox _cboView, _cboMetric, _cboAgent;
        ChartView _chart;
        DataGridView _grid;
        ModernButton _btnRefresh, _btnOpen;
        Label _lblStatus;
        CaptionButton _btnMax;

        static readonly Color BG = Color.FromArgb(255, 244, 246, 251);
        static readonly Color CARD_BORDER = Color.FromArgb(255, 231, 236, 245);
        static readonly Color INK = Color.FromArgb(255, 31, 41, 55);
        static readonly Color SUB = Color.FromArgb(255, 130, 142, 160);

        public MainForm()
        {
            _dataDir = UsageLoader.DefaultSessionsPath();
            BuildUi();
            Shown += delegate { OnShownOnce(); };
        }

        void BuildUi()
        {
            Text = "AI Agent Token 用量";
            Font = Ui.F(9.5f, false);
            BackColor = BG;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 840);
            MinimumSize = new Size(980, 640);
            DoubleBuffered = true;

            // 根布局：显式分行，避免 Dock 顺序歧义
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.BackColor = BG;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));   // 标题栏
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));   // 顶部操作行
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));  // 统计卡片
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));   // 筛选工具行
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));   // 底部状态
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 图表 + 表格
            Controls.Add(root);

            // ---- 第 0 行：标题栏 ----
            Panel titleBar = new Panel();
            titleBar.Dock = DockStyle.Fill;
            titleBar.BackColor = Color.White;
            root.Controls.Add(titleBar, 0, 0);

            LogoBadge logo = new LogoBadge();
            logo.Location = new Point(18, 11);
            titleBar.Controls.Add(logo);

            Label appTitle = new Label();
            appTitle.Text = "AI Agent Token 用量";
            appTitle.Font = Ui.F(11f, true);
            appTitle.ForeColor = INK;
            appTitle.AutoSize = true;
            appTitle.Location = new Point(50, 13);
            titleBar.Controls.Add(appTitle);

            Label ver = new Label();
            ver.Text = "v1.4";
            ver.Font = Ui.F(8.5f, false);
            ver.ForeColor = Color.FromArgb(255, 79, 124, 255);
            ver.AutoSize = true;
            ver.Location = new Point(appTitle.Right + 10, 17);
            titleBar.Controls.Add(ver);

            CaptionButton btnMin = new CaptionButton(CapType.Min);
            CaptionButton btnClose = new CaptionButton(CapType.Close);
            _btnMax = new CaptionButton(CapType.Max);
            btnMin.Location = new Point(0, 7);
            _btnMax.Location = new Point(0, 7);
            btnClose.Location = new Point(0, 7);
            titleBar.Controls.Add(btnMin);
            titleBar.Controls.Add(_btnMax);
            titleBar.Controls.Add(btnClose);
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            _btnMax.Click += delegate { ToggleMax(); };
            btnClose.Click += delegate { Close(); };
            titleBar.Resize += delegate
            {
                int w = titleBar.Width;
                btnClose.Left = w - 46;
                _btnMax.Left = w - 92;
                btnMin.Left = w - 138;
            };

            titleBar.MouseDown += delegate(object s, MouseEventArgs e2) { if (e2.Button == MouseButtons.Left) DragWindow(); };
            titleBar.MouseDoubleClick += delegate { ToggleMax(); };
            appTitle.MouseDown += delegate(object s, MouseEventArgs e2) { if (e2.Button == MouseButtons.Left) DragWindow(); };
            ver.MouseDown += delegate(object s, MouseEventArgs e2) { if (e2.Button == MouseButtons.Left) DragWindow(); };

            // ---- 第 1 行：顶部操作行 ----
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = BG;
            root.Controls.Add(header, 0, 1);

            Label hTitle = new Label();
            hTitle.Text = "用量总览";
            hTitle.Font = Ui.F(16f, true);
            hTitle.ForeColor = INK;
            hTitle.AutoSize = true;
            hTitle.Location = new Point(24, 8);
            header.Controls.Add(hTitle);

            Label hSub = new Label();
            hSub.Text = "本地 AI Agent 会话 token 消耗统计";
            hSub.Font = Ui.F(9f, false);
            hSub.ForeColor = SUB;
            hSub.AutoSize = true;
            hSub.Location = new Point(26, 36);
            header.Controls.Add(hSub);

            _btnOpen = new ModernButton("打开数据目录", BtnKind.Secondary);
            _btnOpen.Size = new Size(132, 34);
            _btnRefresh = new ModernButton("刷新数据", BtnKind.Primary);
            _btnRefresh.Size = new Size(116, 34);
            _btnRefresh.Location = new Point(0, 14);
            _btnOpen.Location = new Point(0, 14);
            header.Controls.Add(_btnRefresh);
            header.Controls.Add(_btnOpen);
            header.Resize += delegate
            {
                int w = header.Width;
                _btnOpen.Left = w - 24 - 132;
                _btnRefresh.Left = w - 24 - 132 - 12 - 116;
            };

            // ---- 第 2 行：统计卡片 ----
            TableLayoutPanel stats = new TableLayoutPanel();
            stats.Dock = DockStyle.Fill;
            stats.BackColor = BG;
            stats.ColumnCount = 8;
            stats.RowCount = 1;
            stats.Padding = new Padding(18, 4, 18, 0);
            for (int i = 0; i < 8; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 8f));
            root.Controls.Add(stats, 0, 2);
            stats.Controls.Add(MakeCard("累计 Tokens", Color.FromArgb(255, 79, 124, 255), out _cardTotal), 0, 0);
            stats.Controls.Add(MakeCard("完成任务", Color.FromArgb(255, 52, 211, 153), out _cardTasks), 1, 0);
            stats.Controls.Add(MakeCard("每任务均耗", Color.FromArgb(255, 244, 114, 182), out _cardPerTask), 2, 0);
            stats.Controls.Add(MakeCard("输入", Color.FromArgb(255, 56, 189, 248), out _cardInput), 3, 0);
            stats.Controls.Add(MakeCard("输出", Color.FromArgb(255, 45, 212, 191), out _cardOutput), 4, 0);
            stats.Controls.Add(MakeCard("缓存读取", Color.FromArgb(255, 251, 191, 36), out _cardCached), 5, 0);
            stats.Controls.Add(MakeCard("推理 tokens", Color.FromArgb(255, 167, 139, 250), out _cardReason), 6, 0);
            stats.Controls.Add(MakeCard("会话数", Color.FromArgb(255, 148, 163, 184), out _cardThreads), 7, 0);

            // ---- 第 3 行：筛选工具行 ----
            Panel tools = new Panel();
            tools.Dock = DockStyle.Fill;
            tools.BackColor = BG;
            root.Controls.Add(tools, 0, 3);

            Label lv = new Label(); lv.Text = "视图"; lv.Font = Ui.F(9f, true); lv.ForeColor = SUB; lv.AutoSize = true; lv.Location = new Point(26, 16);
            _cboView = MakeCombo(new object[] { "按天", "按会话", "趋势", "按模型" });
            _cboView.Location = new Point(70, 10); _cboView.Width = 104;
            Label lm = new Label(); lm.Text = "指标"; lm.Font = Ui.F(9f, true); lm.ForeColor = SUB; lm.AutoSize = true; lm.Location = new Point(200, 16);
            _cboMetric = MakeCombo(new object[] { "总 Tokens", "输入", "输出", "缓存读取", "推理 tokens" });
            _cboMetric.Location = new Point(244, 10); _cboMetric.Width = 136;
            Label la = new Label(); la.Text = "Agent"; la.Font = Ui.F(9f, true); la.ForeColor = SUB; la.AutoSize = true; la.Location = new Point(404, 16);
            _cboAgent = MakeCombo(new object[] { "全部", "Codex", "Claude" });
            _cboAgent.Location = new Point(452, 10); _cboAgent.Width = 108;
            Label hint = new Label();
            hint.Text = "悬停柱状图查看精确数值";
            hint.Font = Ui.F(8.5f, false);
            hint.ForeColor = SUB;
            hint.AutoSize = true;
            hint.Location = new Point(0, 17);
            tools.Controls.Add(lv); tools.Controls.Add(_cboView); tools.Controls.Add(lm); tools.Controls.Add(_cboMetric); tools.Controls.Add(la); tools.Controls.Add(_cboAgent); tools.Controls.Add(hint);
            tools.Resize += delegate { hint.Left = tools.Width - 24 - hint.Width; };

            // ---- 第 4 行：底部状态 ----
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = BG;
            root.Controls.Add(footer, 0, 4);
            _lblStatus = new Label();
            _lblStatus.Text = "就绪";
            _lblStatus.Font = Ui.F(8.5f, false);
            _lblStatus.ForeColor = SUB;
            _lblStatus.AutoSize = true;
            _lblStatus.Location = new Point(26, 5);
            footer.Controls.Add(_lblStatus);

            // ---- 第 5 行：图表 + 表格 ----
            TableLayoutPanel main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.BackColor = BG;
            main.ColumnCount = 1;
            main.RowCount = 2;
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 54f));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 46f));
            root.Controls.Add(main, 0, 5);

            _chart = new ChartView();
            _chart.Dock = DockStyle.Fill;
            _chart.Margin = new Padding(18, 0, 18, 6);
            main.Controls.Add(_chart, 0, 0);

            _grid = MakeGrid();
            _grid.Dock = DockStyle.Fill;
            _grid.Margin = new Padding(18, 6, 18, 8);
            main.Controls.Add(_grid, 0, 1);

            _cboView.SelectedIndexChanged += delegate { RefreshChart(); };
            _cboMetric.SelectedIndexChanged += delegate { RefreshChart(); };
            _cboAgent.SelectedIndexChanged += delegate { ApplyAgentFilter(); };
            _btnRefresh.Click += delegate { LoadData(); };
            _btnOpen.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
                    System.Diagnostics.Process.Start("explorer.exe", _dataDir);
                }
                catch (Exception ex) { MessageBox.Show(this, "无法打开目录: " + ex.Message, "提示"); }
            };
        }
        StatCard MakeCard(string title, Color accent, out StatCard card)
        {
            card = new StatCard(title, accent);
            card.Margin = new Padding(4);
            card.Dock = DockStyle.Fill;
            return card;
        }

        ComboBox MakeCombo(object[] items)
        {
            ComboBox cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.FlatStyle = FlatStyle.Flat;
            cb.Font = Ui.F(9.5f, false);
            cb.BackColor = Color.White;
            cb.Height = 28;
            cb.Items.AddRange(items);
            cb.SelectedIndex = 0;
            return cb;
        }

        DataGridView MakeGrid()
        {
            DataGridView g = new DataGridView();
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = Color.White;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeight = 34;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(255, 241, 245, 250);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(255, 71, 85, 105);
            g.ColumnHeadersDefaultCellStyle.Font = Ui.F(9f, true);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.DefaultCellStyle.BackColor = Color.White;
            g.DefaultCellStyle.ForeColor = Color.FromArgb(255, 31, 41, 55);
            g.DefaultCellStyle.Font = Ui.F(9f, false);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 232, 240, 254);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(255, 30, 41, 59);
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(255, 248, 250, 253);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.GridColor = Color.FromArgb(255, 238, 242, 248);
            return g;
        }

        void ToggleMax()
        {
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            else WindowState = FormWindowState.Maximized;
            _btnMax.Invalidate();
            ApplyRegion();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        void DragWindow()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);
        }

        void ApplyRegion()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                Region = null;
            }
            else
            {
                using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), 14f))
                    Region = new Region(p);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        void OnShownOnce()
        {
            ApplyRegion();
            LoadData();
            if (MainForm.AutoShotView >= 0 && _cboView.Items.Count > MainForm.AutoShotView) _cboView.SelectedIndex = MainForm.AutoShotView;
            if (!String.IsNullOrEmpty(AutoShotPath))
            {
                try
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(700);
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(AutoShotPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch { }
                Close();
            }
        }

        const int WM_NCHITTEST = 0x84;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                  HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                int x = (short)(m.LParam.ToInt64() & 0xFFFF);
                int y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(x, y));
                int edge = 7;
                bool l = p.X < edge, r = p.X > Width - edge;
                bool t = p.Y < edge, b = p.Y > Height - edge;
                if (t && l) m.Result = (IntPtr)HTTOPLEFT;
                else if (t && r) m.Result = (IntPtr)HTTOPRIGHT;
                else if (b && l) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (b && r) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (l) m.Result = (IntPtr)HTLEFT;
                else if (r) m.Result = (IntPtr)HTRIGHT;
                else if (t) m.Result = (IntPtr)HTTOP;
                else if (b) m.Result = (IntPtr)HTBOTTOM;
                return;
            }
            base.WndProc(ref m);
        }

        void LoadData()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                DateTime sw = DateTime.UtcNow;
                _fullData = UsageLoader.LoadAll(_dataDir);
                ApplyAgentFilter();
                double ms = (DateTime.UtcNow - sw).TotalMilliseconds;
                if (_fullData != null)
                    _lblStatus.Text = _lblStatus.Text + " · 扫描耗时 " + ms.ToString("0") + " ms";
                if (_fullData != null && _fullData.Errors.Count > 0 && _fullData.Records.Count == 0)
                    MessageBox.Show(this, "未读取到有效记录。\n" + String.Join("\n", _fullData.Errors), "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        void ApplyAgentFilter()
        {
            if (_fullData == null) return;
            string agent = (_cboAgent.SelectedItem != null) ? _cboAgent.SelectedItem.ToString() : "全部";
            _data = UsageLoader.FilterByAgent(_fullData, agent);
            UpdateStats();
            FillGrid();
            RefreshChart();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (_data == null) return;
            long total = 0;
            foreach (UsageRecord r in _data.Records) total += r.Total;
            string range = _data.Records.Count > 0
                ? _data.MinTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " ~ " + _data.MaxTime.ToLocalTime().ToString("MM-dd HH:mm")
                : "";
            string agents = String.Join("/", _data.Records.Select(r => r.Agent).Distinct());
            _lblStatus.Text = "Agent: " + (agents.Length == 0 ? "-" : agents) + " · 任务 " + _data.Tasks.Count.ToString()
                + " · 记录 " + _data.Records.Count.ToString() + " · 文件 " + _data.FileCount.ToString()
                + (_data.Records.Count > 0 ? " · " + range : "")
                + (_data.Errors.Count > 0 ? "（" + _data.Errors.Count.ToString() + " 警告）" : "");
        }
        void UpdateStats()
        {
            if (_data == null) return;
            long total = 0, inp = 0, outp = 0, cached = 0, reas = 0;
            HashSet<string> threads = new HashSet<string>();
            foreach (UsageRecord r in _data.Records)
            {
                total += r.Total; inp += r.Input; outp += r.Output; cached += r.Cached; reas += r.Reasoning;
                threads.Add(r.Agent + "|" + r.ThreadId);
            }
            _cardTotal.SetValue(Short(total));
            _cardTasks.SetValue(_data.Tasks.Count.ToString("N0"));
            long avg = (_data.Tasks.Count > 0) ? total / _data.Tasks.Count : 0;
            _cardPerTask.SetValue(_data.Tasks.Count > 0 ? Short(avg) : "-");
            _cardInput.SetValue(Short(inp));
            _cardOutput.SetValue(Short(outp));
            _cardCached.SetValue(Short(cached));
            _cardReason.SetValue(Short(reas));
            _cardThreads.SetValue(threads.Count.ToString());
        }
        static string Short(long v)
        {
            if (v >= 1000000000L) return (v / 1000000000.0).ToString("0.00") + "B";
            if (v >= 1000000L) return (v / 1000000.0).ToString("0.00") + "M";
            if (v >= 1000L) return (v / 1000.0).ToString("0.0") + "K";
            return v.ToString("N0");
        }

        void RefreshChart()
        {
            if (_data == null) return;
            int view = _cboView.SelectedIndex;
            int metric = _cboMetric.SelectedIndex;
            List<ChartItem> items = new List<ChartItem>();
            string title = _cboMetric.Text;
            bool line = false;

            if (view == 0)
            {
                List<DayAgg> days = UsageLoader.AggregateByDay(_data);
                for (int i = 0; i < days.Count; i++)
                {
                    DayAgg a = days[i];
                    string lab = (days.Count > 14) ? a.Day.ToString("MM-dd") : a.Day.ToString("M月d日");
                    items.Add(new ChartItem(lab, PickDay(a, metric)));
                }
                title = "按天 · " + _cboMetric.Text;
            }
            else if (view == 1)
            {
                foreach (ThreadAgg a in UsageLoader.AggregateByThread(_data))
                {
                    string lab = (a.Agent ?? "") + "·" + (a.Name ?? UsageLoader.ShortId(a.ThreadId));
                    items.Add(new ChartItem(lab, PickThread(a, metric)));
                }
                title = "按会话 · " + _cboMetric.Text;
            }
            else if (view == 2)
            {
                List<TrendPoint> tr = UsageLoader.AggregateTrend(_data);
                bool multiDay = tr.Count > 0 && (tr[tr.Count - 1].T.Date != tr[0].T.Date);
                for (int i = 0; i < tr.Count; i++)
                {
                    TrendPoint p = tr[i];
                    string lab = multiDay ? p.T.ToString("MM-dd") : p.T.ToString("HH:mm");
                    items.Add(new ChartItem(lab, PickTrend(p, metric)));
                }
                title = "趋势 · " + _cboMetric.Text;
                line = true;
            }
            else
            {
                foreach (ModelAgg a in UsageLoader.AggregateByModel(_data))
                    items.Add(new ChartItem(a.Model, PickModel(a, metric)));
                title = "按模型 · " + _cboMetric.Text;
            }
            _chart.SetItems(items, title, line);
        }

        long PickDay(DayAgg a, int metric)
        {
            switch (metric) { case 1: return a.Input; case 2: return a.Output; case 3: return a.Cached; case 4: return a.Reasoning; default: return a.Total; }
        }
        long PickThread(ThreadAgg a, int metric)
        {
            switch (metric) { case 1: return a.Input; case 2: return a.Output; case 3: return a.Cached; case 4: return a.Reasoning; default: return a.Total; }
        }
        long PickModel(ModelAgg a, int metric)
        {
            switch (metric) { case 1: return a.Input; case 2: return a.Output; case 3: return a.Cached; case 4: return a.Reasoning; default: return a.Total; }
        }
        long PickTrend(TrendPoint a, int metric)
        {
            switch (metric) { case 1: return a.Input; case 2: return a.Output; case 3: return a.Cached; case 4: return a.Reasoning; default: return a.Total; }
        }

        void FillGrid()
        {
            _grid.SuspendLayout();
            _grid.Columns.Clear();
            AddCol("agent", "Agent", 48, DataGridViewContentAlignment.MiddleLeft, false);
            AddCol("model", "模型", 95, DataGridViewContentAlignment.MiddleLeft, false);
            AddCol("time", "时间", 125, DataGridViewContentAlignment.MiddleLeft, false);
            AddCol("thread", "会话", 185, DataGridViewContentAlignment.MiddleLeft, false);
            AddCol("inp", "输入", 65, DataGridViewContentAlignment.MiddleRight, true);
            AddCol("cached", "缓存读取", 70, DataGridViewContentAlignment.MiddleRight, true);
            AddCol("out", "输出", 65, DataGridViewContentAlignment.MiddleRight, true);
            AddCol("reason", "推理", 65, DataGridViewContentAlignment.MiddleRight, true);
            AddCol("total", "总量", 75, DataGridViewContentAlignment.MiddleRight, true);

            List<UsageRecord> sorted = new List<UsageRecord>();
            if (_data != null)
            {
                sorted.AddRange(_data.Records);
                sorted.Sort((x, y) => y.Time.CompareTo(x.Time));
                if (sorted.Count > 5000) sorted = sorted.GetRange(0, 5000);
                _grid.Rows.Clear();
                foreach (UsageRecord r in sorted)
                {
                    int i = _grid.Rows.Add();
                    _grid.Rows[i].Cells[0].Value = r.Agent;
                    _grid.Rows[i].Cells[1].Value = String.IsNullOrEmpty(r.Model) ? "?" : r.Model;
                    _grid.Rows[i].Cells[2].Value = r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    _grid.Rows[i].Cells[3].Value = UsageLoader.ThreadLabel(_data, r.Agent, r.ThreadId);
                    _grid.Rows[i].Cells[4].Value = r.Input.ToString("N0");
                    _grid.Rows[i].Cells[5].Value = r.Cached.ToString("N0");
                    _grid.Rows[i].Cells[6].Value = r.Output.ToString("N0");
                    _grid.Rows[i].Cells[7].Value = r.Reasoning.ToString("N0");
                    _grid.Rows[i].Cells[8].Value = r.Total.ToString("N0");
                }
            }
            _grid.ResumeLayout();
        }

        void AddCol(string name, string header, float fill, DataGridViewContentAlignment align, bool numeric)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.Name = name;
            c.HeaderText = header;
            c.FillWeight = fill;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            if (numeric) c.DefaultCellStyle.Format = "N0";
            _grid.Columns.Add(c);
        }
    }
}
