// AgyCreditOverlay.exe
// Small always-on-top overlay that shows your Antigravity credits / quota in real time.
// Data source: the official Antigravity CLI statusLine JSON feed (saved by agy-statusline.exe).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Antigravity Credit Overlay")]
[assembly: System.Reflection.AssemblyProduct("Antigravity Credit Overlay")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace AgyCreditOverlay
{
    class Bucket
    {
        public string Id;
        public double? Fraction;
        public double? Amount;
        public DateTime? ResetUtc;
        public bool Disabled;
    }

    class QuotaState
    {
        public List<Bucket> Buckets = new List<Bucket>();
        public string PlanTier;
        public string ModelName;
        public string AgentState;
        public DateTime UpdatedUtc;
        public bool HasQuota;
    }

    class Settings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public int OpacityPct = 92;
        public bool AutoHide = true;
        public bool Locked = false;

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgyCreditOverlay"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "overlay.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int n; bool b;
                    if (k == "X" && int.TryParse(v, out n)) s.X = n;
                    else if (k == "Y" && int.TryParse(v, out n)) s.Y = n;
                    else if (k == "Opacity" && int.TryParse(v, out n)) s.OpacityPct = Math.Max(30, Math.Min(100, n));
                    else if (k == "AutoHide" && bool.TryParse(v, out b)) s.AutoHide = b;
                    else if (k == "Locked" && bool.TryParse(v, out b)) s.Locked = b;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    "X=" + X + "\r\nY=" + Y + "\r\nOpacity=" + OpacityPct +
                    "\r\nAutoHide=" + AutoHide + "\r\nLocked=" + Locked + "\r\n");
            }
            catch { }
        }
    }

    class OverlayForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        readonly Settings settings = Settings.Load();
        readonly string statePath = Path.Combine(Settings.Dir, "state.json");
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miShow, miAutoHide, miLock, miStartup;

        QuotaState state;
        QuotaState fileState;                 // from the CLI statusLine feed (while a CLI session is open)
        volatile QuotaState polledState;      // from "agy -p /usage" + "/credits" (works with the CLI closed)
        volatile bool polling;
        DateTime lastPoll = DateTime.MinValue;
        const int PollSeconds = 60;
        DateTime lastFileWrite = DateTime.MinValue;
        DateTime lastProcCheck = DateTime.MinValue;
        bool antigravityRunning, cliRunning, userHidden, dwmRounded;
        float scale = 1f;
        IntPtr trayIconHandle = IntPtr.Zero;
        Color lastTrayColor = Color.Empty;

        static readonly Color Bg = Color.FromArgb(22, 24, 31);
        static readonly Color Border = Color.FromArgb(60, 66, 82);
        static readonly Color TextMain = Color.FromArgb(235, 238, 245);
        static readonly Color TextDim = Color.FromArgb(140, 148, 166);
        static readonly Color Accent = Color.FromArgb(122, 162, 255);
        static readonly Color Track = Color.FromArgb(46, 50, 62);
        static readonly Color Good = Color.FromArgb(72, 199, 142);
        static readonly Color Warn = Color.FromArgb(245, 180, 70);
        static readonly Color Bad = Color.FromArgb(240, 90, 90);

        public OverlayForm()
        {
            Text = "Antigravity Credits";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Opacity = settings.OpacityPct / 100.0;

            using (var g = CreateGraphics()) scale = g.DpiX / 96f;

            BuildMenu();
            ContextMenuStrip = menu;
            tray.ContextMenuStrip = menu;
            tray.Text = "Antigravity Credits";
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleUserHidden();
            };
            UpdateTrayIcon(Accent);

            MouseDown += OnDragMouseDown;
            LocationChanged += delegate { if (Visible && WindowState == FormWindowState.Normal) { settings.X = Left; settings.Y = Top; } };

            var forceHandle = Handle; // create the window now (it starts hidden)
            ReadState(true);
            RelayoutSize();
            PlaceInitially();

            timer.Interval = 1000;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: hide from Alt+Tab
                cp.ExStyle |= 0x08;   // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = 2; // DWMWCP_ROUND (Windows 11)
                dwmRounded = DwmSetWindowAttribute(Handle, 33, ref pref, 4) == 0;
            }
            catch { dwmRounded = false; }
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        // ---------------------------------------------------------------- menu
        void BuildMenu()
        {
            miShow = new ToolStripMenuItem("Hide overlay", null, delegate { ToggleUserHidden(); });
            miAutoHide = new ToolStripMenuItem("Only show while Antigravity is open", null, delegate
            {
                settings.AutoHide = !settings.AutoHide; settings.Save(); SyncMenu(); ApplyVisibility();
            });
            miLock = new ToolStripMenuItem("Lock position", null, delegate
            {
                settings.Locked = !settings.Locked; settings.Save(); SyncMenu();
            });
            var miOpacity = new ToolStripMenuItem("Opacity");
            foreach (int p in new[] { 100, 92, 80, 70, 60, 50 })
            {
                int pct = p;
                miOpacity.DropDownItems.Add(new ToolStripMenuItem(pct + "%", null, delegate
                {
                    settings.OpacityPct = pct; Opacity = pct / 100.0; settings.Save();
                }));
            }
            miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartupEnabled()); SyncMenu(); });
            var miReset = new ToolStripMenuItem("Reset position", null, delegate { settings.X = settings.Y = int.MinValue; PlaceInitially(); settings.Save(); });
            var miFolder = new ToolStripMenuItem("Open data folder", null, delegate
            {
                try { Directory.CreateDirectory(Settings.Dir); Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); } catch { }
            });
            var miCli = new ToolStripMenuItem("Open Antigravity CLI (refresh credits)", null, delegate { LaunchCli(); });
            var miExit = new ToolStripMenuItem("Exit", null, delegate { Close(); });

            menu.Items.AddRange(new ToolStripItem[] {
                miCli, new ToolStripSeparator(),
                miShow, miAutoHide, miLock, miOpacity, miStartup, new ToolStripSeparator(),
                miReset, miFolder, new ToolStripSeparator(), miExit });
            menu.Opening += delegate { SyncMenu(); };
            SyncMenu();
        }

        void SyncMenu()
        {
            miShow.Text = userHidden ? "Show overlay" : "Hide overlay";
            miAutoHide.Checked = settings.AutoHide;
            miLock.Checked = settings.Locked;
            miStartup.Checked = IsStartupEnabled();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "AgyCreditOverlay";

        static bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        void ToggleUserHidden()
        {
            userHidden = !userHidden;
            SyncMenu();
            ApplyVisibility();
        }

        static string FindAgy()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"agy\bin\agy.exe");
            if (File.Exists(local)) return local;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var d in path.Split(';'))
            {
                try { string p = Path.Combine(d.Trim(), "agy.exe"); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        void LaunchCli()
        {
            string agy = FindAgy();
            if (agy == null)
            {
                MessageBox.Show("Antigravity CLI (agy) was not found.\nInstall it from https://antigravity.google/docs/cli",
                    "Antigravity Credits", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/k \"\"" + agy + "\"\"");
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        // ---------------------------------------------------------------- window
        void OnDragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks >= 2) { LaunchCli(); return; }
            if (settings.Locked) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
            settings.X = Left; settings.Y = Top;
            settings.Save();
        }

        void PlaceInitially()
        {
            var pt = new Point(settings.X, settings.Y);
            bool onScreen = false;
            if (settings.X != int.MinValue)
                foreach (var scr in Screen.AllScreens)
                    if (scr.WorkingArea.IntersectsWith(new Rectangle(pt, Size))) { onScreen = true; break; }
            if (!onScreen)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                pt = new Point(wa.Right - Width - S(24), wa.Top + S(24));
            }
            Location = pt;
        }

        void Tick()
        {
            ReadState(false);

            if ((DateTime.UtcNow - lastProcCheck).TotalSeconds >= 2)
            {
                lastProcCheck = DateTime.UtcNow;
                antigravityRunning = IsRunning("Antigravity");
                cliRunning = IsRunning("agy");
                ApplyVisibility();
            }

            if (Visible && !polling && (DateTime.UtcNow - lastPoll).TotalSeconds >= PollSeconds) StartPoll();
            PickState();

            if (Visible)
            {
                RelayoutSize();
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // keep on top, no activate
                Invalidate();
            }
            UpdateTrayText();
        }

        static bool IsRunning(string name)
        {
            try
            {
                var ps = Process.GetProcessesByName(name);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                return any;
            }
            catch { return false; }
        }

        void ApplyVisibility()
        {
            bool want = !userHidden && (!settings.AutoHide || antigravityRunning || cliRunning);
            if (want && !Visible) { Show(); SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
            else if (!want && Visible) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            settings.Save();
            tray.Visible = false;
            tray.Dispose();
            if (trayIconHandle != IntPtr.Zero) DestroyIcon(trayIconHandle);
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- data
        void ReadState(bool force)
        {
            try
            {
                if (!File.Exists(statePath)) { fileState = null; PickState(); return; }
                var wt = File.GetLastWriteTimeUtc(statePath);
                if (!force && wt == lastFileWrite) return;

                string raw;
                using (var fs = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    raw = r.ReadToEnd();

                var parsed = Parse(raw, wt);
                if (parsed != null) { fileState = parsed; lastFileWrite = wt; PickState(); }
            }
            catch { /* file mid-write; retry next tick */ }
        }

        static int OrderOf(string id)
        {
            switch (id)
            {
                case "gemini-5h": return 0;
                case "gemini-weekly": return 1;
                case "3p-5h": return 2;
                case "3p-weekly": return 3;
                case "ai-credits": return 4;
                default: return 10;
            }
        }

        // Combine the two sources: newest one wins, buckets only the other has are added.
        void PickState()
        {
            var a = fileState; var b = polledState;
            if (a == null && b == null) { state = null; return; }
            QuotaState baseSt = a == null ? b : (b == null ? a : (b.UpdatedUtc >= a.UpdatedUtc ? b : a));
            QuotaState other = ReferenceEquals(baseSt, a) ? b : a;
            var merged = new QuotaState
            {
                PlanTier = baseSt.PlanTier ?? (other != null ? other.PlanTier : null),
                ModelName = baseSt.ModelName, AgentState = baseSt.AgentState,
                UpdatedUtc = baseSt.UpdatedUtc, HasQuota = true
            };
            merged.Buckets.AddRange(baseSt.Buckets);
            if (other != null)
                foreach (var ob in other.Buckets)
                    if (!merged.Buckets.Exists(x => x.Id == ob.Id)) merged.Buckets.Add(ob);
            merged.Buckets.Sort((x, y) => OrderOf(x.Id).CompareTo(OrderOf(y.Id)));
            state = merged;
        }

        // ---------------------------------------------------------------- polling the CLI (no session needed)
        void StartPoll()
        {
            if (polling) return;
            string agy = FindAgy();
            if (agy == null) return;
            polling = true;
            lastPoll = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string credits = null;
                    var creditsThread = new Thread(delegate () { try { credits = RunAgy(agy, "/credits", 30000); } catch { } });
                    creditsThread.IsBackground = true;
                    creditsThread.Start();
                    string usage = RunAgy(agy, "/usage", 30000);
                    creditsThread.Join(32000);
                    var st = ParsePolled(usage, credits);
                    if (st != null) polledState = st;
                }
                catch { }
                finally { polling = false; }
            });
        }

        static string RunAgy(string exe, string arg, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, "-p " + arg);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                return outTask.Result;
            }
        }

        static QuotaState ParsePolled(string usage, string credits)
        {
            var st = new QuotaState { UpdatedUtc = DateTime.UtcNow, HasQuota = true };
            if (!string.IsNullOrEmpty(usage))
            {
                foreach (var raw in usage.Split('\n'))
                {
                    var p = raw.TrimEnd('\r').Split('\t');
                    if (p.Length < 3) continue;
                    string pct = p[2].Trim().TrimEnd('%');
                    double v;
                    if (!double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    bool gemini = p[0].StartsWith("Gemini", StringComparison.OrdinalIgnoreCase);
                    bool weekly = p[1].IndexOf("week", StringComparison.OrdinalIgnoreCase) >= 0;
                    var b = new Bucket { Id = (gemini ? "gemini" : "3p") + "-" + (weekly ? "weekly" : "5h"), Fraction = v / 100.0 };
                    DateTime rt;
                    if (p.Length >= 4 && DateTime.TryParse(p[3].Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out rt))
                        b.ResetUtc = rt;
                    st.Buckets.Add(b);
                }
            }
            if (!string.IsNullOrEmpty(credits))
            {
                foreach (var raw in credits.Split('\n'))
                {
                    var p = raw.TrimEnd('\r').Split('\t');
                    double v;
                    if (p.Length >= 2 && p[0].IndexOf("credit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        st.Buckets.Add(new Bucket { Id = "ai-credits", Amount = v });
                }
            }
            return st.Buckets.Count == 0 ? null : st;
        }

        static QuotaState Parse(string raw, DateTime fileUtc)
        {
            var root = new JavaScriptSerializer().DeserializeObject(raw) as IDictionary<string, object>;
            if (root == null) return null;
            var st = new QuotaState { UpdatedUtc = fileUtc };
            st.PlanTier = Str(root, "plan_tier");
            st.AgentState = Str(root, "agent_state");
            object m;
            if (root.TryGetValue("model", out m) && m is IDictionary<string, object>)
                st.ModelName = Str((IDictionary<string, object>)m, "display_name");

            object q;
            if (root.TryGetValue("quota", out q) && q is IDictionary<string, object>)
            {
                st.HasQuota = true;
                foreach (var kv in (IDictionary<string, object>)q)
                {
                    var d = kv.Value as IDictionary<string, object>;
                    if (d == null) continue;
                    var b = new Bucket { Id = kv.Key };
                    b.Fraction = Num(d, "remaining_fraction");
                    b.Amount = Num(d, "remaining_amount");
                    object dis;
                    if (d.TryGetValue("disabled", out dis) && dis is bool) b.Disabled = (bool)dis;
                    string rt = Str(d, "reset_time");
                    DateTime parsedReset;
                    if (rt != null && DateTime.TryParse(rt, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsedReset))
                        b.ResetUtc = parsedReset;
                    else
                    {
                        double? secs = Num(d, "reset_in_seconds");
                        if (secs.HasValue) b.ResetUtc = fileUtc.AddSeconds(secs.Value);
                    }
                    st.Buckets.Add(b);
                }
            }
            return st;
        }

        static string Str(IDictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static double? Num(IDictionary<string, object> d, string k)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        // ---------------------------------------------------------------- formatting
        static string Pretty(string id)
        {
            switch (id)
            {
                case "gemini-5h": return "Gemini \u00B7 5 hour";
                case "gemini-weekly": return "Gemini \u00B7 Weekly";
                case "3p-5h": return "Claude & GPT \u00B7 5 hour";
                case "3p-weekly": return "Claude & GPT \u00B7 Weekly";
                case "ai-credits": return "AI Credits";
            }
            var words = id.Replace('-', ' ').Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return id;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i].ToLowerInvariant();
                if (w == "ai" || w == "api" || w == "g1") words[i] = w.ToUpperInvariant();
                else if (i == 0) words[i] = char.ToUpper(w[0]) + w.Substring(1);
            }
            return string.Join(" ", words);
        }

        static string FormatSpan(TimeSpan t)
        {
            if (t.TotalSeconds <= 0) return "now";
            if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m " + t.Seconds + "s";
            return t.Seconds + "s";
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 5) return "just now";
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + "s ago";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m ago";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h ago";
            return (int)t.TotalDays + "d ago";
        }

        static Color LevelColor(double frac)
        {
            if (frac <= 0.15) return Bad;
            if (frac <= 0.40) return Warn;
            return Good;
        }

        string ValueText(Bucket b)
        {
            if (b.Disabled) return "disabled";
            if (b.Fraction.HasValue) return Math.Round(b.Fraction.Value * 100).ToString(CultureInfo.InvariantCulture) + "%";
            if (b.Amount.HasValue) return b.Amount.Value.ToString("#,0.##", CultureInfo.InvariantCulture) + (b.Id == "ai-credits" ? "" : " left");
            return "—";
        }

        // ---------------------------------------------------------------- layout & paint
        const float W = 268, PAD = 14, HEADER = 22, ROW = 54, FOOTER = 22, MSG = 40;

        void RelayoutSize()
        {
            int rows = (state != null && state.Buckets.Count > 0) ? state.Buckets.Count : 0;
            float h = PAD + HEADER + 6 + (rows > 0 ? rows * ROW : MSG) + FOOTER + PAD - 6;
            var sz = new Size(S(W), S(h));
            if (ClientSize != sz)
            {
                ClientSize = sz;
                if (!dwmRounded)
                    using (var p = RoundRect(new RectangleF(0, 0, sz.Width, sz.Height), S(12)))
                        Region = new Region(p);
            }
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Bg);

            using (var pen = new Pen(Border, 1))
            using (var path = RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(11)))
                g.DrawPath(pen, path);

            using (var fHead = new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fName = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fVal = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fSmall = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var bMain = new SolidBrush(TextMain))
            using (var bDim = new SolidBrush(TextDim))
            using (var bAccent = new SolidBrush(Accent))
            {
                float x = S(PAD), right = Width - S(PAD), y = S(PAD);
                var rightFmt = new StringFormat { Alignment = StringAlignment.Far };

                // header
                g.DrawString("\u26A1 ANTIGRAVITY CREDITS", fHead, bAccent, x, y);
                if (state != null && !string.IsNullOrEmpty(state.PlanTier))
                    g.DrawString(state.PlanTier, fSmall, bDim, new RectangleF(x, y + S(1), right - x, S(HEADER)), rightFmt);
                y += S(HEADER + 6);

                double lowest = 1.0;
                if (state == null || state.Buckets.Count == 0)
                {
                    string msg = state == null
                        ? "Fetching your quota\u2026\n(needs the Antigravity CLI, agy)"
                        : "Connected, but no quota data\nwas reported for this account.";
                    g.DrawString(msg, fName, bDim, new RectangleF(x, y, right - x, S(MSG)));
                    y += S(MSG);
                }
                else
                {
                    foreach (var b in state.Buckets)
                    {
                        double frac = b.Fraction.HasValue ? Math.Max(0, Math.Min(1, b.Fraction.Value)) : (b.Amount.HasValue && b.Amount.Value <= 0 ? 0 : 1);
                        if (!b.Disabled && b.Fraction.HasValue) lowest = Math.Min(lowest, frac);
                        Color c = b.Disabled ? TextDim : LevelColor(frac);

                        g.DrawString(Pretty(b.Id), fName, bMain, x, y + S(4));
                        using (var bc = new SolidBrush(c))
                            g.DrawString(ValueText(b), fVal, bc, new RectangleF(x, y, right - x, S(24)), rightFmt);

                        float by = y + S(27), bh = S(6);
                        if (b.Fraction.HasValue)
                            using (var tb = new SolidBrush(Track))
                            using (var tp = RoundRect(new RectangleF(x, by, right - x, bh), bh / 2))
                                g.FillPath(tb, tp);
                        if (b.Fraction.HasValue && frac > 0.005)
                            using (var fb = new SolidBrush(c))
                            using (var fp = RoundRect(new RectangleF(x, by, Math.Max(bh, (float)((right - x) * frac)), bh), bh / 2))
                                g.FillPath(fb, fp);

                        string reset = b.ResetUtc.HasValue
                            ? "Resets in " + FormatSpan(b.ResetUtc.Value - DateTime.UtcNow) + "  \u00B7  " + b.ResetUtc.Value.ToLocalTime().ToString("ddd HH:mm")
                            : "";
                        g.DrawString(reset, fSmall, bDim, x, by + S(9));
                        y += S(ROW);
                    }
                }

                // footer
                y += S(2);
                bool fresh = state != null && (DateTime.UtcNow - state.UpdatedUtc).TotalMinutes < 3;
                Color dot = state == null ? TextDim : (fresh ? Good : Warn);
                using (var db = new SolidBrush(dot)) g.FillEllipse(db, x, y + S(5), S(7), S(7));
                string foot;
                if (state == null) foot = polling ? "Fetching\u2026" : "Waiting for data";
                else foot = (fresh ? "Live" : "Stale") + " \u00B7 updated " + Ago(DateTime.UtcNow - state.UpdatedUtc);
                g.DrawString(foot, fSmall, bDim, x + S(12), y + S(1));

                if (state != null && state.Buckets.Count > 0)
                {
                    Color tc = LevelColor(lowest);
                    if (tc != lastTrayColor) UpdateTrayIcon(tc);
                }
            }
        }

        // ---------------------------------------------------------------- tray
        void UpdateTrayIcon(Color c)
        {
            lastTrayColor = c;
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 28, 28);
                    using (var f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var tb = new SolidBrush(Color.FromArgb(20, 22, 28)))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("C", f, tb, new RectangleF(0, 1, 32, 32), sf);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (old != null) old.Dispose();
                if (trayIconHandle != IntPtr.Zero) DestroyIcon(trayIconHandle);
                trayIconHandle = h;
            }
        }

        void UpdateTrayText()
        {
            string t = "Antigravity Credits";
            if (state != null && state.Buckets.Count > 0)
            {
                var parts = new List<string>();
                foreach (var b in state.Buckets) parts.Add(Pretty(b.Id) + ": " + ValueText(b));
                t = string.Join("\n", parts.ToArray());
            }
            if (t.Length > 63) t = t.Substring(0, 62) + "\u2026";
            if (tray.Text != t) tray.Text = t;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\AgyCreditOverlay_SingleInstance", out created))
            {
                if (!created) return;
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new OverlayForm();
                form.FormClosed += delegate { Application.ExitThread(); };
                Application.Run();
            }
        }
    }
}
