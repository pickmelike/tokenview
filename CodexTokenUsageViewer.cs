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
        public string ThreadId;
        public string TurnId;
        public string ResponseId;
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public long Reasoning;
        public long Total;
    }

    class UsageData
    {
        public List<UsageRecord> Records = new List<UsageRecord>();
        public Dictionary<string, string> ThreadNames = new Dictionary<string, string>();
        public List<string> Errors = new List<string>();
        public int FileCount;
        public DateTime MinTime = DateTime.MaxValue;
        public DateTime MaxTime = DateTime.MinValue;
    }

    class DayAgg
    {
        public DateTime Day;
        public long Total, Input, Output, Cached, Reasoning;
        public int Turns;
    }

    class ThreadAgg
    {
        public string ThreadId;
        public string Name;
        public long Total, Input, Output, Cached, Reasoning;
        public int Turns;
    }

    // ---------- 数据加载 ----------
    static class UsageLoader
    {
        public static string DefaultSessionsPath()
        {
            string root = Environment.GetEnvironmentVariable("USERPROFILE");
            if (String.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrEmpty(root)) root = @"C:\Users\" + Environment.UserName;
            return Path.Combine(root, ".codex", "sessions");
        }

        public static UsageData LoadAll(string sessionsDir)
        {
            UsageData data = new UsageData();
            if (!Directory.Exists(sessionsDir)) { data.Errors.Add("目录不存在: " + sessionsDir); return data; }

            // 线程名映射
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
                                data.ThreadNames[id.ToString()] = name;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { data.Errors.Add("读取 session_index 失败: " + ex.Message); }

            // 扫描 rollout 文件
            HashSet<string> seen = new HashSet<string>();
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            List<string> files = new List<string>();
            try { files.AddRange(Directory.GetFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)); }
            catch (Exception ex) { data.Errors.Add("扫描目录失败: " + ex.Message); }
            data.FileCount = files.Count;

            foreach (string file in files)
            {
                try
                {
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            UsageRecord rec = TryParse(js, line);
                            if (rec == null) continue;
                            string key = String.IsNullOrEmpty(rec.ResponseId) ? (rec.TurnId + "|" + rec.Time.Ticks.ToString()) : rec.ResponseId;
                            if (!seen.Add(key)) continue;   // 去重
                            data.Records.Add(rec);
                        }
                    }
                }
                catch (Exception ex) { data.Errors.Add("读取文件失败: " + Path.GetFileName(file) + " -> " + ex.Message); }
            }

            if (data.Records.Count > 0)
            {
                data.MinTime = data.Records.Min(r => r.Time);
                data.MaxTime = data.Records.Max(r => r.Time);
            }
            return data;
        }

        static UsageRecord TryParse(JavaScriptSerializer js, string line)
        {
            try
            {
                Dictionary<string, object> root = js.DeserializeObject(line) as Dictionary<string, object>;
                if (root == null) return null;
                object tv;
                if (!root.TryGetValue("type", out tv) || tv == null || tv.ToString() != "token_usage_record") return null;

                Dictionary<string, object> payload = GetDict(root, "payload");
                if (payload == null) return null;
                Dictionary<string, object> usage = GetDict(payload, "usage");
                if (usage == null) return null;

                UsageRecord rec = new UsageRecord();
                string ts = GetStr(root, "timestamp");
                DateTime t;
                if (!String.IsNullOrEmpty(ts) && DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) rec.Time = t;
                else rec.Time = DateTime.UtcNow;
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
                ThreadAgg a;
                if (!map.TryGetValue(r.ThreadId, out a))
                {
                    a = new ThreadAgg(); a.ThreadId = r.ThreadId;
                    string nm;
                    a.Name = (d.ThreadNames.TryGetValue(r.ThreadId, out nm) && !String.IsNullOrEmpty(nm)) ? nm : ShortId(r.ThreadId);
                    map[r.ThreadId] = a;
                }
                a.Input += r.Input; a.Output += r.Output; a.Cached += r.Cached; a.Reasoning += r.Reasoning;
                a.Total += r.Total; a.Turns++;
            }
            List<ThreadAgg> list = map.Values.ToList();
            list.Sort((x, y) => y.Total.CompareTo(x.Total));
            return list;
        }

        public static string ShortId(string id)
        {
            if (String.IsNullOrEmpty(id)) return "(unknown)";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        public static string ThreadLabel(UsageData d, string threadId)
        {
            string nm;
            if (d.ThreadNames.TryGetValue(threadId, out nm) && !String.IsNullOrEmpty(nm)) return nm;
            return ShortId(threadId);
        }

        public static string BuildReport(UsageData d)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Codex Token Usage Report ===");
            sb.AppendLine("数据目录: " + DefaultSessionsPath());
            sb.AppendLine("扫描文件数: " + d.FileCount.ToString());
            sb.AppendLine("记录数(去重后): " + d.Records.Count.ToString());
            if (d.Records.Count > 0)
            {
                sb.AppendLine("时间范围: " + d.MinTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " ~ " + d.MaxTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                long tIn = d.Records.Sum(r => r.Input), tOut = d.Records.Sum(r => r.Output), tCached = d.Records.Sum(r => r.Cached), tTotal = d.Records.Sum(r => r.Total);
                sb.AppendLine("总 Tokens: " + tTotal.ToString("N0"));
                sb.AppendLine("输入: " + tIn.ToString("N0") + ", 缓存读取: " + tCached.ToString("N0") + ", 输出: " + tOut.ToString("N0"));
                sb.AppendLine();
                sb.AppendLine("--- 按天 ---");
                foreach (DayAgg a in AggregateByDay(d))
                    sb.AppendLine(a.Day.ToString("yyyy-MM-dd") + " | 输入 " + a.Input.ToString("N0") + " | 缓存 " + a.Cached.ToString("N0") + " | 输出 " + a.Output.ToString("N0") + " | 总计 " + a.Total.ToString("N0") + " | 轮次 " + a.Turns.ToString());
                sb.AppendLine();
                sb.AppendLine("--- 按会话 ---");
                foreach (ThreadAgg a in AggregateByThread(d))
                    sb.AppendLine((a.Name ?? "") + " | 输入 " + a.Input.ToString("N0") + " | 缓存 " + a.Cached.ToString("N0") + " | 输出 " + a.Output.ToString("N0") + " | 总计 " + a.Total.ToString("N0") + " | 轮次 " + a.Turns.ToString());
            }
            foreach (string e in d.Errors) sb.AppendLine("[warn] " + e);
            return sb.ToString();
        }
    }

    // ---------- 自绘柱状图 ----------
    class ChartItem
    {
        public string Label;
        public long Value;
        public ChartItem(string label, long value) { Label = label; Value = value; }
    }

    class ChartView : Control
    {
        List<ChartItem> _items = new List<ChartItem>();
        string _title = "";
        int _hover = -1;

        public ChartView()
        {
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
            this.BackColor = Color.White;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetItems(IEnumerable<ChartItem> items, string title)
        {
            _items = (items == null) ? new List<ChartItem>() : items.ToList();
            _title = title ?? "";
            _hover = -1;
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
            Rectangle plot = PlotRect();
            if (!plot.Contains(x, y)) return -1;
            double n = _items.Count;
            double slot = plot.Width / n;
            int i = (int)((x - plot.X) / slot);
            if (i < 0 || i >= _items.Count) return -1;
            return i;
        }

        Rectangle PlotRect()
        {
            int left = 78, top = 46, right = 18, bottom = 58;
            return new Rectangle(left, top, Math.Max(10, this.ClientSize.Width - left - right), Math.Max(10, this.ClientSize.Height - top - bottom));
        }

        static string ShortNum(long v)
        {
            if (v >= 1000000000L) return (v / 1000000000.0).ToString("0.##") + "B";
            if (v >= 1000000L) return (v / 1000000.0).ToString("0.##") + "M";
            if (v >= 1000L) return (v / 1000.0).ToString("0.##") + "K";
            return v.ToString();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.BackColor);
            int W = this.ClientSize.Width, H = this.ClientSize.Height;

            // 标题
            using (Font titleFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(_title, titleFont);
                using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(40, 50, 70)))
                    g.DrawString(_title, titleFont, titleBrush, (W - sz.Width) / 2f, 12f);
            }

            if (_items.Count == 0)
            {
                using (Font f = new Font("Microsoft YaHei UI", 10f))
                    g.DrawString("暂无数据。请确认 ~/.codex/sessions 下存在 rollout-*.jsonl 记录。", f, Brushes.Gray, 20, H / 2f - 10);
                return;
            }

            Rectangle plot = PlotRect();
            long maxV = Math.Max(1, _items.Max(i => i.Value));

            // Y 轴刻度
            double rawStep = maxV / 5.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double norm = rawStep / mag;
            double nice = norm < 1 ? 1 : norm < 2 ? 2 : norm < 5 ? 5 : 10;
            double step = nice * mag;
            long topV = (long)(Math.Ceiling(maxV / step) * step);
            if (topV <= 0) topV = 1;

            using (Font tickFont = new Font("Microsoft YaHei UI", 8.5f))
            using (Pen gridPen = new Pen(Color.FromArgb(225, 230, 238)))
            using (Pen axisPen = new Pen(Color.FromArgb(170, 180, 195)))
            {
                for (long v = 0; v <= topV; v += (long)step)
                {
                    float y = plot.Bottom - (float)((double)v / topV) * plot.Height;
                    g.DrawLine(gridPen, plot.X, y, plot.Right, y);
                    string s = ShortNum(v);
                    SizeF sz = g.MeasureString(s, tickFont);
                    using (SolidBrush tickBrush = new SolidBrush(Color.FromArgb(110, 120, 140)))
                        g.DrawString(s, tickFont, tickBrush, plot.X - sz.Width - 6, y - sz.Height / 2f);
                }
                g.DrawLine(axisPen, plot.X, plot.Bottom, plot.Right, plot.Bottom);
                g.DrawLine(axisPen, plot.X, plot.Top, plot.X, plot.Bottom);
            }

            // 柱子
            double n = _items.Count;
            double slot = plot.Width / n;
            double barW = Math.Min(slot * 0.62, 64);
            int labelEvery = (int)Math.Ceiling(n / 14.0);
            if (labelEvery < 1) labelEvery = 1;

            using (Font labelFont = new Font("Microsoft YaHei UI", 8.5f))
            using (Font tipFont = new Font("Microsoft YaHei UI", 9f))
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    double cx = plot.X + slot * i + slot / 2;
                    double h = ((double)_items[i].Value / topV) * plot.Height;
                    if (h < 1 && _items[i].Value > 0) h = 1;
                    RectangleF bar = new RectangleF((float)(cx - barW / 2), (float)(plot.Bottom - h), (float)barW, (float)h);

                    bool hot = (i == _hover);
                    if (hot)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 180, 60)))
                            g.FillRectangle(b, bar);
                    }
                    else
                    {
                        using (LinearGradientBrush b = new LinearGradientBrush(bar, Color.FromArgb(47, 128, 237), Color.FromArgb(86, 204, 242), LinearGradientMode.Vertical))
                            g.FillRectangle(b, bar);
                        using (Pen p = new Pen(Color.FromArgb(40, 47, 128, 237))) g.DrawRectangle(p, bar.X, bar.Y, bar.Width, bar.Height);
                    }

                    // X 轴标签
                    if (i % labelEvery == 0)
                    {
                        string lab = _items[i].Label;
                        if (lab.Length > 10) lab = lab.Substring(0, 10) + "…";
                        SizeF sz = g.MeasureString(lab, labelFont);
                        float lx = (float)(cx - sz.Width / 2);
                        lx = Math.Max(plot.X, Math.Min(plot.Right - sz.Width, lx));
                        using (SolidBrush labBrush = new SolidBrush(Color.FromArgb(100, 110, 130)))
                            g.DrawString(lab, labelFont, labBrush, lx, plot.Bottom + 8);
                    }

                    // 悬停 tooltip
                    if (hot && _items[i].Value > 0)
                    {
                        string txt = _items[i].Label + "\n" + _items[i].Value.ToString("N0") + " tokens";
                        SizeF sz = g.MeasureString(txt, tipFont);
                        float tw = sz.Width + 16, th = sz.Height + 10;
                        float tx = (float)(cx - tw / 2);
                        tx = Math.Max(plot.X, Math.Min(W - tw - 6, tx));
                        float ty = (float)(plot.Bottom - h - th - 6);
                        if (ty < plot.Top) ty = plot.Top + 2;
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 46, 56, 78)))
                            g.FillRectangle(bg, tx, ty, tw, th);
                        g.DrawString(txt, tipFont, Brushes.White, tx + 8, ty + 5);
                    }
                }
            }
        }
    }

    // ---------- 主窗口 ----------
    class MainForm : Form
    {
        public static string AutoShotPath;
        UsageData _data;
        Label _lblTotal, _lblInput, _lblOutput, _lblCached, _lblReasoning, _lblSessions, _lblRange;
        ToolStripStatusLabel _lblStatus;
        ComboBox _cboView, _cboMetric;
        ChartView _chart;
        DataGridView _grid;
        Button _btnRefresh, _btnOpenDir;
        string _dataDir;

        public MainForm()
        {
            _dataDir = UsageLoader.DefaultSessionsPath();
            BuildUi();
            this.Shown += delegate
            {
                LoadData();
                if (!String.IsNullOrEmpty(MainForm.AutoShotPath))
                {
                    try
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(500);
                        Application.DoEvents();
                        using (Bitmap bmp = new Bitmap(this.Width, this.Height))
                        {
                            this.DrawToBitmap(bmp, new Rectangle(0, 0, this.Width, this.Height));
                            bmp.Save(MainForm.AutoShotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch { }
                    this.Close();
                }
            };
        }

        void BuildUi()
        {
            this.Text = "Codex Token 用量可视化";
            this.Font = new Font("Microsoft YaHei UI", 9f);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(1180, 760);
            this.MinimumSize = new Size(920, 620);

            // 顶部统计卡
            TableLayoutPanel stats = new TableLayoutPanel();
            stats.Dock = DockStyle.Top;
            stats.Height = 86;
            stats.ColumnCount = 7;
            stats.RowCount = 1;
            stats.Padding = new Padding(8);
            for (int i = 0; i < 7; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7f));
            stats.Controls.Add(MakeCard("累计 Tokens", out _lblTotal, Color.FromArgb(47, 128, 237)), 0, 0);
            stats.Controls.Add(MakeCard("输入", out _lblInput, Color.FromArgb(86, 204, 242)), 1, 0);
            stats.Controls.Add(MakeCard("输出", out _lblOutput, Color.FromArgb(88, 214, 141)), 2, 0);
            stats.Controls.Add(MakeCard("缓存读取", out _lblCached, Color.FromArgb(255, 180, 60)), 3, 0);
            stats.Controls.Add(MakeCard("推理 tokens", out _lblReasoning, Color.FromArgb(190, 130, 255)), 4, 0);
            stats.Controls.Add(MakeCard("会话数", out _lblSessions, Color.FromArgb(90, 160, 255)), 5, 0);
            stats.Controls.Add(MakeCard("数据范围", out _lblRange, Color.FromArgb(150, 160, 175)), 6, 0);
            this.Controls.Add(stats);

            // 工具栏
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 46;
            bar.Padding = new Padding(10, 8, 10, 4);
            Label l1 = new Label(); l1.Text = "视图:"; l1.AutoSize = true; l1.Location = new Point(12, 15);
            _cboView = new ComboBox(); _cboView.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboView.Items.AddRange(new object[] { "按天", "按会话" });
            _cboView.SelectedIndex = 0;
            _cboView.Location = new Point(58, 12); _cboView.Width = 90;
            Label l2 = new Label(); l2.Text = "指标:"; l2.AutoSize = true; l2.Location = new Point(165, 15);
            _cboMetric = new ComboBox(); _cboMetric.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboMetric.Items.AddRange(new object[] { "总 Tokens", "输入", "输出", "缓存读取", "推理 tokens" });
            _cboMetric.SelectedIndex = 0;
            _cboMetric.Location = new Point(215, 12); _cboMetric.Width = 120;
            _btnRefresh = new Button(); _btnRefresh.Text = "刷新数据"; _btnRefresh.Location = new Point(355, 10); _btnRefresh.Size = new Size(92, 26);
            _btnOpenDir = new Button(); _btnOpenDir.Text = "打开数据目录"; _btnOpenDir.Location = new Point(455, 10); _btnOpenDir.Size = new Size(108, 26);
            bar.Controls.Add(l1); bar.Controls.Add(_cboView); bar.Controls.Add(l2); bar.Controls.Add(_cboMetric);
            bar.Controls.Add(_btnRefresh); bar.Controls.Add(_btnOpenDir);
            this.Controls.Add(bar);

            // 图表 + 明细
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;

            _chart = new ChartView();
            _chart.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(_chart);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            split.Panel2.Controls.Add(_grid);
            this.Controls.Add(split);

            // 状态栏
            StatusStrip strip = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel();
            _lblStatus.Text = "就绪";
            _lblStatus.Spring = true;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(_lblStatus);
            this.Controls.Add(strip);
            split.SplitterDistance = 380;
            split.Panel1MinSize = 220;
            split.Panel2MinSize = 180;

            _cboView.SelectedIndexChanged += delegate { RefreshChart(); };
            _cboMetric.SelectedIndexChanged += delegate { RefreshChart(); };
            _btnRefresh.Click += delegate { LoadData(); };
            _btnOpenDir.Click += delegate
            {
                try
                {
                    if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
                    System.Diagnostics.Process.Start("explorer.exe", _dataDir);
                }
                catch (Exception ex) { MessageBox.Show(this, "无法打开目录: " + ex.Message, "提示"); }
            };
        }

        Panel MakeCard(string title, out Label valueLabel, Color accent)
        {
            Panel p = new Panel();
            p.Margin = new Padding(4);
            p.BackColor = Color.White;
            p.Padding = new Padding(8, 4, 4, 2);
            Label t = new Label();
            t.Text = title;
            t.ForeColor = Color.FromArgb(120, 130, 145);
            t.Font = new Font("Microsoft YaHei UI", 8.5f);
            t.Dock = DockStyle.Top;
            t.Height = 22;
            Label v = new Label();
            v.Text = "-";
            v.ForeColor = accent;
            v.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            v.Dock = DockStyle.Fill;
            v.TextAlign = ContentAlignment.MiddleLeft;
            p.Controls.Add(v);
            p.Controls.Add(t);
            valueLabel = v;
            return p;
        }

        void LoadData()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                DateTime sw = DateTime.UtcNow;
                UsageData d = UsageLoader.LoadAll(_dataDir);
                _data = d;
                double ms = (DateTime.UtcNow - sw).TotalMilliseconds;
                UpdateStats();
                FillGrid();
                RefreshChart();
                _lblStatus.Text = "已加载: " + d.FileCount.ToString() + " 个文件, " + d.Records.Count.ToString() + " 条用量记录" +
                    (d.Errors.Count > 0 ? " (" + d.Errors.Count.ToString() + " 个警告)" : "") + " | 耗时 " + ms.ToString("0") + " ms | " + _dataDir;
                if (d.Errors.Count > 0 && d.Records.Count == 0)
                    MessageBox.Show(this, "未读取到有效记录。\n" + String.Join("\n", d.Errors), "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        void UpdateStats()
        {
            if (_data == null) return;
            long total = 0, inp = 0, outp = 0, cached = 0, reas = 0;
            HashSet<string> threads = new HashSet<string>();
            foreach (UsageRecord r in _data.Records)
            {
                total += r.Total; inp += r.Input; outp += r.Output; cached += r.Cached; reas += r.Reasoning;
                threads.Add(r.ThreadId);
            }
            _lblTotal.Text = Short(total);
            _lblInput.Text = Short(inp);
            _lblOutput.Text = Short(outp);
            _lblCached.Text = Short(cached);
            _lblReasoning.Text = Short(reas);
            _lblSessions.Text = threads.Count.ToString();
            _lblRange.Text = (_data.Records.Count > 0)
                ? _data.MinTime.ToLocalTime().ToString("yyyy-MM-dd") + " ~ " + _data.MaxTime.ToLocalTime().ToString("yyyy-MM-dd")
                : "-";
        }

        static string Short(long v)
        {
            if (v >= 1000000000L) return (v / 1000000000.0).ToString("0.00") + "B";
            if (v >= 1000000L) return (v / 1000000.0).ToString("0.00") + "M";
            if (v >= 1000L) return (v / 1000.0).ToString("0.0") + "K";
            return v.ToString("N0");
        }

        long Pick(UsageRecord r, int metric)
        {
            switch (metric)
            {
                case 1: return r.Input;
                case 2: return r.Output;
                case 3: return r.Cached;
                case 4: return r.Reasoning;
                default: return r.Total;
            }
        }

        void RefreshChart()
        {
            if (_data == null) return;
            int view = _cboView.SelectedIndex;
            int metric = _cboMetric.SelectedIndex;
            List<ChartItem> items = new List<ChartItem>();
            string title = "";
            string metricName = _cboMetric.Text;

            if (view == 0)
            {
                foreach (DayAgg a in UsageLoader.AggregateByDay(_data))
                {
                    long v = 0;
                    switch (metric) { case 1: v = a.Input; break; case 2: v = a.Output; break; case 3: v = a.Cached; break; case 4: v = a.Reasoning; break; default: v = a.Total; break; }
                    items.Add(new ChartItem(a.Day.ToString("MM-dd"), v));
                }
                title = "按天 · " + metricName + " 用量";
            }
            else
            {
                foreach (ThreadAgg a in UsageLoader.AggregateByThread(_data))
                {
                    long v = 0;
                    switch (metric) { case 1: v = a.Input; break; case 2: v = a.Output; break; case 3: v = a.Cached; break; case 4: v = a.Reasoning; break; default: v = a.Total; break; }
                    items.Add(new ChartItem(a.Name ?? UsageLoader.ShortId(a.ThreadId), v));
                }
                title = "按会话 · " + metricName + " 用量";
            }
            _chart.SetItems(items, title);
        }

        void FillGrid()
        {
            _grid.SuspendLayout();
            _grid.Columns.Clear();
            _grid.Columns.Add("time", "时间");
            _grid.Columns.Add("thread", "会话");
            _grid.Columns.Add("inp", "输入");
            _grid.Columns.Add("cached", "缓存读取");
            _grid.Columns.Add("out", "输出");
            _grid.Columns.Add("reason", "推理");
            _grid.Columns.Add("total", "总量");
            foreach (DataGridViewColumn c in _grid.Columns)
            {
                if (c.Name == "thread") c.FillWeight = 200;
                else if (c.Name == "time") c.FillWeight = 130;
                else c.FillWeight = 60;
            }
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
                    _grid.Rows[i].Cells[0].Value = r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    _grid.Rows[i].Cells[1].Value = UsageLoader.ThreadLabel(_data, r.ThreadId);
                    _grid.Rows[i].Cells[2].Value = r.Input.ToString("N0");
                    _grid.Rows[i].Cells[3].Value = r.Cached.ToString("N0");
                    _grid.Rows[i].Cells[4].Value = r.Output.ToString("N0");
                    _grid.Rows[i].Cells[5].Value = r.Reasoning.ToString("N0");
                    _grid.Rows[i].Cells[6].Value = r.Total.ToString("N0");
                }
            }
            _grid.ResumeLayout();
        }
    }
}
