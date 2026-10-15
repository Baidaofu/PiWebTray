using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 单文件 exe：不需要任何旁边的 ini / ico，配置可选
[assembly: AssemblyTitle("PiWebTray")]
[assembly: AssemblyProduct("PiWebTray")]
[assembly: AssemblyDescription("Windows tray launcher for pi-web: silent start, update / restart / exit from the notification area")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyCopyright("MIT License")]

#pragma warning disable 649  // 托盘菜单项字段：仅赋值不使用，抑制 CS0649

// PiWebTray - 极小托盘程序：静默启动 pi-web，右键菜单更新/重启/退出。
// 用 Windows PowerShell 5.1 自带的 csc 编译（C# 5 语法，无第三方依赖）。
static class PiWebTray
{
    const string AppName = "PiWebTray";
    const string Package = "@agegr/pi-web@latest";

    static string LogDir, LogFile, CfgFile, ExePath;
    static string BindHost = "127.0.0.1";
    static int Port = 30141;
    static string ExtraArgs = "";

    static Process Proc;
    static NotifyIcon Ni;
    static ToolStripMenuItem MiStatus, MiUpdate, MiRestart, MiStop, MiOpen, MiCopy, MiLog, MiAuto, MiPort;
    static Icon CustomIcon;
    static MemoryStream IconStream;
    static readonly object Sync = new object();
    static volatile bool Busy;
    static volatile bool Quitting;
    static volatile bool External;

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    [STAThread]
    static void Main(string[] args)
    {
        // 调试用：PiWebTray.exe --export-icon out.ico
        if (args != null && args.Length == 2 && args[0] == "--export-icon")
        {
            File.WriteAllBytes(args[1], BuildIcoBytes());
            return;
        }

        // 调试用：PiWebTray.exe --selftest-service（在独立端口跑一遍 启动→重启→停止）
        if (args != null && args.Length == 2 && args[0] == "--selftest-service")
        {
            string d2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(d2);
            string f2 = Path.Combine(d2, "selftest.log");
            ExePath = Application.ExecutablePath;
            LogDir = d2; LogFile = Path.Combine(d2, "selftest.log"); CfgFile = Path.Combine(AppDir(), "pi-web-tray.ini");
            Port = int.Parse(args[1]);
            File.WriteAllText(f2, "selftest port=" + Port + " " + DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine);
            StartService();
            File.AppendAllText(f2, "after start: portOpen=" + PortOpen() + " external=" + External + Environment.NewLine);

            // 场景A：外部实例（非本进程拉起）→ 重启 / 停止是否照样生效
            Process orphan = Proc;
            Proc = null;                      // 装作不是自己拉起的
            External = true;
            File.AppendAllText(f2, "-- external branch (orphan pid=" + (orphan == null ? -1 : orphan.Id) + ") --" + Environment.NewLine);
            RestartService();
            File.AppendAllText(f2, "after external restart: portOpen=" + PortOpen() + " external=" + External + Environment.NewLine);

            Proc = null;
            External = true;
            StopOnly();
            File.AppendAllText(f2, "after external stop: portOpen=" + PortOpen() + Environment.NewLine);
            return;
        }

        // 调试用：PiWebTray.exe --selftest
        if (args != null && args.Length == 1 && args[0] == "--selftest")
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(d);
            string f = Path.Combine(d, "selftest.log");
            File.WriteAllText(f, "start " + DateTime.Now.ToString("HH:mm:ss.fff") + Environment.NewLine);
            ExePath = Application.ExecutablePath;
            string logDir = LogDir;
            Directory.CreateDirectory(logDir);
            LogFile = Path.Combine(logDir, "tray.log");
            string c = Path.Combine(d, "selftest.log");
            CfgFile = Path.Combine(AppDir(), "pi-web-tray.ini");
            File.AppendAllText(f, "paths ok" + Environment.NewLine);
            LoadConfig();
            File.AppendAllText(f, "config ok port=" + Port + Environment.NewLine);
            BuildTray();
            File.AppendAllText(f, "tray built icon=" + (CustomIcon != null ? CustomIcon.Width + "x" + CustomIcon.Height : "null") + Environment.NewLine);
            Ni.Visible = true;
            File.AppendAllText(f, "visible ok" + Environment.NewLine);
            return;
        }

