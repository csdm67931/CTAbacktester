using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;

namespace CtaBacktest
{
    // 交給子程式 BacktestWorker.exe 的一件工作（spec.md 13.2）
    [Serializable]
    public sealed class WorkerJob
    {
        public const string KindRun = "run", KindOptimize = "opt";
        public string Kind = KindRun;
        public string DataFolder;                  // 商品資料夾（SymbolData.Open 用）
        public string DllPath;                     // 策略 DLL（EvoCode 編的，或自檢編的）
        public string StrategyClass;               // 類別全名
        public Dictionary<string, object> Parameters = new Dictionary<string, object>();
        public TesterSettings Settings;
        public List<OptParam> OptParams;           // 最佳化才用
        public OptSettings Opt;
    }

    // 子程式與視窗程式之間的檔案與訊息格式
    public static class WorkerProtocol
    {
        // 子程式標準輸出一行一則：P 進度（0～1）、H 心跳（最久策略呼叫秒數|函式|模擬時間）、R 最佳化一組（Base64）、E 錯誤、D 完成
        public const char Progress = 'P', Heartbeat = 'H', OptRow = 'R', Error = 'E', Done = 'D';

        public static void Save(string path, object obj)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) new BinaryFormatter().Serialize(fs, obj);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static T Load<T>(string path)
        {
            using (var fs = File.OpenRead(path)) return (T)new BinaryFormatter().Deserialize(fs);
        }

        public static string ToBase64(object obj)
        {
            using (var ms = new MemoryStream()) { new BinaryFormatter().Serialize(ms, obj); return Convert.ToBase64String(ms.ToArray()); }
        }

