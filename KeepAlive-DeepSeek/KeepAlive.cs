// Keep-Alive 保活看门狗 v2 —— 多目标监控列表 + 可调窗口 (WinForms, .NET 4.0+, C#5)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeepAliveTool
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool silent = false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "/autostart", StringComparison.OrdinalIgnoreCase)) silent = true;

            bool createdNew;
            Mutex m = new Mutex(true, "Local\\KeepAliveWatchdogMutex", out createdNew);
            if (!createdNew)
            {
                if (!silent) MessageBox.Show("Keep-Alive 已经在运行了喵（看右下角托盘）。", "Keep-Alive",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(silent));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(AppDomain.CurrentDomain.BaseDirectory + "crash.log", ex.ToString()); }
                catch { }
                throw;
            }
            finally { m.ReleaseMutex(); m.Dispose(); }
        }
    }

    // ---------------- 单个监控项 ----------------
    class MonitorItem
    {
        // 配置
        public string Target = "";
        public string Args = "";
        public string WorkDir = "";
        public string ProcName = "";
        public string Pattern = "";
        public int IntervalSec = 5;
        public int MaxCrash = 3;
        public int BackoffSec = 300;
        public bool HideConsole = false;
        public bool StartMinimized = false;
        public bool StartHideWindow = false;
        public bool StartOnBoot = true;   // 开机自启后自动开始检测（默认勾选）
        public volatile bool SuppressAutoHide;   // 用户手动显示窗口后，暂停启动补刀自动隐藏

        // 运行状态（线程写，UI 读）
        public volatile bool Running;
        public volatile bool Alive;
        public volatile int CrashCount;
        public volatile int Restarts;
        public string StatusText = "已停止";
        public string LastEvent = "";
        public Thread ThreadRef;

        public string DisplayName
        {
            get
            {
                try { string n = Path.GetFileName(Target); return string.IsNullOrEmpty(n) ? ProcName : n; }
                catch { return ProcName; }
            }
        }
        public string MatchText
        {
            get
            {
                if (!string.IsNullOrEmpty(Pattern)) return ProcName + " ↳ " + Pattern;
                return ProcName;
            }
        }
    }

    // ---------------- 管理器 ----------------
    class Manager
    {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); // 0=隐藏 6=最小化 9=还原
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        public readonly List<MonitorItem> Items = new List<MonitorItem>();
        private readonly object _lock = new object();
        private readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
        private string _logPath = "";
        public Action<string> UiEvent; // 事件推送到 UI

        public void Init(string logPath) { _logPath = logPath; }

        public void Notify(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            try { if (!string.IsNullOrEmpty(_logPath)) File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
            _events.Enqueue(line);
        }

        public bool DrainEvent(out string line) { return _events.TryDequeue(out line); }

        public void StartItem(MonitorItem it)
        {
            if (it.Running) return;
            it.Running = true;
            it.Restarts = 0;
            it.StatusText = "运行中";
            it.ThreadRef = new Thread(delegate () { Loop(it); }) { IsBackground = true };
            it.ThreadRef.Start();
            Notify("开始监控: " + it.DisplayName);
        }

        public void StopItem(MonitorItem it)
        {
            if (!it.Running) return;
            it.Running = false;
            try { if (it.ThreadRef != null && it.ThreadRef.IsAlive) it.ThreadRef.Join(2000); }
            catch { }
            it.ThreadRef = null;
            it.CrashCount = 0;
            it.Alive = false;
            it.StatusText = "已停止";
            Notify("停止监控: " + it.DisplayName);
        }

        public void StartAll()
        {
            lock (_lock) { foreach (MonitorItem it in Items) if (!it.Running) StartItem(it); }
        }
        public void StartBootItems()
        {
            foreach (MonitorItem it in Items)
                if (it.StartOnBoot && !it.Running) StartItem(it);
        }
        public void StopAll()
        {
            lock (_lock) { foreach (MonitorItem it in Items) if (it.Running) StopItem(it); }
        }

        private void Loop(MonitorItem it)
        {
            while (it.Running)
            {
                try
                {
                    if (IsAlive(it))
                    {
                        it.Alive = true;
                        it.CrashCount = 0;
                        if (!string.IsNullOrEmpty(it.StatusText) && !it.StatusText.StartsWith("运行中")) it.StatusText = "运行中";
                    }
                    else
                    {
                        it.Alive = false;
                        if (it.CrashCount >= it.MaxCrash)
                        {
                            it.StatusText = "风暴退避";
                            Notify("重启风暴（连续 " + it.CrashCount + " 次快速退出），" + it.DisplayName + " 退避 " + it.BackoffSec + " 秒，请人工检查");
                            it.CrashCount = 0;
                            Thread.Sleep(it.BackoffSec * 1000);
                            continue;
                        }
                        it.CrashCount++;
                        it.Restarts++;
                        it.StatusText = "重启中";
                        it.SuppressAutoHide = false;   // 新的一次拉起：恢复按设置自动隐藏/最小化
                        Notify("目标未运行 -> 第 " + it.CrashCount + " 次拉起: " + it.DisplayName + (it.Args.Length > 0 ? " " + it.Args : ""));
                        try { StartTarget(it); }
                        catch (Exception ex) { it.StatusText = "启动失败"; Notify("拉起失败(" + it.DisplayName + "): " + ex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    Notify("看护循环异常(" + it.DisplayName + "): " + ex.Message);
                }
                int total = Math.Max(1, it.IntervalSec) * 1000;
                int waited = 0;
                while (it.Running && waited < total) { Thread.Sleep(200); waited += 200; }
            }
        }

        public static bool IsAlive(MonitorItem it)
        {
            string proc = it.ProcName;
            if (string.IsNullOrEmpty(proc)) { try { proc = Path.GetFileNameWithoutExtension(it.Target); } catch { } }
            if (string.IsNullOrEmpty(proc)) return false;
            if (!string.IsNullOrEmpty(it.Pattern))
            {
                try
                {
                    string esc = it.Pattern.Replace("'", "''");
                    string wql = "SELECT ProcessId FROM Win32_Process WHERE Name='" + proc + ".exe' AND CommandLine LIKE '%" + esc + "%'";
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(wql))
                    using (ManagementObjectCollection c = s.Get())
                        foreach (ManagementObject o in c) return true;
                    return false;
                }
                catch { }
            }
            try { return Process.GetProcessesByName(proc).Length > 0; }
            catch { return false; }
        }

        public static void StartTarget(MonitorItem it)
        {
            ProcessStartInfo psi = new ProcessStartInfo { FileName = it.Target };
            if (!string.IsNullOrEmpty(it.Args)) psi.Arguments = it.Args;
            if (!string.IsNullOrEmpty(it.WorkDir)) psi.WorkingDirectory = it.WorkDir;
            if (it.HideConsole || it.StartHideWindow) psi.WindowStyle = ProcessWindowStyle.Hidden;
            else if (it.StartMinimized) psi.WindowStyle = ProcessWindowStyle.Minimized;
            psi.UseShellExecute = true;
            Process p = Process.Start(psi);

            // 事后“补刀”：部分程序会无视启动参数自开窗口，这里在 ~12 秒内反复隐藏/最小化它的主窗口
            bool needPost = (it.StartHideWindow || it.StartMinimized) && p != null;
            if (!needPost) return;
            try
            {
                int applied = 0;
                for (int i = 0; i < 60 && applied < 4; i++)   // 200ms x 60 ≈ 12s
                {
                    if (it.SuppressAutoHide) break;          // 用户已手动显示 → 不再压
                    p.Refresh();
                    if (p.HasExited) break;
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        if (it.StartHideWindow) ShowWindow(hwnd, 0);        // SW_HIDE
                        else if (it.StartMinimized) ShowWindow(hwnd, 6);    // SW_MINIMIZE
                        applied++;
                        if (applied < 4) Thread.Sleep(500);                 // 给程序重绘/重建窗口的机会后再补一次
                    }
                    Thread.Sleep(200);
                }
            }
            catch { }
        }

        // 取目标进程的 PID 列表（支持按命令行特征匹配）
        public static List<int> GetPids(MonitorItem it)
        {
            List<int> list = new List<int>();
            try
            {
                string proc = it.ProcName;
                if (string.IsNullOrEmpty(proc)) proc = Path.GetFileNameWithoutExtension(it.Target);
                if (string.IsNullOrEmpty(proc)) return list;
                if (!string.IsNullOrEmpty(it.Pattern))
                {
                    string esc = it.Pattern.Replace("'", "''");
                    string wql = "SELECT ProcessId FROM Win32_Process WHERE Name='" + proc + ".exe' AND CommandLine LIKE '%" + esc + "%'";
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(wql))
                    using (ManagementObjectCollection c = s.Get())
                        foreach (ManagementObject o in c)
                        {
                            object v = o["ProcessId"];
                            if (v != null) { int id; if (int.TryParse(v.ToString(), out id)) list.Add(id); }
                        }
                }
                else
                {
                    foreach (Process p in Process.GetProcessesByName(proc)) list.Add(p.Id);
                }
            }
            catch { }
            return list;
        }

        private static IntPtr FindTopWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            IntPtr visible = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == (uint)pid)
                {
                    if (found == IntPtr.Zero) found = h;
                    if (IsWindowVisible(h) && visible == IntPtr.Zero) visible = h;
                }
                return true;
            }, IntPtr.Zero);
            return visible != IntPtr.Zero ? visible : found;
        }

        public bool RestoreTarget(MonitorItem it)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = -1;
            foreach (int pid in GetPids(it))
            {
                EnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    uint p;
                    GetWindowThreadProcessId(h, out p);
                    if (p != (uint)pid) return true;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                    GetWindowText(h, sb, 256);
                    if (sb.Length == 0) return true;          // 只要带标题的窗口
                    RECT rc;
                    if (!GetWindowRect(h, out rc)) return true;
                    int area = (rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                    return true;
                }, IntPtr.Zero);
            }
            if (best == IntPtr.Zero) return false;
            it.SuppressAutoHide = true;   // 用户手动显示 → 暂停自动隐藏，避免“显示后又压回去”
            ShowWindow(best, 9);          // SW_RESTORE（含取消隐藏）
            SetForegroundWindow(best);
            return true;
        }
        public bool HideTargetNow(MonitorItem it)
        {
            it.SuppressAutoHide = false;   // 主动再隐藏 → 恢复自动隐藏
            foreach (int pid in GetPids(it))
            {
                IntPtr h = FindTopWindow(pid);
                if (h == IntPtr.Zero) continue;
                ShowWindow(h, 0);          // SW_HIDE
                return true;
            }
            return false;
        }
        public bool KillTarget(MonitorItem it)
        {
            List<int> pids = GetPids(it);
            if (pids.Count == 0) return false;
            foreach (int id in pids)
            {
                try { Process.GetProcessById(id).Kill(); }
                catch { }
            }
            Notify("已结束目标进程: " + it.DisplayName);
            return true;
        }
    }

    // ---------------- 主窗体 ----------------
    class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(30, 32, 38);
        private static readonly Color BorderC = Color.FromArgb(66, 71, 82);
        private static readonly Color TextMain = Color.FromArgb(232, 234, 238);
        private static readonly Color TextSub = Color.FromArgb(152, 158, 170);
        private static readonly Color OkGreen = Color.FromArgb(80, 200, 120);
        private static readonly Color BadRed = Color.FromArgb(235, 90, 80);
        private static readonly Color InBox = Color.FromArgb(24, 26, 31);
        private static readonly Color HoverBtn = Color.FromArgb(54, 58, 68);
        private static readonly Color DownBtn = Color.FromArgb(36, 40, 48);
        private static readonly Color DimText = Color.FromArgb(120, 126, 138);   // 贴近背景的低调文字
        private static readonly Color HoverDim = Color.FromArgb(176, 182, 194);
        private static readonly Color NearBg = Color.FromArgb(42, 44, 50);        // ≈#1E2026 背景同色、再淡1度
        private static readonly Color NearBgHot = Color.FromArgb(172, 178, 190);  // 悬停时显现

        private readonly ConfigStore _store = new ConfigStore();
        private readonly Manager _mgr = new Manager();
        private readonly string _cfgPath;
        private readonly string _logPath;

        // top
        private CheckBox _chkAuto, _chkTray;
        private Label _lblNeko;
        private Panel _topPnl;
        private bool _neko;
        // list
        private DataGridView _grid;
        private SplitContainer _split;
        private const int C_HIDE = 0;   // 列：隐藏到托盘（真勾选框）
        private const int C_MIN = 1;    // 列：最小化（真勾选框）
        private const int C_BOOT = 2;   // 列：开机自动检测（真勾选框）
        // edit pane
        private TextBox _tTarget, _tArgs, _tWork, _tProc, _tPattern;
        private NumericUpDown _nInterval, _nMaxCrash, _nBackoff;
        private CheckBox _chkMin, _chkBoot, _chkHideWin;
        // bottom
        private TextBox _tLog;
        private Label _lblStatus;
        private FlowLayoutPanel _flp;
        private Panel _botPnl;
        private Button _btnDelLog;
        private NotifyIcon _tray;
        private Icon _appIcon;
        private readonly Dictionary<MonitorItem, NotifyIcon> _itemTrays = new Dictionary<MonitorItem, NotifyIcon>();

        private static Icon LoadIconResource()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("icon.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (System.IO.Stream s = asm.GetManifestResourceStream(n))
                            if (s != null) return new Icon(s);
                    }
                }
            }
            catch { }
            return null;
        }
        private System.Windows.Forms.Timer _timer;
        private bool _loadingEdit;
        private bool _silent;
        private MonitorItem _sel;
        private Panel _dot;

        public MainForm(bool silent)
        {
            _silent = silent;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            _cfgPath = Path.Combine(dir, "keep-alive.cfg");
            _logPath = Path.Combine(dir, "keep-alive.log");
            _mgr.Init(_logPath);
            _store.Load(_cfgPath);
            foreach (MonitorItem it in _store.Items) _mgr.Items.Add(it);
            _neko = _store.NekoMode;

            _appIcon = LoadIconResource();
            if (_appIcon != null) Icon = _appIcon;

            Text = "Keep-Alive 保活看门狗";
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(1080, 700);
            MinimumSize = new Size(940, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextMain;

            // 三行固定布局：132(顶) / 弹性(中) / 172(底)
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Bg,
                ColumnCount = 1,
                RowCount = 3
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 172f));
            Controls.Add(root);

            BuildTop(root);
            BuildBottom(root);
            BuildCenter(root);
            BuildTray();
            RefreshTitle();

            if (_silent)
            {
                _chkTray.Checked = true;
                ShowInTaskbar = false;
                Hide();
                SaveAll();
                _mgr.StartBootItems();
                int boot = 0;
                foreach (MonitorItem it in _mgr.Items) if (it.StartOnBoot) boot++;
                _tray.ShowBalloonTip(1800, "Keep-Alive", Neko("看护已后台启动（自动检测 " + boot + " 项）"), ToolTipIcon.Info);
            }
            else
            {
                RefreshList();
                if (_mgr.Items.Count > 0) SelectItem(_mgr.Items[0]);
            }
        }

        // ---------------- 顶部 ----------------
        private void BuildTop(TableLayoutPanel root)
        {
            _topPnl = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            root.Controls.Add(_topPnl, 0, 0);
            Panel top = _topPnl;

            Label t = new Label { Text = "Keep-Alive · 进程保活看门狗", Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold), ForeColor = TextMain, Location = new Point(18, 10), AutoSize = true };
            top.Controls.Add(t);
            Label s = new Label { Text = "监控列表：进程退出自动拉起 · 防重启风暴 · 日志留痕 · 可随开机隐藏运行", ForeColor = TextSub, Location = new Point(20, 38), AutoSize = true };
            top.Controls.Add(s);

            _chkAuto = new CheckBox { Text = "开机自启", ForeColor = TextMain, Location = new Point(22, 60), AutoSize = true, Checked = _store.AutoStart };
            _chkTray = new CheckBox { Text = "隐藏运行开关（勾选：最小化/关闭时收进托盘）", ForeColor = TextMain, Location = new Point(400, 60), AutoSize = true, Checked = _store.StartHidden };
            top.Controls.Add(_chkAuto); top.Controls.Add(_chkTray);

            int[] xs = { 22, 146, 270, 394, 518, 642, 772 };
            string[] ts = { "+ 添加监控", "删除选中", "▶ 启动全部", "■ 停止全部", "保存全部", "打开日志", "— 隐藏到托盘" };
            Button[] bs = new Button[7];
            for (int i = 0; i < 7; i++)
            {
                Button b = new Button { Text = ts[i], Location = new Point(xs[i], 100), Width = 116, Height = 30, FlatStyle = FlatStyle.Flat,
                    ForeColor = TextMain, FlatAppearance = { BorderColor = BorderC, BorderSize = 1, MouseOverBackColor = HoverBtn, MouseDownBackColor = DownBtn } };
                top.Controls.Add(b);
                bs[i] = b;
            }
            bs[0].Click += delegate { AddItemFromEdit(); };
            bs[1].Click += delegate { DeleteSelected(); };
            bs[2].Click += delegate { _mgr.StartAll(); SaveAll(); RefreshList(); };
            bs[3].Click += delegate { _mgr.StopAll(); RefreshList(); };
            bs[4].Click += delegate { SaveAll(); _mgr.Notify("设置已保存"); };
            bs[5].Click += delegate { OpenLog(); };
            bs[6].Click += delegate { HideToTray(); };

            // 猫娘模式开关：极低调小字，点击切换
            _lblNeko = new Label
            {
                Text = NekoStateText(),
                ForeColor = NearBg,
                Font = new Font("Microsoft YaHei UI", 8f),
                AutoSize = true,
                Cursor = Cursors.Hand,
                Location = new Point(0, 14)
            };
            _lblNeko.MouseEnter += delegate { _lblNeko.ForeColor = NearBgHot; };
            _lblNeko.MouseLeave += delegate { _lblNeko.ForeColor = NearBg; };
            _lblNeko.Click += delegate { ToggleNeko(); };
            top.Controls.Add(_lblNeko);
            top.Resize += delegate { PlaceNeko(); };
            top.Layout += delegate { PlaceNeko(); };
        }

        private string NekoStateText()
        {
            return _neko ? "猫娘·开 (=^･ω･^=)" : "猫娘·关";
        }
        private void PlaceNeko()
        {
            if (_lblNeko == null || _topPnl == null) return;
            _lblNeko.Left = Math.Max(0, _topPnl.ClientSize.Width - _lblNeko.Width - 22);
        }
        private void ToggleNeko()
        {
            _neko = !_neko;
            _lblNeko.Text = NekoStateText();
            PlaceNeko();
            RefreshTitle();
            _store.NekoMode = _neko;
            _store.Save(_cfgPath);
            if (_tray != null)
                _tray.ShowBalloonTip(1000, "Keep-Alive", _neko ? "猫娘模式已开启喵~" : "猫娘模式已关闭。", ToolTipIcon.Info);
        }
        private void RefreshTitle()
        {
            Text = "Keep-Alive 保活看门狗" + (_neko ? " · 猫娘模式" : "");
        }
        private string Neko(string s)
        {
            return _neko ? s + " (=^･ω･^=)" : s;
        }

        // ---------------- 中部：列表 + 编辑 ----------------
        private void BuildCenter(TableLayoutPanel root)
        {
            _split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Bg };
            _split.SplitterWidth = 5;
            root.Controls.Add(_split, 0, 1);

            // 左：监控表格 —— 前两列都是系统真勾选框（同款代码，居中），外观调成与整体深色一致
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = InBox,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(50, 54, 64),
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 28;
            _grid.RowTemplate.Height = 28;
            DataGridViewCellStyle headStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(45, 48, 56),
                ForeColor = TextMain,
                SelectionBackColor = Color.FromArgb(45, 48, 56),
                SelectionForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 9f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 0, 0)
            };
            DataGridViewCellStyle cellStyle = new DataGridViewCellStyle
            {
                BackColor = InBox,
                ForeColor = TextMain,
                SelectionBackColor = Color.FromArgb(58, 68, 92),
                SelectionForeColor = TextMain,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            DataGridViewCellStyle chkStyle = (DataGridViewCellStyle)cellStyle.Clone();
            chkStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.ColumnHeadersDefaultCellStyle = headStyle;
            _grid.DefaultCellStyle = cellStyle;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "隐藏到托盘", Width = 96, ThreeState = false, DefaultCellStyle = chkStyle });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "最小化", Width = 60, ThreeState = false, DefaultCellStyle = chkStyle });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "开机检测", Width = 76, ThreeState = false, DefaultCellStyle = chkStyle });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 74, DefaultCellStyle = cellStyle });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "程序", Width = 180, DefaultCellStyle = cellStyle });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "进程匹配", Width = 140, DefaultCellStyle = cellStyle });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "重启", Width = 50, DefaultCellStyle = cellStyle });
            DataGridViewTextBoxColumn lastCol = new DataGridViewTextBoxColumn { HeaderText = "最后事件", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = cellStyle };
            _grid.Columns.Add(lastCol);

            _grid.SelectionChanged += delegate { EditLoadSelected(); };
            _grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == C_HIDE || e.ColumnIndex == C_MIN || e.ColumnIndex == C_BOOT) return;
                ToggleSelected();
            };
            _grid.CellClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                MonitorItem it = _grid.Rows[e.RowIndex].Tag as MonitorItem;
                if (it == null) return;
                if (e.ColumnIndex == C_HIDE) ToggleItemHide(it, e.RowIndex);
                else if (e.ColumnIndex == C_MIN) ToggleItemMin(it, e.RowIndex);
                else if (e.ColumnIndex == C_BOOT) ToggleItemBoot(it, e.RowIndex);
            };
            // 勾选框列：自绘，带左偏移（隐藏=一字12px 左移，最小化=半字6px 左移）
            _grid.CellPainting += delegate(object s, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex != C_HIDE && e.ColumnIndex != C_MIN) return;
                e.Handled = true;
                MonitorItem it = _grid.Rows[e.RowIndex].Tag as MonitorItem;
                if (it == null) return;
                DataGridViewCell cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                Color bg = cell.Selected ? Color.FromArgb(58, 68, 92) : InBox;
                using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.CellBounds);
                bool chk = e.ColumnIndex == C_HIDE ? it.StartHideWindow : it.StartMinimized;
                int shift = e.ColumnIndex == C_HIDE ? 12 : 6;   // 向左偏移
                const int box = 13;
                int cx = e.CellBounds.Left + e.CellBounds.Width / 2 - shift;
                int cy = e.CellBounds.Top + e.CellBounds.Height / 2;
                Rectangle cb = new Rectangle(cx - box / 2, cy - box / 2, box, box);
                System.Windows.Forms.VisualStyles.CheckBoxState st = chk
                    ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
                    : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
                System.Windows.Forms.CheckBoxRenderer.DrawCheckBox(e.Graphics, cb.Location, st);
            };
            // 右键菜单
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("▶ 启动该项", null, delegate { ToggleSelected(); });
            cm.Items.Add("■ 停止该项", null, delegate { ToggleSelected(); });
            cm.Items.Add("结束目标进程", null, delegate { EndTargetProcess(_sel); });
            ToolStripItem miMin = cm.Items.Add("最小化窗口（勾选切换）", null, delegate { ToggleRowMin(); });
            ToolStripItem miBoot = cm.Items.Add("开机自启自动检测（勾选切换）", null, delegate { ToggleRowBoot(); });
            ToolStripItem miHide = cm.Items.Add("隐藏到托盘运行（勾选切换）", null, delegate { ToggleRowHide(); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("删除该项", null, delegate { DeleteSelected(); });
            cm.Opening += delegate
            {
                if (miHide is ToolStripMenuItem)
                    ((ToolStripMenuItem)miHide).Checked = _sel != null && _sel.StartHideWindow;
                if (miMin is ToolStripMenuItem)
                    ((ToolStripMenuItem)miMin).Checked = _sel != null && _sel.StartMinimized;
                if (miBoot is ToolStripMenuItem)
                    ((ToolStripMenuItem)miBoot).Checked = _sel != null && _sel.StartOnBoot;
            };
            _grid.ContextMenuStrip = cm;
            _split.Panel1.Controls.Add(_grid);

            // 右：编辑面板 —— FlowLayoutPanel TopDown + AutoScroll（可靠的纵向滚动）
            FlowLayoutPanel flp = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(35, 37, 44),
                Padding = new Padding(8, 6, 8, 10)
            };
            _flp = flp;
            _split.Panel2.Controls.Add(flp);

            // 标题行
            Panel hr = NewRow(flp, 34);
            Label hh = new Label { Text = "监控项设置（选中列表行后编辑）", Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold), ForeColor = TextMain, Location = new Point(4, 8), AutoSize = true };
            hr.Controls.Add(hh);

            // 目标程序路径
            Panel r1 = NewRow(flp, 60);
            RowCap(r1, "目标程序路径");
            _tTarget = RowBox(r1, 4, 24, 172);
            _tTarget.TextChanged += delegate { if (string.IsNullOrEmpty(_tProc.Text)) _tProc.Text = SafeProcName(_tTarget.Text); };
            Button bt = RowBtn(r1, "浏览...", 176, 24); bt.Click += delegate { BrowseTarget(); };

            // 启动参数
            Panel r2 = NewRow(flp, 60);
            RowCap(r2, "启动参数");
            _tArgs = RowBox(r2, 4, 24, 274);

            // 工作目录
            Panel r3 = NewRow(flp, 60);
            RowCap(r3, "工作目录");
            _tWork = RowBox(r3, 4, 24, 172);
            Button bw = RowBtn(r3, "选择...", 176, 24); bw.Click += delegate { BrowseDir(); };

            // 进程名
            Panel r4 = NewRow(flp, 60);
            RowCap(r4, "进程名（空则自动取程序文件名）");
            _tProc = RowBox(r4, 4, 24, 274);

            // 命令行特征
            Panel r5 = NewRow(flp, 60);
            RowCap(r5, "命令行特征（可空，多实例同名区分）");
            _tPattern = RowBox(r5, 4, 24, 274);

            // 三个数字
            Panel r6 = NewRow(flp, 60);
            RowCap(r6, "检测间隔(秒)");
            _nInterval = RowNum(r6, 4, 24, 90, 2, 3600, 5);
            Panel r7 = NewRow(flp, 60);
            RowCap(r7, "连续崩溃上限（防风暴）");
            _nMaxCrash = RowNum(r7, 4, 24, 90, 1, 99, 3);
            Panel r8 = NewRow(flp, 60);
            RowCap(r8, "风暴退避(秒)");
            _nBackoff = RowNum(r8, 4, 24, 120, 5, 86400, 300);

            // 启动方式
            Panel r9 = NewRow(flp, 34);
            _chkMin = new CheckBox { Text = "最小化运行", ForeColor = TextMain, Location = new Point(4, 7), AutoSize = true };
            r9.Controls.Add(_chkMin);

            Panel r9b = NewRow(flp, 34);
            _chkBoot = new CheckBox { Text = "开机自启后自动开始检测", ForeColor = TextMain, Location = new Point(4, 7), AutoSize = true, Checked = true };
            r9b.Controls.Add(_chkBoot);

            Panel r10 = NewRow(flp, 34);
            _chkHideWin = new CheckBox { Text = "隐藏到托盘运行", ForeColor = TextMain, Location = new Point(4, 7), AutoSize = true };
            r10.Controls.Add(_chkHideWin);

            // 提示
            Panel r11 = NewRow(flp, 30);
            Label tip2 = new Label { Text = "提示：首列勾选 = 隐藏到托盘运行；双击行 = 启停", ForeColor = TextSub, Location = new Point(4, 6), AutoSize = true };
            r11.Controls.Add(tip2);
        }

        private static Panel NewRow(FlowLayoutPanel f, int h)
        {
            Panel r = new Panel { Width = 282, Height = h, BackColor = Color.Transparent };
            f.Controls.Add(r);
            return r;
        }
        private static void RowCap(Panel r, string cap)
        {
            Label l = new Label { Text = cap, ForeColor = TextSub, Location = new Point(4, 3), AutoSize = true };
            r.Controls.Add(l);
        }
        private static TextBox RowBox(Panel r, int x, int y, int w)
        {
            TextBox b = new TextBox { Location = new Point(x, y), Width = w, Height = 26, BackColor = InBox, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
            r.Controls.Add(b);
            return b;
        }
        private static NumericUpDown RowNum(Panel r, int x, int y, int w, decimal min, decimal max, decimal val)
        {
            NumericUpDown n = new NumericUpDown { Location = new Point(x, y), Width = w, Height = 26, Minimum = min, Maximum = max, Value = val, BackColor = InBox, ForeColor = TextMain };
            r.Controls.Add(n);
            return n;
        }
        private static Button RowBtn(Panel r, string t, int x, int y)
        {
            RowMiniBtn b = new RowMiniBtn
            {
                Text = t,
                Location = new Point(x, y),
                Width = 102,
                Height = 23
            };
            r.Controls.Add(b);
            return b;
        }

        // 右栏小按钮（深色扁平自绘）：完整处理 常态/悬浮/按下/离开，文字本体上移4px
        private sealed class RowMiniBtn : Button
        {
            private bool _hover;
            private bool _down;
            public RowMiniBtn()
            {
                FlatStyle = FlatStyle.Flat;
                BackColor = Color.FromArgb(46, 50, 58);
                ForeColor = TextMain;
                Font = new Font("Microsoft YaHei UI", 9f);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs pe)
            {
                Color bg = _down ? DownBtn : (_hover ? HoverBtn : BackColor);
                using (SolidBrush b = new SolidBrush(bg))
                    pe.Graphics.FillRectangle(b, 0, 0, Width, Height);
                using (Pen p = new Pen(BorderC))
                    pe.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                TextFormatFlags ff = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                Size sz = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), ff);
                int x = Math.Max(1, (Width - sz.Width) / 2);
                int y = (Height - sz.Height) / 2;   // 文字垂直居中（0px偏移）
                if (y < 0) y = 0;
                TextRenderer.DrawText(pe.Graphics, Text, Font, new Point(x, y), ForeColor, ff);
            }
        }

        // ---------------- 底部状态+日志 ----------------
        private void BuildBottom(TableLayoutPanel root)
        {
            Panel bot = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            _botPnl = bot;
            root.Controls.Add(bot, 0, 2);

            _dot = new Panel { Size = new Size(12, 12), BackColor = TextSub, Location = new Point(18, 12) };
            bot.Controls.Add(_dot);
            _lblStatus = new Label { Text = "共 0 个监控项", ForeColor = TextMain, Location = new Point(38, 7), AutoSize = true };
            bot.Controls.Add(_lblStatus);

            _tLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 34), Size = new Size(930, 126), BackColor = InBox, ForeColor = TextSub,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom };
            bot.Controls.Add(_tLog);
            _tLog.Resize += delegate { _tLog.Width = Math.Max(200, bot.ClientSize.Width - 32); _tLog.Height = Math.Max(60, bot.ClientSize.Height - 46); };

            // 删除日志（移到回收站）
            Button btnDelLog = new Button
            {
                Text = "删除日志",
                Width = 100,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                ForeColor = TextMain,
                FlatAppearance = { BorderColor = BorderC, BorderSize = 1, MouseOverBackColor = HoverBtn, MouseDownBackColor = DownBtn }
            };
            _btnDelLog = btnDelLog;
            btnDelLog.Click += delegate { DeleteLog(); };
            bot.Controls.Add(btnDelLog);
            bot.Resize += delegate { PlaceLogBtn(); };
            bot.Layout += delegate { PlaceLogBtn(); };
        }

        private void PlaceLogBtn()
        {
            if (_btnDelLog == null || _botPnl == null) return;
            _btnDelLog.Left = Math.Max(150, _botPnl.ClientSize.Width - _btnDelLog.Width - 16);
            _btnDelLog.Top = 7;
        }

        private void DeleteLog()
        {
            try
            {
                if (!File.Exists(_logPath))
                {
                    _mgr.Notify("还没有日志文件可删");
                    return;
                }
                if (MessageBox.Show(this, "确定把日志文件移到回收站？", "Keep-Alive",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(_logPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _tLog.Clear();
                _mgr.Notify("日志已删除（已移到回收站）");
            }
            catch (Exception ex)
            {
                _mgr.Notify("删除日志失败: " + ex.Message);
            }
        }

        // ---------------- 托盘 ----------------
        private void BuildTray()
        {
            _tray = new NotifyIcon { Text = "Keep-Alive 保活看门狗", Icon = _appIcon != null ? _appIcon : MakeIcon(), Visible = true };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowUi(); });
            menu.Items.Add("隐藏到托盘", null, delegate { HideToTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("启动全部", null, delegate { _mgr.StartAll(); RefreshList(); });
            menu.Items.Add("停止全部", null, delegate { _mgr.StopAll(); RefreshList(); });
            menu.Items.Add("打开日志", null, delegate { OpenLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowUi(); };
        }
        private void ShowUi()
        {
            _allowShow = true;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _tray.ShowBalloonTip(1000, "Keep-Alive", Neko("已隐藏到托盘（看护继续运行）"), ToolTipIcon.Info);
        }
        private bool _forceExit;
        private bool _allowShow;   // 静默模式：仅当用户从托盘要求显示时才允许窗口可见

        protected override void SetVisibleCore(bool value)
        {
            if (_silent && !_allowShow) value = false;   // 开机自启/静默启动：强制隐藏主窗口
            base.SetVisibleCore(value);
        }
        private void ExitApp()
        {
            _forceExit = true;
            _mgr.StopAll();
            _tray.Visible = false;
            Application.Exit();
        }
        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(61, 122, 216))) g.FillEllipse(b, 1, 1, 14, 14);
                    using (SolidBrush w = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(w, 4, 4, 2.5f, 2.5f);
                        g.FillPie(w, 4, 10, 8, 4, 180, 180);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return Icon.FromHandle(h); }
                catch { return SystemIcons.Application; }
            }
        }

        // ---------------- 动作 ----------------
        private void BrowseTarget()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "选择要保活的程序";
                d.Filter = "程序|*.exe;*.com;*.bat;*.cmd;*.ps1|所有文件|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _tTarget.Text = d.FileName;
                    if (string.IsNullOrEmpty(_tWork.Text)) _tWork.Text = Path.GetDirectoryName(d.FileName);
                }
            }
        }
        private void BrowseDir()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
                if (d.ShowDialog(this) == DialogResult.OK) _tWork.Text = d.SelectedPath;
        }
        private void OpenLog()
        {
            try { Process.Start("notepad.exe", "\"" + _logPath + "\""); }
            catch (Exception ex) { _mgr.Notify("打开日志失败: " + ex.Message); }
            RefreshLogTail();
        }

        private MonitorItem ReadEditFields()
        {
            MonitorItem it = new MonitorItem();
            it.Target = _tTarget.Text.Trim();
            it.Args = _tArgs.Text.Trim();
            it.WorkDir = _tWork.Text.Trim();
            it.ProcName = _tProc.Text.Trim();
            it.Pattern = _tPattern.Text.Trim();
            it.IntervalSec = (int)_nInterval.Value;
            it.MaxCrash = (int)_nMaxCrash.Value;
            it.BackoffSec = (int)_nBackoff.Value;
            it.StartMinimized = _chkMin.Checked;
            it.StartHideWindow = _chkHideWin.Checked;
            it.StartOnBoot = _chkBoot.Checked;
            if (string.IsNullOrEmpty(it.ProcName)) it.ProcName = SafeProcName(it.Target);
            return it;
        }
        private void WriteEditFields(MonitorItem it)
        {
            _loadingEdit = true;
            _tTarget.Text = it.Target;
            _tArgs.Text = it.Args;
            _tWork.Text = it.WorkDir;
            _tProc.Text = it.ProcName;
            _tPattern.Text = it.Pattern;
            _nInterval.Value = Math.Max(_nInterval.Minimum, Math.Min(_nInterval.Maximum, it.IntervalSec));
            _nMaxCrash.Value = Math.Max(_nMaxCrash.Minimum, Math.Min(_nMaxCrash.Maximum, it.MaxCrash));
            _nBackoff.Value = Math.Max(_nBackoff.Minimum, Math.Min(_nBackoff.Maximum, it.BackoffSec));
            _chkMin.Checked = it.StartMinimized;
            _chkBoot.Checked = it.StartOnBoot;
            _chkHideWin.Checked = it.StartHideWindow;
            _loadingEdit = false;
        }
        private static string SafeProcName(string target)
        {
            try { if (string.IsNullOrEmpty(target)) return ""; return Path.GetFileNameWithoutExtension(target) ?? ""; }
            catch { return ""; }
        }

        private void AddItemFromEdit()
        {
            if (string.IsNullOrEmpty(_tTarget.Text.Trim()))
            {
                MessageBox.Show(this, "请先填目标程序路径喵", "Keep-Alive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MonitorItem it = ReadEditFields();
            // 已在列表则视为更新该行配置
            if (_sel != null && !_sel.Running && string.Equals(_sel.Target, it.Target, StringComparison.OrdinalIgnoreCase))
            {
                CopyTo(_sel, it);
                _mgr.Notify("已更新监控项: " + _sel.DisplayName);
            }
            else
            {
                _mgr.Items.Add(it);
                _sel = it;
                _mgr.Notify("已添加监控项: " + it.DisplayName);
            }
            SaveAll();
            RefreshList();
            SelectItem(_sel);
        }
        private static void CopyTo(MonitorItem dst, MonitorItem src)
        {
            dst.Target = src.Target; dst.Args = src.Args; dst.WorkDir = src.WorkDir;
            dst.ProcName = src.ProcName; dst.Pattern = src.Pattern;
            dst.IntervalSec = src.IntervalSec; dst.MaxCrash = src.MaxCrash;
            dst.BackoffSec = src.BackoffSec; dst.HideConsole = src.HideConsole;
            dst.StartMinimized = src.StartMinimized;
            dst.StartHideWindow = src.StartHideWindow;
            dst.StartOnBoot = src.StartOnBoot;
        }
        private void DeleteSelected()
        {
            if (_sel == null) return;
            _mgr.StopItem(_sel);
            _mgr.Items.Remove(_sel);
            _mgr.Notify("已删除监控项: " + _sel.DisplayName);
            _sel = null;
            SaveAll();
            RefreshList();
            EditClear();
        }
        private void ToggleSelected()
        {
            if (_sel == null) return;
            if (_sel.Running) { _mgr.StopItem(_sel); SaveAll(); }
            else { _mgr.StartItem(_sel); SaveAll(); }
            RefreshList();
        }
        private void ToggleRowMin()
        {
            if (_sel == null) return;
            _sel.StartMinimized = !_sel.StartMinimized;
            WriteEditFields(_sel);
            SaveAll();
            RefreshList();
            _mgr.Notify((_sel.StartMinimized ? "已开启" : "已关闭") + "最小化运行: " + _sel.DisplayName + "（下次目标被拉起时生效）");
        }
        private void ToggleRowBoot()
        {
            if (_sel == null) return;
            _sel.StartOnBoot = !_sel.StartOnBoot;
            WriteEditFields(_sel);
            SaveAll();
            RefreshList();
            _mgr.Notify((_sel.StartOnBoot ? "已开启" : "已关闭") + "开机自启自动检测: " + _sel.DisplayName);
        }
        private void ToggleRowHide()
        {
            if (_sel == null) return;
            _sel.StartHideWindow = !_sel.StartHideWindow;
            WriteEditFields(_sel);
            SaveAll();
            RefreshList();
            _mgr.Notify((_sel.StartHideWindow ? "已开启" : "已关闭") + "隐藏运行: " + _sel.DisplayName + "（下次目标被拉起时生效）");
        }

        private void EditLoadSelected()
        {
            if (_loadingEdit) return;
            if (_grid.CurrentRow != null && _grid.CurrentRow.Tag is MonitorItem)
                SelectItem((MonitorItem)_grid.CurrentRow.Tag);
        }
        private void SelectItem(MonitorItem it)
        {
            _sel = it;
            if (it != null) WriteEditFields(it);
        }
        private void EditClear()
        {
            _loadingEdit = true;
            _tTarget.Text = ""; _tArgs.Text = ""; _tWork.Text = ""; _tProc.Text = ""; _tPattern.Text = "";
            _chkMin.Checked = false;
            _chkBoot.Checked = true;
            _chkHideWin.Checked = false;
            _loadingEdit = false;
        }

        // ---------------- 列表刷新 ----------------
        private void RefreshList()
        {
            if (_grid == null) return;
            string selTarget = _sel != null ? _sel.Target : null;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (MonitorItem it in _mgr.Items)
            {
                int i = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[i];
                row.Tag = it;
                SyncRow(i);
            }
            _grid.ResumeLayout();
            // 恢复选中
            if (selTarget != null)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                    if (row.Tag is MonitorItem && string.Equals(((MonitorItem)row.Tag).Target, selTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        if (row.Cells.Count > 3) _grid.CurrentCell = row.Cells[3];
                        break;
                    }
            }
            _lblStatus.Text = "共 " + _mgr.Items.Count + " 个监控项";
        }
        private void SyncRow(int rowIndex)
        {
            if (_grid == null || rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            DataGridViewRow row = _grid.Rows[rowIndex];
            MonitorItem it = row.Tag as MonitorItem;
            if (it == null) return;
            row.Cells[C_HIDE].Value = it.StartHideWindow;
            row.Cells[C_MIN].Value = it.StartMinimized;
            row.Cells[C_BOOT].Value = it.StartOnBoot;
            row.Cells[3].Value = StateOf(it);
            row.Cells[4].Value = it.DisplayName;
            row.Cells[5].Value = it.MatchText;
            row.Cells[6].Value = it.Restarts.ToString();
            row.Cells[7].Value = it.LastEvent;
            Color fc = it.Running ? (it.Alive ? OkGreen : BadRed) : TextSub;
            foreach (DataGridViewCell c in row.Cells)
            {
                DataGridViewCellStyle cs = c.Style.Clone();
                cs.ForeColor = fc;
                if (c.ColumnIndex < 3) cs.ForeColor = TextMain;
                c.Style = cs;
            }
        }
        private void ToggleItemHide(MonitorItem it, int rowIndex)
        {
            it.StartHideWindow = !it.StartHideWindow;
            SyncRow(rowIndex);
            if (_sel == it) WriteEditFields(it);
            SaveAll();
            _mgr.Notify((it.StartHideWindow ? "已开启" : "已关闭") + "隐藏到托盘运行: " + it.DisplayName + "（下次目标拉起时生效）");
        }
        private void ToggleItemMin(MonitorItem it, int rowIndex)
        {
            it.StartMinimized = !it.StartMinimized;
            SyncRow(rowIndex);
            if (_sel == it) WriteEditFields(it);
            SaveAll();
            _mgr.Notify((it.StartMinimized ? "已开启" : "已关闭") + "最小化运行: " + it.DisplayName + "（下次目标拉起时生效）");
        }
        private void ToggleItemBoot(MonitorItem it, int rowIndex)
        {
            it.StartOnBoot = !it.StartOnBoot;
            SyncRow(rowIndex);
            if (_sel == it) WriteEditFields(it);
            SaveAll();
            _mgr.Notify((it.StartOnBoot ? "已开启" : "已关闭") + "开机自启自动检测: " + it.DisplayName);
        }
        private static string StateOf(MonitorItem it)
        {
            if (!it.Running) return "已停止";
            if (it.Alive) return "运行中";
            if (it.StatusText.StartsWith("风暴")) return "风暴退避";
            if (it.StatusText.StartsWith("重启")) return "重启中";
            return "运行中";
        }

        // ---------------- 保存/加载 ----------------
        private void SaveAll()
        {
            _store.AutoStart = _chkAuto.Checked;
            _store.StartHidden = _chkTray.Checked;
            _store.NekoMode = _neko;
            _store.Items.Clear();
            foreach (MonitorItem it in _mgr.Items) _store.Items.Add(it);
            _store.Save(_cfgPath);
            ApplyAutoStart();
        }
        private void ApplyAutoStart()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk == null) return;
                    if (_store.AutoStart) rk.SetValue("KeepAliveWatchdog", "\"" + Application.ExecutablePath + "\" /autostart");
                    else rk.DeleteValue("KeepAliveWatchdog", false);
                }
            }
            catch (Exception ex) { _mgr.Notify("开机自启设置失败: " + ex.Message); }
        }

        // ---------------- 事件 & 定时刷新 ----------------
        private void StartUiTimer()
        {
            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += delegate
            {
                string ev;
                bool any = false;
                while (_mgr.DrainEvent(out ev)) { _tLog.AppendText(ev + Environment.NewLine); any = true; }
                if (any || (_timer.Tag == null)) { RefreshList(); _timer.Tag = true; }
                else
                {
                    // 每 ~3 秒无条件刷新一次状态
                    _tick++;
                    if (_tick % 6 == 0) RefreshList();
                }
                if (_tick % 2 == 0) SyncItemTrays();   // 每 ~1 秒同步“隐藏软件”的托盘图标
            };
            _timer.Start();
        }
        private int _tick;

        // 为“隐藏到托盘运行”的监控项维护系统托盘图标（软件自己没有托盘时也有图标可找回窗口）
        private void SyncItemTrays()
        {
            try
            {
                var need = new List<MonitorItem>();
                foreach (MonitorItem it in _mgr.Items)
                    if (it.Running && it.StartHideWindow) need.Add(it);

                // 需要但没有 → 创建
                foreach (MonitorItem it in need)
                {
                    if (_itemTrays.ContainsKey(it)) continue;
                    NotifyIcon n = MakeItemTray(it);
                    _itemTrays[it] = n;
                }
                // 不再需要 → 移除
                var dead = new List<MonitorItem>();
                foreach (KeyValuePair<MonitorItem, NotifyIcon> kv in _itemTrays)
                    if (!need.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (MonitorItem k in dead)
                {
                    try { NotifyIcon n = _itemTrays[k]; n.Visible = false; n.Dispose(); } catch { }
                    _itemTrays.Remove(k);
                }
            }
            catch { }
        }

        private NotifyIcon MakeItemTray(MonitorItem it)
        {
            Icon ico = null;
            try { ico = Icon.ExtractAssociatedIcon(it.Target); } catch { }
            if (ico == null) ico = _appIcon != null ? _appIcon : SystemIcons.Application;
            NotifyIcon n = new NotifyIcon { Icon = ico, Visible = true, Text = it.DisplayName + "（已隐藏，点击显示窗口）" };
            n.DoubleClick += delegate { ShowHiddenTarget(it); };
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("显示窗口", null, delegate { ShowHiddenTarget(it); });
            m.Items.Add("再次隐藏", null, delegate { if (!_mgr.HideTargetNow(it)) _mgr.Notify("未找到窗口: " + it.DisplayName); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("结束进程", null, delegate { EndTargetProcess(it); });
            n.ContextMenuStrip = m;
            return n;
        }

        private void ShowHiddenTarget(MonitorItem it)
        {
            if (!_mgr.RestoreTarget(it))
                _mgr.Notify("未找到 " + it.DisplayName + " 的窗口（可能已退出）");
            else
                _mgr.Notify("已显示窗口: " + it.DisplayName);
        }

        private void EndTargetProcess(MonitorItem it)
        {
            if (it == null) return;
            if (MessageBox.Show(this, "确定结束进程：" + it.DisplayName + " ？", "Keep-Alive",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!_mgr.KillTarget(it))
                _mgr.Notify("未找到目标进程: " + it.DisplayName);
            RefreshList();
        }

        private void RefreshLogTail()
        {
            try
            {
                if (!File.Exists(_logPath)) return;
                string[] lines = File.ReadAllLines(_logPath, Encoding.UTF8);
                StringBuilder sb = new StringBuilder();
                for (int i = Math.Max(0, lines.Length - 60); i < lines.Length; i++) sb.AppendLine(lines[i]);
                _tLog.Text = sb.ToString();
                _tLog.SelectionStart = _tLog.Text.Length;
                _tLog.ScrollToCaret();
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                if (_split != null)
                {
                    _split.Panel1MinSize = 380;
                    _split.Panel2MinSize = 300;
                    _split.SplitterDistance = Math.Max(_split.Panel1MinSize, ClientSize.Width - 340);
                }
            }
            catch { }
            _mgr.UiEvent = delegate { };
            StartUiTimer();
            if (!_silent)
            {
                RefreshList();
                if (_mgr.Items.Count > 0) SelectItem(_mgr.Items[0]);
                // 把历史日志尾部载入
                RefreshLogTail();
            }
            _mgr.UiEvent = delegate { };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PlaceNeko();      // 首次显示后，把右上角“猫娘”和右下“删除日志”摆到正确位置
            PlaceLogBtn();
            BeginInvoke((Action)delegate { PlaceNeko(); PlaceLogBtn(); }); // 布局稳定后再摆一次
        }

        // 关闭选择框：是=退出，最小化=收托盘，否=不动作
        private DialogResult ShowCloseChoice()
        {
            using (Form f = new Form())
            {
                f.Text = "Keep-Alive";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(380, 150);
                f.BackColor = Bg;
                f.ForeColor = TextMain;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                Label msg = new Label
                {
                    Text = "要关闭 Keep-Alive 吗？",
                    ForeColor = TextMain,
                    Font = new Font("Microsoft YaHei UI", 10.5f),
                    Location = new Point(18, 18),
                    AutoSize = false,
                    Size = new Size(344, 30),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                f.Controls.Add(msg);
                Button bYes = new Button { Text = "是", Location = new Point(18, 88), Width = 100, Height = 30, DialogResult = DialogResult.Yes };
                Button bMin = new Button { Text = "最小化到托盘", Location = new Point(140, 88), Width = 100, Height = 30, DialogResult = DialogResult.No };
                Button bNo = new Button { Text = "否", Location = new Point(262, 88), Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
                f.Controls.Add(bYes); f.Controls.Add(bMin); f.Controls.Add(bNo);
                f.AcceptButton = bYes;
                f.CancelButton = bNo;
                return f.ShowDialog(this);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_silent && !_forceExit)
            {
                DialogResult r = ShowCloseChoice();
                if (r == DialogResult.No) { e.Cancel = true; Hide(); ShowInTaskbar = false; return; }       // 最小化
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }                                   // 否
                // 是 → 继续退出
            }
            if (_timer != null) _timer.Stop();
            _mgr.StopAll();
            SaveAll();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            foreach (KeyValuePair<MonitorItem, NotifyIcon> kv in _itemTrays)
                try { kv.Value.Visible = false; kv.Value.Dispose(); } catch { }
            _itemTrays.Clear();
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 最小化 -> 收进托盘（开关开启时；静默自启模式也收）
            if (WindowState == FormWindowState.Minimized)
            {
                bool toTray = _silent || (_chkTray != null && _chkTray.Checked);
                if (toTray)
                {
                    Hide();
                    ShowInTaskbar = false;
                    if (!_silent)
                        _tray.ShowBalloonTip(900, "Keep-Alive", Neko("已最小化到托盘（双击图标恢复）"), ToolTipIcon.Info);
                }
            }
        }
    }

    // ---------------- 配置存取 ----------------
    class ConfigStore
    {
        public bool AutoStart = false;
        public bool StartHidden = false;
        public bool NekoMode = false;
        public bool SelfMinStart = false;
        public bool SelfHideStart = false;
        public readonly List<MonitorItem> Items = new List<MonitorItem>();

        public void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                MonitorItem cur = null;
                foreach (string line in lines)
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1);
                    if (k == "AutoStart") { bool b; AutoStart = bool.TryParse(v, out b) && b; }
                    else if (k == "StartHidden") { bool b; StartHidden = bool.TryParse(v, out b) && b; }
                    else if (k == "NekoMode") { bool b; NekoMode = bool.TryParse(v, out b) && b; }
                    else if (k == "SelfMinStart") { bool b; SelfMinStart = bool.TryParse(v, out b) && b; }
                    else if (k == "SelfHideStart") { bool b; SelfHideStart = bool.TryParse(v, out b) && b; }
                    else if (k == "item.begin") { cur = new MonitorItem(); }
                    else if (k == "item.end") { if (cur != null) Items.Add(cur); cur = null; }
                    else if (cur != null)
                    {
                        if (k == "Target") cur.Target = v;
                        else if (k == "Args") cur.Args = v;
                        else if (k == "WorkDir") cur.WorkDir = v;
                        else if (k == "ProcName") cur.ProcName = v;
                        else if (k == "Pattern") cur.Pattern = v;
                        else if (k == "IntervalSec") { int t; if (int.TryParse(v, out t) && t > 0) cur.IntervalSec = t; }
                        else if (k == "MaxCrash") { int t; if (int.TryParse(v, out t) && t > 0) cur.MaxCrash = t; }
                        else if (k == "BackoffSec") { int t; if (int.TryParse(v, out t) && t > 0) cur.BackoffSec = t; }
                        else if (k == "HideConsole") { bool b; cur.HideConsole = bool.TryParse(v, out b) && b; }
                        else if (k == "StartMinimized") { bool b; cur.StartMinimized = bool.TryParse(v, out b) && b; }
                        else if (k == "StartHideWindow") { bool b; cur.StartHideWindow = bool.TryParse(v, out b) && b; }
                        else if (k == "StartOnBoot") { bool b; cur.StartOnBoot = bool.TryParse(v, out b) && b; }
                    }
                }
            }
            catch { }
        }

        public void Save(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("version=2");
                sb.AppendLine("AutoStart=" + AutoStart);
                sb.AppendLine("StartHidden=" + StartHidden);
                sb.AppendLine("NekoMode=" + NekoMode);
                sb.AppendLine("SelfMinStart=" + SelfMinStart);
                sb.AppendLine("SelfHideStart=" + SelfHideStart);
                foreach (MonitorItem it in Items)
                {
                    sb.AppendLine("item.begin");
                    sb.AppendLine("Target=" + it.Target);
                    sb.AppendLine("Args=" + it.Args);
                    sb.AppendLine("WorkDir=" + it.WorkDir);
                    sb.AppendLine("ProcName=" + it.ProcName);
                    sb.AppendLine("Pattern=" + it.Pattern);
                    sb.AppendLine("IntervalSec=" + it.IntervalSec);
                    sb.AppendLine("MaxCrash=" + it.MaxCrash);
                    sb.AppendLine("BackoffSec=" + it.BackoffSec);
                    sb.AppendLine("HideConsole=" + it.HideConsole);
                    sb.AppendLine("StartMinimized=" + it.StartMinimized);
                    sb.AppendLine("StartHideWindow=" + it.StartHideWindow);
                    sb.AppendLine("StartOnBoot=" + it.StartOnBoot);
                    sb.AppendLine("item.end");
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