        bool created;
        using (Mutex m = new Mutex(true, "Global\\PiWebTray_SingleInstance", out created))
        {
            if (!created) return;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Crash(e);
            }
        }
    }

    static void Run()
    {
        ExePath = Application.ExecutablePath;
            LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            LogFile = Path.Combine(LogDir, "tray.log");
            CfgFile = Path.Combine(AppDir(), "pi-web-tray.ini");
            Directory.CreateDirectory(LogDir);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Crash(e.ExceptionObject as Exception ?? new Exception("unknown"));
            };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Crash(e.Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            LoadConfig();

            BuildTray();
            Ni.Visible = true;
            Write("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] tray started, port=" + Port + " icon=" + (CustomIcon != null ? CustomIcon.Width + "x" + CustomIcon.Height : "none"));

            // 后台线程启动服务，不阻塞消息循环
            ThreadPool.QueueUserWorkItem(delegate { StartService(); });

            Application.Run();
            Ni.Visible = false;
            StopService();
        Ni.Dispose();
    }

    static void Crash(Exception e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + e.ToString() + Environment.NewLine + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
        try
        {
            MessageBox.Show(e.Message, AppName + " 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    static string AppDir()
    {
        return Path.GetDirectoryName(ExePath);
    }

    // ---------------- 配置 ----------------
    static void LoadConfig()
    {
        try
        {
            if (!File.Exists(CfgFile)) return;
            foreach (string raw in File.ReadAllLines(CfgFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim().ToLowerInvariant();
                string v = line.Substring(i + 1).Trim().Trim('"');
                if (k == "port")
                {
                    int p;
                    if (int.TryParse(v, out p) && p > 0 && p < 65536) Port = p;
                }
                else if (k == "host") BindHost = v;
                else if (k == "args") ExtraArgs = v;
            }
        }
        catch (Exception e) { Write("config error: " + e.Message); }
    }

    

    static string PiWebCmd()
    {
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "pi-web.cmd");
        if (File.Exists(p)) return p;
        return "pi-web.cmd";
    }

    // ---------------- 托盘 UI ----------------
    static void BuildTray()
    {
        CustomIcon = MakeIcon();

        ContextMenuStrip menu = new ContextMenuStrip();
        MiStatus = new ToolStripMenuItem("状态：检测中...");
        MiStatus.Enabled = false;
        MiOpen = new ToolStripMenuItem("打开网页", null, delegate { OpenBrowser(); });
        MiCopy = new ToolStripMenuItem("复制地址", null, delegate { CopyUrl(); });
        menu.Items.Add(MiStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MiOpen);
        menu.Items.Add(MiCopy);
        menu.Items.Add(new ToolStripSeparator());
        MiUpdate = new ToolStripMenuItem("更新 pi-web", null, delegate { Guard(UpdateService); });
        MiRestart = new ToolStripMenuItem("重启服务（含外部实例）", null, delegate { Guard(RestartService); });
        MiStop = new ToolStripMenuItem("停止服务（含外部实例）", null, delegate { Guard(StopOnly); });
        MiLog = new ToolStripMenuItem("打开日志目录", null, delegate { OpenLogDir(); });
        MiPort = new ToolStripMenuItem("端口");
        MiPort.DropDownItems.Add(new ToolStripMenuItem("30141", null, delegate { Guard(delegate { SetPort(30141); }); }));
        MiPort.DropDownItems.Add(new ToolStripMenuItem("30180", null, delegate { Guard(delegate { SetPort(30180); }); }));
        MiPort.DropDownItems.Add(new ToolStripMenuItem("30100", null, delegate { Guard(delegate { SetPort(30100); }); }));
        MiPort.DropDownItems.Add(new ToolStripMenuItem("8000", null, delegate { Guard(delegate { SetPort(8000); }); }));
        MiPort.DropDownItems.Add(new ToolStripMenuItem("其它端口（编辑配置文件）", null, delegate { OpenConfig(); }));
        menu.Items.Add(MiUpdate);
        menu.Items.Add(MiRestart);
        menu.Items.Add(MiStop);
        menu.Items.Add(MiPort);
        menu.Items.Add(MiLog);
        menu.Items.Add(new ToolStripSeparator());
        MiAuto = new ToolStripMenuItem("开机自启动", null, delegate { ToggleAutoStart(); });
        MiAuto.CheckOnClick = false;
        MiAuto.Checked = HasAutoStart();
        menu.Items.Add(MiAuto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Quit(); }));

        Ni = new NotifyIcon();
        Ni.Icon = CustomIcon;
        Ni.Text = AppName + " - " + Port;
        Ni.ContextMenuStrip = menu;
        Ni.Visible = true;
        Ni.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) OpenBrowser();
        };
    }

    static void Guard(ThreadStart action)
    {
        if (Busy) return;
        ThreadPool.QueueUserWorkItem(delegate { action(); });
    }

    static void SetMenuState(string status, bool running)
    {
        try
        {
            if (Ni == null) return;
            MiStatus.Text = "状态：" + status;
            MiUpdate.Enabled = !Busy;
            MiRestart.Enabled = !Busy && running;
            MiStop.Enabled = !Busy && running;
            MiOpen.Enabled = running;
            MiCopy.Enabled = running;
        }
        catch { }
    }

    static void SetTip(string text)
    {
        try { if (Ni != null) Ni.Text = text; } catch { }
    }

    static void Balloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            Ni.BalloonTipTitle = title;
            Ni.BalloonTipText = text;
            Ni.BalloonTipIcon = icon;
            Ni.ShowBalloonTip(4000);
        }
        catch { }
    }

    // ---------------- 图标（程序化生成多分辨率 ICO: 16/24/32/48） ----------------
    static readonly int[] IconSizes = new int[] { 16, 24, 32, 48 };

    static Bitmap DrawIconBitmap(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float inset = size >= 32 ? 1f : 0f;
            RectangleF box = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
            Color top = Color.FromArgb(140, 106, 226);
            Color bottom = Color.FromArgb(74, 58, 190);
            using (Brush bg = new LinearGradientBrush(box, top, bottom, 60f))
                g.FillEllipse(bg, box);
            using (Pen ring = new Pen(Color.FromArgb(255, 255, 255, 255), Math.Max(1f, size / 24f)))
                g.DrawEllipse(ring, box);

            float fs = size * (size <= 16 ? 0.58f : 0.40f);
            using (Font f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("Pi", f, tb, new RectangleF(0, 0, size, size), sf);
            }
        }
        return bmp;
    }

    // ARGB32 -> ICO 用的 BGRA 自下而上位图 + 空 AND 掩码
    static byte[] BitmapToDibBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte[] pixels = new byte[w * h * 4];
        System.Drawing.Imaging.BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bmp.UnlockBits(data); }

        int stride = ((w + 31) / 32) * 4;          // 1bpp 行对齐
        int xorSize = w * h * 4;
        int andSize = stride * h;
        byte[] outBytes = new byte[40 + xorSize + andSize];

        WriteI32(outBytes, 0, 40);                  // biSize
        WriteI32(outBytes, 4, w);                   // biWidth
        WriteI32(outBytes, 8, h * 2);               // biHeight = XOR + AND
        WriteI16(outBytes, 12, 1);                  // biPlanes
        WriteI16(outBytes, 14, 32);                 // biBitCount
        WriteI32(outBytes, 16, 0);                  // BI_RGB
        WriteI32(outBytes, 20, xorSize + andSize);

        int q = 40;
        for (int y = h - 1; y >= 0; y--)             // 自下而上
        {
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                outBytes[q++] = pixels[si + 2];       // B
                outBytes[q++] = pixels[si + 1];       // G
                outBytes[q++] = pixels[si + 0];       // R
                outBytes[q++] = pixels[si + 3];       // A
            }
        }
        // AND 掩码保持全 0（透明由 alpha 通道决定）
        return outBytes;
    }

    static void WriteI32(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    static void WriteI16(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
    }

    static byte[] BuildIcoBytes()
    {
        int n = IconSizes.Length;
        byte[][] images = new byte[n][];
        int offset = 6 + 16 * n;
        for (int i = 0; i < n; i++)
        {
            using (Bitmap bmp = DrawIconBitmap(IconSizes[i]))
                images[i] = BitmapToDibBytes(bmp);
        }

        byte[] ico = new byte[offset + TotalSize(images)];
        WriteI16(ico, 0, 0);      // reserved
        WriteI16(ico, 2, 1);      // type = icon
        WriteI16(ico, 4, n);      // count

        int p = 6;
        for (int i = 0; i < n; i++)
        {
            WriteI32(ico, p + 8, images[i].Length);
            WriteI32(ico, p + 12, offset);
            ico[p] = (byte)(IconSizes[i] >= 256 ? 0 : IconSizes[i]);
            ico[p + 1] = (byte)(IconSizes[i] >= 256 ? 0 : IconSizes[i]);
            ico[p + 2] = 0;  // 调色板数
            ico[p + 3] = 0;  // reserved
            WriteI16(ico, p + 4, 1);
            WriteI16(ico, p + 6, 32);
            p += 16;
            offset += images[i].Length;
        }

        p = 6 + 16 * n;
        for (int i = 0; i < n; i++)
        {
            Buffer.BlockCopy(images[i], 0, ico, p, images[i].Length);
            p += images[i].Length;
        }
        return ico;
    }

    static int TotalSize(byte[][] arr)
    {
        int s = 0;
        for (int i = 0; i < arr.Length; i++) s += arr[i].Length;
        return s;
    }

    static Icon MakeIcon()
    {
        try
        {
            byte[] ico = BuildIcoBytes();
            MemoryStream ms = new MemoryStream(ico);
            Icon ic = new Icon(ms);
            IconStream = ms;   // Icon 依赖流存活，不能提前释放
            return ic;
        }
        catch (Exception e)
        {
            Write("icon error: " + e.Message);
            return SystemIcons.Application;
        }
    }

    // ---------------- 服务控制 ----------------
    static void StartService()
    {
        try { StartServiceCore(); }
        catch (Exception e) { Write("StartService error: " + e); Crash(e); }
    }

    static void StartServiceCore()
    {
        External = false;
        if (Proc != null && !Proc.HasExited) { WaitPort(30); return; }
        Busy = true;
        SetMenuState("启动中...", false);
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] starting pi-web ...");

        // 端口已被占用：区分“已被 pi-web 占用”(静默接管监控) 与“被其它程序占用”(告警)
        if (PortOpen())
        {
            if (PiWebRunningOnPort())
            {
                Write("port " + Port + " already served by pi-web, adopt silently");
                Busy = false;
                External = true;
                SetMenuState("运行中(外部)", true);
                SetTip(AppName + " - 运行中(外部) :" + Port);
            }
            else
            {
                Write("port " + Port + " occupied by another program");
                Busy = false;
                SetMenuState("端口被占用", false);
                SetTip(AppName + " - 端口 " + Port + " 被占用");
                Balloon(AppName, "端口 " + Port + " 被其它程序占用，pi-web 未启动。右键→端口 可换一个端口", ToolTipIcon.Warning);
            }
            return;
        }
        try
        {
            string log = ServerLog();
            Write("server log: " + log);
            string cmd = "call \"" + PiWebCmd() + "\" --port " + Port + " --hostname " + BindHost + " --no-open " + ExtraArgs + " >> \"" + log + "\" 2>&1";
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd);
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            lock (Sync) { Proc = Process.Start(psi); }
            Write("pid=" + Proc.Id);
        }
        catch (Exception e)
        {
            Write("start failed: " + e.Message);
            Balloon(AppName, "启动失败，详见日志", ToolTipIcon.Error);
        }
        bool ok = WaitPort(90);
        Busy = false;
        if (ok)
        {
            CleanupServerLogs(5);
            SetMenuState("运行中", true);
            SetTip(AppName + " - 运行中 :" + Port);
            Balloon(AppName, "pi-web 已启动  " + Url(), ToolTipIcon.Info);
        }
        else
        {
            SetMenuState("未响应", false);
            SetTip(AppName + " - 未响应");
            Balloon(AppName, "未检测到端口 " + Port + "，请查看日志", ToolTipIcon.Warning);
        }
    }

    static bool WaitPort(int seconds)
    {
        DateTime end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end && !Quitting)
        {
            if (PortOpen()) return true;
            if (Proc != null && Proc.HasExited)
            {
                int ec = -1;
                try { ec = Proc.ExitCode; } catch { }
                Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] child exited early, exit=" + ec);
                Thread.Sleep(1500);
                return PortOpen();
            }
            Thread.Sleep(500);
        }
        return PortOpen();
    }

    static bool PortOpen()
    {
        try
        {
            TcpClient c = new TcpClient();
            IAsyncResult ar = c.BeginConnect("127.0.0.1", Port, null, null);
            bool ok = ar.AsyncWaitHandle.WaitOne(800) && c.Connected;
            c.Close();
            return ok;
        }
        catch { return false; }
    }

    // 菜单「停止服务」：自己拉起的或外部启动的都停
    static void StopOnly()
    {
        bool wasExternal = External;
        StopAny();
        Busy = false;
        SetMenuState("已停止", false);
        SetTip(AppName + " - 已停止");
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] stopped by user (external=" + wasExternal + ")");
    }

    // 停止当前在跑的服务，不管它是谁拉起来的
    static void StopAny()
    {
        if (Proc != null && !Proc.HasExited)
        {
            StopService();
            return;
        }
        if (External || (PortOpen() && PiWebRunningOnPort()))
        {
            StopExternal();
            External = false;
        }
    }

    static void StopService()
    {
        // 先用内置的 pi-web stop（优雅关闭），再回退到强杀进程树
        if (Proc != null && !Proc.HasExited)
        {
            RunHidden("cmd.exe", "/c call \"" + PiWebCmd() + "\" stop --port " + Port + " >> \"" + Path.Combine(LogDir, "pi-web-stop.log") + "\" 2>&1", 30);
        }
        Process p;
        lock (Sync) { p = Proc; Proc = null; }
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                RunHidden("taskkill", "/PID " + p.Id + " /T /F", 30);
            }
        }
        catch (Exception e) { Write("stop error: " + e.Message); }
        for (int i = 0; i < 20 && PortOpen(); i++) Thread.Sleep(500);
    }

    // 菜单「重启服务」：外部实例同样支持，停掉后由托盘接管并重新拉起
    static void RestartService()
    {
        try { RestartServiceCore(); }
        catch (Exception e) { Write("RestartService error: " + e); Crash(e); Busy = false; }
    }

    static void RestartServiceCore()
    {
        bool wasExternal = External;
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] restart requested (external=" + wasExternal + ")");
        Busy = true;
        SetMenuState("重启中...", PortOpen());
        StopAny();
        Thread.Sleep(800);
        External = false;
        StartService();
    }

    static void UpdateService()
    {
        try { UpdateServiceCore(); }
        catch (Exception e) { Write("UpdateService error: " + e); Crash(e); Busy = false; }
    }

    static void UpdateServiceCore()
    {
        if (Busy) return;

        // pi-web update 要求服务器处于停止状态；外部实例需要用户确认
        if (External || (Proc == null && PortOpen() && PiWebRunningOnPort()))
        {
            DialogResult r = MessageBox.Show(
                "pi-web 正在运行（端口 " + Port + "，外部启动）。\r\n\r\n" +
                "更新必须先停止该实例，更新完成后由托盘重新拉起。\r\n是否继续？",
                AppName + " - 更新 pi-web", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] update cancelled by user");
                return;
            }
            StopExternal();
            if (PortOpen())
            {
                Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] update aborted: port " + Port + " still busy");
                SetMenuState("端口仍被占用", true);
                Balloon(AppName, "端口 " + Port + " 未释放，已取消更新。", ToolTipIcon.Warning);
                return;
            }
        }
        else
        {
            StopService();
        }

        Busy = true;
        External = false;
        SetMenuState("更新中...", false);
        Balloon(AppName, "正在执行 pi-web update", ToolTipIcon.Info);
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] update start");

        // 抓取输出，便于把真实原因写进日志并提示用户
        string outp = RunCapture("cmd.exe", "/c call \"" + PiWebCmd() + "\" update", 900);
        if (outp != null && outp.Trim().Length > 0)
        {
            Write("---- pi-web update output ----");
            foreach (string l in outp.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                Write("  " + l.TrimEnd());
        }
        bool ok = LooksUpdated(outp);

        if (ok)
        {
            Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] update ok");
            Thread.Sleep(1000);
            StartService();
            Balloon(AppName, "更新完成，服务已重启", ToolTipIcon.Info);
        }
        else
        {
            Busy = false;
            Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] update FAILED");
            SetMenuState("更新失败", PortOpen());
            Balloon(AppName, "更新失败：" + FailureHint(outp), ToolTipIcon.Error);
        }
    }

    // pi-web update 无独立退出码语义：成功时不输出错误信息
    static bool LooksUpdated(string outp)
    {
        if (outp == null) return false;
        string t = outp.ToLowerInvariant();
        string[] bad = { "run `pi-web stop`", "npm err", "error", "failed", "eaddrinuse", "not found" };
        foreach (string b in bad)
            if (t.Contains(b)) return false;
        return true;
    }

    static string FailureHint(string outp)
    {
        if (outp == null) return "未获得输出，详见日志";
        foreach (string l in outp.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            string s = l.Trim();
            if (s.Length == 0) continue;
            if (s.ToLowerInvariant().Contains("npm err") || s.ToLowerInvariant().Contains("error"))
                return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }
        return "详见 %APPDATA%/PiWebTray/tray.log";
    }

    // 停止不是本托盘拉起的 pi-web（用 pi-web stop，不杀进程树）
    static void StopExternal()
    {
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] stopping external pi-web on port " + Port);
        RunCapture("cmd.exe", "/c call \"" + PiWebCmd() + "\" stop --port " + Port, 60);
        for (int i = 0; i < 30 && PortOpen(); i++) Thread.Sleep(500);
        if (PortOpen())
        {
            // pi-web stop 没搞定时按端口找 PID 兜底
            try
            {
                string ns = RunCapture("cmd.exe",
                    "/c for /f \"tokens=5\" %a in ('netstat -ano ^| findstr 127.0.0.1:" + Port + " ^| findstr LISTENING') do taskkill /PID %a /T /F",
                    30);
                if (ns != null) Write(ns.Trim());
            }
            catch { }
            for (int i = 0; i < 20 && PortOpen(); i++) Thread.Sleep(500);
        }
    }

    static bool PiWebRunningOnPort()
    {
        // 借助 pi-web 自带的 status 子命令判断，避免误报
        try
        {
            string outp = RunCapture("cmd.exe", "/c call \"" + PiWebCmd() + "\" status", 20);
            if (outp == null) return true;   // 拿不到结果时保守处理，不打扰用户
            return outp.IndexOf(":" + Port + " ", StringComparison.OrdinalIgnoreCase) >= 0
                || outp.IndexOf(":" + Port + "\r", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return true; }
    }

    static int RunHidden(string file, string args, int timeoutSec)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(timeoutSec * 1000);
                if (!p.HasExited) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }
        catch (Exception e)
        {
            Write("exec error " + file + ": " + e.Message);
            return -2;
        }
    }

    static string RunCapture(string file, string args, int timeoutSec)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutSec * 1000);
                if (!p.HasExited) { try { p.Kill(); } catch { } }
                return so;
            }
        }
        catch { return null; }
    }

    // ---------------- 杂项 ----------------
    static string Url()
    {
        string h = BindHost == "0.0.0.0" ? "127.0.0.1" : BindHost;
        return "http://" + h + ":" + Port;
    }

    static void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url()) { UseShellExecute = true });
        }
        catch (Exception e) { Write("browser error: " + e.Message); }
    }

    static void CopyUrl()
    {
        try { Clipboard.SetText(Url()); Balloon(AppName, "已复制 " + Url(), ToolTipIcon.Info); }
        catch { }
    }

    static void OpenLogDir()
    {
        try { Process.Start(new ProcessStartInfo(LogDir) { UseShellExecute = true }); }
        catch { }
    }

    // ---------------- 端口设置 ----------------
    static void SetPort(int port)
    {
        if (port == Port || Busy) return;
        Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] switch port " + Port + " -> " + port);
        SavePort(port);
        Port = port;
        SetTip(AppName + " - :" + Port);
        Balloon(AppName, "端口已改为 " + Port + "，正在重启服务", ToolTipIcon.Info);
        RestartService();
    }

    static void SavePort(int port)
    {
        try
        {
            string[] lines = File.Exists(CfgFile)
                ? File.ReadAllLines(CfgFile)
                : new string[0];
            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0 && t.StartsWith("port", StringComparison.OrdinalIgnoreCase)
                    && t.IndexOf('=') > 0
                    && t.Substring(0, t.IndexOf('=')).Trim().ToLowerInvariant() == "port")
                {
                    lines[i] = "port=" + port;
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>(lines);
                list.Insert(0, "port=" + port);
                lines = list.ToArray();
            }
            File.WriteAllLines(CfgFile, lines, new UTF8Encoding(false));
        }
        catch (Exception e) { Write("save config error: " + e.Message); }
    }

    static void OpenConfig()
    {
        try
        {
            if (!File.Exists(CfgFile))
                File.WriteAllText(CfgFile, "port=" + Port + Environment.NewLine + "host=" + BindHost + Environment.NewLine + "args=", new UTF8Encoding(false));
            Process.Start("notepad.exe", "\"" + CfgFile + "\"");
            Balloon(AppName, "修改 port= 后，从托盘菜单点「重启服务」生效", ToolTipIcon.Info);
        }
        catch (Exception e) { Write("open config error: " + e.Message); }
    }

    static string StartupLink()
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startup, "PiWebTray.lnk");
    }

    static bool HasAutoStart()
    {
        return File.Exists(StartupLink());
    }

    static void ToggleAutoStart()
    {
        try
        {
            string lnk = StartupLink();
            if (File.Exists(lnk))
            {
                File.Delete(lnk);
                MiAuto.Checked = false;
                Balloon(AppName, "已关闭开机自启动", ToolTipIcon.Info);
            }
            else
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                try
                {
                    object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                    t.GetProperty("TargetPath").SetValue(link, ExePath, null);
                    t.GetProperty("WorkingDirectory").SetValue(link, AppDir(), null);
                    t.GetProperty("Description").SetValue(link, "Pi-web tray launcher", null);
                    t.GetProperty("WindowStyle").SetValue(link, 7, null); // 7 = 最小化
                    t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }
                finally { Marshal.FinalReleaseComObject(sh); }
                MiAuto.Checked = true;
                Balloon(AppName, "已添加开机自启动", ToolTipIcon.Info);
            }
        }
        catch (Exception e)
        {
            Write("autostart error: " + e.Message);
            Balloon(AppName, "设置开机自启失败", ToolTipIcon.Error);
        }
    }

    static void Quit()
    {
        Quitting = true;
        Application.Exit();
    }

    static void Write(string msg)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogFile, msg + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    // 每次启动一个独立日志文件：cmd 的 >> 需要独占写句柄，
    // 若多个 pi-web 实例共用一个日志文件，后启动的会因打不开日志而瞬间退出。
    static string ServerLog()
    {
        return Path.Combine(LogDir, "pi-web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
    }

    // 清理过期的服务日志，只留最近 keepLogs 份
    static void CleanupServerLogs(int keepLogs)
    {
        try
        {
            string full = Path.GetFullPath(LogDir) + Path.DirectorySeparatorChar;
            FileInfo[] files = new DirectoryInfo(LogDir).GetFiles("pi-web-*.log");
            Array.Sort(files, delegate(FileInfo a, FileInfo b) { return b.CreationTime.CompareTo(a.CreationTime); });
            for (int i = keepLogs; i < files.Length; i++)
            {
                if (Path.GetFullPath(files[i].FullName).StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    files[i].Delete();
            }
        }
        catch { }
    }
}