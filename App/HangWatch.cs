using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BacktesterApp
{
    // 卡住偵測（spec 6.5）：畫面執行緒 10 秒沒報到就記下它的呼叫堆疊
    internal static class HangWatch
    {
        private const int PingMs = 2000, HangSec = 10, RepeatSec = 10, MaxDumps = 6, KeepFiles = 20;

        private static Thread ui;
        private static Control owner;
        private static long lastPongTicks = DateTime.UtcNow.Ticks;
        private static volatile string lastStatus = "", lastAction = "（還沒有操作）";
        private static string file;
        private static readonly object fileLock = new object();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CtaBacktest", "hang"); }
        }

        // 在畫面執行緒、表單建好之後呼叫
        public static void Start(Form form)
        {
            ui = Thread.CurrentThread;
            owner = form;
            file = Path.Combine(Folder, "hang_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
            Track(form);
            Application.ThreadException += (s, e) => Write("未處理的例外（畫面執行緒）", e.Exception.ToString());
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("未處理的例外", Convert.ToString(e.ExceptionObject));
            new Thread(Loop) { IsBackground = true, Name = "HangWatch" }.Start();
        }

        // 記下按鈕、勾選、分頁切換與狀態列文字（都在畫面執行緒觸發，不跨執行緒讀控制項）
        private static void Track(Control c)
        {
            if (c is ButtonBase) c.Click += (s, e) => lastAction = "按「" + ((Control)s).Text + "」 " + Now();
            var tc = c as TabControl;
            if (tc != null) tc.SelectedIndexChanged += (s, e) => lastAction = "切到分頁「" + (tc.SelectedTab != null ? tc.SelectedTab.Text : "") + "」 " + Now();
            if (c is Label && c.Name == "lbStatus") c.TextChanged += (s, e) => lastStatus = ((Control)s).Text;
            foreach (Control child in c.Controls) Track(child);
        }

        public static void Note(string action) { lastAction = action + " " + Now(); }
        public static void NoteStatus(string text) { lastStatus = text ?? ""; }

        private static string Now() { return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture); }

        private static void Loop()
        {
            DateTime? hangStart = null, lastDump = null;
            int dumps = 0;
            while (true)
            {
                Thread.Sleep(PingMs);
                if (owner.IsDisposed) return;
                // 視窗還沒建好（Application.Run 之前）不算卡住
                if (!owner.IsHandleCreated) { Interlocked.Exchange(ref lastPongTicks, DateTime.UtcNow.Ticks); continue; }
                try { owner.BeginInvoke(new Action(() => Interlocked.Exchange(ref lastPongTicks, DateTime.UtcNow.Ticks))); }
                catch { return; }   // 視窗已關
                DateTime pong = new DateTime(Interlocked.Read(ref lastPongTicks), DateTimeKind.Utc);
                double silent = (DateTime.UtcNow - pong).TotalSeconds;
                if (silent < HangSec)
                {
                    if (hangStart.HasValue)
                        Write("恢復", "共卡 " + (DateTime.UtcNow - hangStart.Value).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " 秒");
                    hangStart = null; lastDump = null; dumps = 0;
                    continue;
                }
                if (!hangStart.HasValue) hangStart = pong;
                if (dumps < MaxDumps && (!lastDump.HasValue || (DateTime.UtcNow - lastDump.Value).TotalSeconds >= RepeatSec))
                {
                    dumps++;
                    lastDump = DateTime.UtcNow;
                    Write("畫面卡住 " + silent.ToString("0", CultureInfo.InvariantCulture) + " 秒（第 " + dumps + " / " + MaxDumps + " 次記錄）", UiStack());
                }
            }
        }

#pragma warning disable 618
        private static string UiStack()
        {
            StackTrace st = null;
            string err = null;
            bool suspended = false;
            try
            {
                ui.Suspend();
                suspended = true;
                st = new StackTrace(ui, true);
            }
            catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; }
            finally { if (suspended) { try { ui.Resume(); } catch { } } }
            if (st == null) return "取不到堆疊：" + err;
            var sb = new StringBuilder();
            foreach (StackFrame f in st.GetFrames() ?? new StackFrame[0])
            {
                var m = f.GetMethod();
                sb.Append("   ").Append(m == null ? "?" : (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name);
                if (f.GetFileName() != null) sb.Append("  ").Append(Path.GetFileName(f.GetFileName())).Append(':').Append(f.GetFileLineNumber());
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
#pragma warning restore 618

        private static void Write(string title, string body)
        {
            try
            {
                lock (fileLock)
                {
                    bool first = !File.Exists(file);
                    if (first) { Directory.CreateDirectory(Folder); Prune(); }
                    var sb = new StringBuilder();
                    if (first) sb.Append("自製回測器卡住偵測，開啟時間 ").Append(Path.GetFileNameWithoutExtension(file).Substring(5)).Append("\r\n\r\n");
                    sb.Append("== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").Append(title).Append("\r\n");
                    sb.Append("最後操作：").Append(lastAction).Append("\r\n");
                    sb.Append("狀態列：").Append(lastStatus).Append("\r\n");
                    try
                    {
                        var p = Process.GetCurrentProcess();
                        sb.Append("CPU 累計 ").Append(p.TotalProcessorTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                          .Append(" 秒，記憶體 ").Append(p.WorkingSet64 / 1048576).Append(" MB\r\n");
                    }
                    catch { }
                    sb.Append(body).Append("\r\n\r\n");
                    File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
                }
            }
            catch { }
        }

        private static void Prune()
        {
            try
            {
                foreach (var f in new DirectoryInfo(Folder).GetFiles("hang_*.txt").OrderByDescending(x => x.Name).Skip(KeepFiles - 1))
                    f.Delete();
            }
            catch { }
        }
    }
}
