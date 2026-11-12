using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BacktesterApp
{
    // 應用程式日誌與當機偵測。所有寫檔失敗一律吞掉，不影響程式。
    internal static class AppLog
    {
        private static readonly object Gate = new object();
        private static string logFolder;
        private static string flagPath;
        private static Func<IWin32Window> ownerGetter;
        private static bool handlersInstalled;

        public static bool PreviousRunCrashed { get; private set; }

        public static string LogFolder
        {
            get
            {
                if (logFolder == null)
                    logFolder = Path.Combine(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CtaBacktest"), "logs");
                return logFolder;
            }
        }

        public static void Init(string version)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                CleanOld();
            }
            catch { }

            try
            {
                flagPath = Path.Combine(Path.GetDirectoryName(LogFolder), "running.flag");
                PreviousRunCrashed = File.Exists(flagPath);
                File.WriteAllText(flagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }

            Info("程式啟動 版本=" + version + " 作業系統=" + Environment.OSVersion
                + " .NET=" + Environment.Version + (Environment.Is64BitProcess ? " 64位元" : " 32位元"));
            if (PreviousRunCrashed) Info("偵測到上次沒有正常關閉");
        }

        public static void Info(string message) { Write("INFO", message); }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);
        }

        public static void MarkCleanExit()
        {
            try
            {
                Info("程式正常結束");
                if (flagPath != null && File.Exists(flagPath)) File.Delete(flagPath);
            }
            catch { }
        }

        // 要在 Application.Run 之前呼叫
        public static void InstallGlobalHandlers(Func<IWin32Window> owner)
        {
            ownerGetter = owner;
            if (handlersInstalled) return;
            handlersInstalled = true;

            try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Report("畫面執行緒發生未處理的例外", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Report(e.IsTerminating ? "程式即將結束：發生未處理的例外" : "背景執行緒發生未處理的例外",
                    ex ?? new Exception(Convert.ToString(e.ExceptionObject)));
            };
            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e)
            {
                e.SetObserved();
                Report("背景工作發生未觀察到的例外", e.Exception);
            };
        }

        private static void Report(string title, Exception ex)
        {
            Error(title, ex);
            try
            {
                IWin32Window owner = null;
                try { owner = ownerGetter != null ? ownerGetter() : null; } catch { }
                ErrorDialog.Show(owner, title, title, ex);
            }
            catch { }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogFolder);
                    string file = Path.Combine(LogFolder, "app_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(file, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private static void CleanOld()
        {
            DateTime limit = DateTime.Now.AddDays(-14);
            foreach (string f in Directory.GetFiles(LogFolder, "app_*.log"))
            {
                try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { }
            }
        }
    }
}
