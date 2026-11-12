using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CtaBacktest;

// 回測子程式（spec.md 13.2）：一次跑一件工作，策略只在這裡執行；當掉、卡住都不會拖垮視窗程式。
// 用法：BacktestWorker.exe <工作檔> <結果檔>
// 標準輸出一行一則訊息（格式見 WorkerProtocol）；標準輸入收到 stop 就在下一根 K 棒停止
static class WorkerProgram
{
    private static readonly object outLock = new object();
    private static volatile bool stopRequested;
    private static TextWriter stdout;

    static int Main(string[] args)
    {
        stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        if (args.Length < 2) { Send(WorkerProtocol.Error, "用法：BacktestWorker.exe <工作檔> <結果檔>"); return 2; }
        try
        {
            WorkerJob job = WorkerProtocol.Load<WorkerJob>(args[0]);
            StartStdinReader();
            StartHeartbeat();

            DllLoadResult lr = DllLoader.Load(job.DllPath);
            if (!lr.Ok) { Send(WorkerProtocol.Error, lr.Output); return 3; }
            StrategyInfo info = lr.Strategies.FirstOrDefault(x => x.Type.FullName == job.StrategyClass) ?? (lr.Strategies.Count == 1 ? lr.Strategies[0] : null);
            if (info == null) { Send(WorkerProtocol.Error, "DLL 裡找不到策略類別 " + job.StrategyClass); return 3; }
            SymbolData data = SymbolData.Open(job.DataFolder);
            Func<bool> cancel = () => stopRequested;

            if (job.Kind == WorkerJob.KindOptimize)
            {
                var opt = new Optimizer(data, info.Type, job.Parameters, job.Settings, job.OptParams, job.Opt);
                var all = opt.Run(r => Send(WorkerProtocol.OptRow, WorkerProtocol.ToBase64(r)), cancel);
                WorkerProtocol.Save(args[1], all.Count);
            }
            else
            {
                double lastSent = -1;
                BacktestResult r = Tester.Run(data, info.Type, job.Parameters, job.Settings, p =>
                {
                    if (p - lastSent >= 0.002 || p >= 1) { lastSent = p; Send(WorkerProtocol.Progress, p.ToString("0.####", CultureInfo.InvariantCulture)); }
                }, cancel);
                WorkerProtocol.Save(args[1], r);
            }
            Send(WorkerProtocol.Done, "");
            return 0;
        }
        catch (Exception ex)
        {
            Send(WorkerProtocol.Error, ex.GetType().Name + "：" + ex.Message);
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void Send(char kind, string body)
    {
        // 一則訊息一行：內容不可以有換行
        string line = kind + " " + (body ?? "").Replace("\r", " ").Replace("\n", " ");
        lock (outLock) stdout.WriteLine(line);
    }

    // 視窗程式寫 stop 或關掉標準輸入（視窗程式結束）都當作要停止
    private static void StartStdinReader()
    {
        new Thread(() =>
        {
            try
            {
                // 不用 Console.In：它用系統編碼（Big5），對方若送了 UTF-8 BOM 會和第一個字黏成亂碼
                string line;
                var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                while ((line = reader.ReadLine()) != null)
                    if (line.Contains("stop")) stopRequested = true;
            }
            catch { }
            stopRequested = true;
        }) { IsBackground = true, Name = "StdinReader" }.Start();
    }

    // 每秒回報目前執行最久的策略呼叫，視窗程式據此判斷是否卡住
    private static void StartHeartbeat()
    {
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                string where; DateTime sim;
                double sec = Tester.LongestStrategyCall(out where, out sim);
                Send(WorkerProtocol.Heartbeat, sec.ToString("0.#", CultureInfo.InvariantCulture) + "|" + (where ?? "") + "|"
                    + (sim == default(DateTime) ? "" : sim.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }
        }) { IsBackground = true, Name = "Heartbeat" }.Start();
    }
}