        public static T FromBase64<T>(string s)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(s))) return (T)new BinaryFormatter().Deserialize(ms);
        }
    }

    // 視窗程式這一端：開子程式、看進度、停止、逾時、記憶體上限、異常結束（spec.md 13.2）。
    // RunBacktest／RunOptimize 會阻塞，請在背景執行緒呼叫；一個物件跑一件工作
    public sealed class WorkerClient
    {
        public int TimeoutSeconds = 60;            // 單一策略呼叫超過這麼久沒回來 → 結束子程式
        public long MemoryLimitBytes = DefaultMemoryLimit();
        public int StopGraceMs = 3000;             // 按停止後等子程式自己停的時間
        public Action<double> Progress;            // 在讀取執行緒上呼叫
        public Action<OptResult> OptResultReceived;

        public string Failure { get; private set; }   // null = 正常完成或使用者停止；否則是給使用者看的原因
        public bool ForcedStop { get; private set; }  // 停止後子程式沒在時限內結束，被直接結束
        public int ExitCode { get; private set; }

        private Process proc;
        private readonly object sync = new object();
        private double longestCall;
        private string longestWhere, longestSim;
        private string workerError;
        private bool done;
        private readonly Queue<string> stderrTail = new Queue<string>();

        // 子程式放在 CtaBacktest.dll 旁邊
        public static string WorkerPath
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(WorkerClient).Assembly.Location), "BacktestWorker.exe"); }
        }

        public BacktestResult RunBacktest(WorkerJob job, Func<bool> cancel)
        {
            job.Kind = WorkerJob.KindRun;
            string result = Execute(job, cancel);
            if (result == null) return null;
            try { return WorkerProtocol.Load<BacktestResult>(result); }
            catch (Exception ex) { Failure = "讀不到子程式的結果：" + ex.Message; return null; }
            finally { TryDelete(result); }
        }

        // 回傳依序號排序的全部結果；子程式中途結束時回傳已收到的
        public List<OptResult> RunOptimize(WorkerJob job, Func<bool> cancel)
        {
            job.Kind = WorkerJob.KindOptimize;
            var got = new List<OptResult>();
            Action<OptResult> prev = OptResultReceived;
            OptResultReceived = r => { lock (got) got.Add(r); if (prev != null) prev(r); };
            try
            {
                string result = Execute(job, cancel);
                if (result != null) TryDelete(result);
            }
            finally { OptResultReceived = prev; }
            lock (got) { got.Sort((a, b) => a.Pass.CompareTo(b.Pass)); return got; }
        }

        // 跑完回傳結果檔路徑；失敗回傳 null 並設 Failure
        private string Execute(WorkerJob job, Func<bool> cancel)
        {
            if (!File.Exists(WorkerPath)) { Failure = "找不到子程式 " + WorkerPath + "，請重新建置回測器（build.cmd）。"; return null; }
            string dir = Path.Combine(Path.GetTempPath(), "CtaBacktest", "jobs");
            Directory.CreateDirectory(dir);
            // 上次當掉或被強制結束留下的工作檔：一天以前的刪掉
            try { foreach (string old in Directory.GetFiles(dir)) if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) TryDelete(old); } catch { }
            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string jobFile = Path.Combine(dir, id + ".job"), resultFile = Path.Combine(dir, id + ".result");
            WorkerProtocol.Save(jobFile, job);

            var psi = new ProcessStartInfo(WorkerPath, "\"" + jobFile + "\" \"" + resultFile + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                proc = Process.Start(psi);
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sync) { stderrTail.Enqueue(e.Data); while (stderrTail.Count > 20) stderrTail.Dequeue(); } };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                DateTime stopAsked = DateTime.MinValue;
                bool first = true;
                // 第一次不等：啟動前就按了停止時馬上送出
                while (first || !proc.WaitForExit(200))
                {
                    first = false;
                    if (cancel != null && cancel() && stopAsked == DateTime.MinValue)
                    {
                        stopAsked = DateTime.UtcNow;
                        // 直接寫 ASCII 位元組：StandardInput 會先送 UTF-8 BOM
                        try { var b = Encoding.ASCII.GetBytes("stop\n"); proc.StandardInput.BaseStream.Write(b, 0, b.Length); proc.StandardInput.BaseStream.Flush(); } catch { }
                    }
                    if (stopAsked != DateTime.MinValue && (DateTime.UtcNow - stopAsked).TotalMilliseconds > StopGraceMs)
                    {
                        ForcedStop = true; Kill(); break;
                    }
                    double call; string where, sim;
                    lock (sync) { call = longestCall; where = longestWhere; sim = longestSim; }
                    if (TimeoutSeconds > 0 && call > TimeoutSeconds)
                    {
                        Failure = "策略的 " + where + " 執行超過 " + TimeoutSeconds + " 秒沒有回來（模擬時間 " + sim + "），可能卡在無窮迴圈，已停止回測。";
                        Kill(); break;
                    }
                    if (MemoryLimitBytes > 0)
                    {
                        long mem = 0;
                        try { proc.Refresh(); mem = proc.PrivateMemorySize64; } catch { }
                        if (mem > MemoryLimitBytes)
                        {
                            Failure = "回測用掉的記憶體超過上限 " + (MemoryLimitBytes >> 20) + " MB（目前 " + (mem >> 20) + " MB），已停止。可以縮短回測期間、減少最佳化同時執行數，或檢查策略是否一直累積資料。";
                            Kill(); break;
                        }
                    }
                }
                proc.WaitForExit();   // 讓非同步讀取把剩下的輸出讀完
                ExitCode = proc.ExitCode;
            }
            catch (Exception ex) { Failure = "無法執行子程式：" + ex.Message; Kill(); return null; }
            finally
            {
                TryDelete(jobFile);
                TryDelete(resultFile + ".tmp");   // 寫結果寫到一半被結束時留下的
                if (proc != null) proc.Dispose();
            }

            if (Failure != null) { TryDelete(resultFile); return null; }
            if (ForcedStop) { TryDelete(resultFile); Failure = null; return null; }
            if (done && File.Exists(resultFile)) return resultFile;
            Failure = Explain(ExitCode, workerError, StderrTail());
            TryDelete(resultFile);
            return null;
        }

        // 子程式異常結束的說明
        public static string Explain(int code, string workerError, string stderr)
        {
            string why;
            if (code == unchecked((int)0xC00000FD)) why = "策略發生堆疊溢位（常見原因：函式無限遞迴呼叫自己）";
            else if (code == unchecked((int)0xC0000005)) why = "子程式發生記憶體存取錯誤";
            else if (code == unchecked((int)0xE0434352)) why = "策略丟出沒有被接住的例外";
            else why = "子程式異常結束（結束代碼 " + code + "）";
            var sb = new StringBuilder(why).Append("，回測沒有完成。回測器本身不受影響。");
            if (!string.IsNullOrEmpty(workerError)) sb.Append("\r\n").Append(workerError);
            if (!string.IsNullOrEmpty(stderr)) sb.Append("\r\n子程式最後的錯誤輸出：\r\n").Append(stderr);
            return sb.ToString();
        }

        public void Kill()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        }

        private string StderrTail() { lock (sync) return string.Join("\r\n", stderrTail.ToArray()); }

        private void OnLine(string line)
        {
            if (line.Length < 2) return;
            string body = line.Substring(2);
            switch (line[0])
            {
                case WorkerProtocol.Progress:
                    double p;
                    if (Progress != null && double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out p)) Progress(p);
                    break;
                case WorkerProtocol.Heartbeat:
                    string[] f = body.Split('|');
                    double sec;
                    if (f.Length >= 3 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                        lock (sync) { longestCall = sec; longestWhere = f[1]; longestSim = f[2]; }
                    break;
                case WorkerProtocol.OptRow:
                    if (OptResultReceived != null)
                    {
                        OptResult r = null;
                        try { r = WorkerProtocol.FromBase64<OptResult>(body); } catch { }
                        if (r != null) OptResultReceived(r);
                    }
                    break;
                case WorkerProtocol.Error:
                    lock (sync) workerError = workerError == null ? body : workerError + "\r\n" + body;
                    break;
                case WorkerProtocol.Done:
                    done = true;
                    break;
            }
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // 預設記憶體上限：實體記憶體一半，最多 8 GB（讀不到時 4 GB）
        public static long DefaultMemoryLimit()
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            long total = GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 8L << 30;
            return Math.Min(total / 2, 8L << 30);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    }
}
