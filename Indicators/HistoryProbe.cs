﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using Runtime.Script;
using TradeApi.History;
using TradeApi.Indicators;
using TradeApi.Instruments;

// ============================================================================
// HistoryProbe 平台歷史長度探測 v0.2（規格見 文件\群益交易王CTA\Backtester\spec.md 3.4）
// v0.2：平台一次最多給約 40 萬根（2026-11-10 實測 EURUSD M1 剛好 400000 根），加「往前追」：
// 以最舊那根為終點再要，直到平台給 0 根或不再更早。
// 掛在任一圖表一次：對 Symbols 每個商品、Timeframes 每個週期，向平台要「StartYear 到現在」，
// 記下平台給幾根、最舊／最新、從最新往回的連續區段有多長、缺口在哪。
// 只寫報告 文件\群益交易王CTA\Export\HistoryProbe\probe_<時間>.txt，不寫 K 棒 CSV、不畫線、不下單。
// 要量「伺服器」能給多少，請在空的本機歷史快取下執行（步驟見 Backtester\README.md）。
// ============================================================================

namespace HistoryProbeIndicator
{
    public class HistoryProbe : IndicatorBuilder
    {
        [InputParameter(InputType.String, "商品代號 Symbols（逗號分隔）", 0)]
        public string Symbols = "EURUSD,USDJPY,NZDCAD,XAUUSD,XTIUSD,NDAQ100";

        [InputParameter(InputType.String, "週期 Timeframes（M1,M5,M15,M30,H1,H4,D1）", 1)]
        public string Timeframes = "M1,M5,M15,M30,H1,H4,D1";

        [InputParameter(InputType.Numeric, "起始年份 StartYear", 2)]
        [SimpleNumeric(1990D, 2100D, 0, 1)]
        public int StartYear = 2000;

        [InputParameter(InputType.Numeric, "筆數幾秒不變視為載完 StableSeconds", 3)]
        [SimpleNumeric(2D, 600D, 0, 1)]
        public int StableSeconds = 15;

        [InputParameter(InputType.Numeric, "每項最多等幾秒 MaxWaitSeconds", 4)]
        [SimpleNumeric(30D, 3600D, 0, 1)]
        public int MaxWaitSeconds = 300;

        [InputParameter(InputType.Numeric, "往前追幾輪 WalkBackRounds（以最舊那根為終點再要，0 = 不追；D1 不追）", 5)]
        [SimpleNumeric(0D, 50D, 0, 1)]
        public int WalkBackRounds = 10;

        private class Job
        {
            public string Symbol, Tf;
            public Period Period;
            public int Value;
            public Instrument Instrument;
            public TimeHistoricalRequest Request;
            public HistoricalData Data;
            public bool Sent, NotFound;
            public string Error;
            public DateTime ReadyUtc, SentUtc, LastChangeUtc;
            public int LastCount = -1, Changes;
            public int Round;                  // 0 = 第一次（到現在）；1 起是往前追
            public DateTime To;                // 往前追時的區間終點（不含）；第一次為 MinValue
        }

        private readonly object sync = new object();
        private readonly List<Job> jobs = new List<Job>();
        private readonly List<string> results = new List<string>();
        private int next;
        private Job current;
        private bool started, finished;
        private DataType dataType = DataType.Trade;
        private System.Threading.Timer timer;
        private int polling;
        private DateTime lastShownUtc = DateTime.MinValue;
        private string reportPath;

        public HistoryProbe() : base()
        {
            Credentials.ProjectName = "HistoryProbe";
            Lines.Set("none");
            Lines["none"].Color = Color.Transparent;
            SeparateWindow = false;
        }

        public override void Init()
        {
            lock (sync)
            {
                jobs.Clear();
                results.Clear();
                next = 0;
                current = null;
                started = false;
                finished = false;
                char[] sep = { ',', ';', ' ', '\t', '\r', '\n' };
                var syms = new List<string>();
                foreach (string raw in (Symbols ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    string s = raw.Trim().ToUpperInvariant();
                    if (s.Length > 0 && !syms.Contains(s)) syms.Add(s);
                }
                var tfs = new List<string>();
                foreach (string raw in (Timeframes ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = raw.Trim().ToUpperInvariant();
                    Period p; int v;
                    if (ParseTf(t, out p, out v) && !tfs.Contains(t)) tfs.Add(t);
                }
                // 同一個週期的所有商品排在一起，方便對照
                foreach (string t in tfs)
                    foreach (string s in syms)
                    {
                        Period p; int v;
                        ParseTf(t, out p, out v);
                        jobs.Add(new Job { Symbol = s, Tf = t, Period = p, Value = v });
                    }
            }
        }

        private static bool ParseTf(string t, out Period p, out int v)
        {
            p = Period.Minute; v = 1;
            switch (t)
            {
                case "M1": return true;
                case "M5": v = 5; return true;
                case "M15": v = 15; return true;
                case "M30": v = 30; return true;
                case "H1": p = Period.Hour; v = 1; return true;
                case "H4": p = Period.Hour; v = 4; return true;
                case "D1": p = Period.Day; v = 1; return true;
                default: return false;
            }
        }

        // 缺口門檻同回測器 DataQuality：分鐘線到 H4 超過 5 天，D1 超過 10 天
        private static double GapDays(string tf) { return tf == "D1" ? 10 : 5; }

        public override void Update(TickStatus args)
        {
            try
            {
                bool start;
                lock (sync) { start = !started; started = true; }
                if (start) Start();
                SendPending(false);
                DateTime now = DateTime.UtcNow;
                if ((now - lastShownUtc).TotalSeconds >= 1) { lastShownUtc = now; ShowProgress(); }
            }
            catch { }
        }

        private void Start()
        {
            try
            {
                HistoricalRequest req = HistoryDataSeries.HistoricalRequest;
                if (req != null) dataType = req.DataType;
            }
            catch { }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export", "HistoryProbe");
            Directory.CreateDirectory(dir);
            HistoricalDataManager.OnLoaded -= OnLoaded;
            HistoricalDataManager.OnLoaded += OnLoaded;
            lock (sync)
            {
                reportPath = Path.Combine(dir, "probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt");
                results.Add("HistoryProbe v0.2 開始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "（UTC " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "），要求區間 " + StartYear + "-01-01 到現在，DataType " + dataType
                    + "，共 " + jobs.Count + " 項");
                results.Add("欄位：商品 週期｜平台給幾根｜最舊～最新｜從最新往回的連續區段（天數）｜缺口數與最大的 3 個｜等候秒數、筆數變動次數");
                if (timer == null) timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, 3000, 3000);
            }
            WriteReport();
        }

        private void SendPending(bool fromTimer)
        {
            Job j;
            lock (sync)
            {
                j = current;
                if (j == null || j.Sent) return;
                if (fromTimer && (DateTime.UtcNow - j.ReadyUtc).TotalSeconds < 20) return;
                j.Sent = true;
                j.SentUtc = DateTime.UtcNow;
                j.LastChangeUtc = j.SentUtc;
            }
            try
            {
                Instrument inst = Find(j.Symbol);
                if (inst == null) { lock (sync) j.NotFound = true; return; }
                var request = new TimeHistoricalRequest(inst, dataType, j.Period, j.Value);
                lock (sync) { j.Instrument = inst; j.Request = request; }
                var interval = new Interval(new DateTime(StartYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), j.Round == 0 ? DateTime.UtcNow.AddDays(1) : j.To);
                HistoricalData d = HistoricalDataManager.Get(request, interval);
                lock (sync) { if (d != null && j.Data == null) j.Data = d; }
            }
            catch (Exception ex) { lock (sync) j.Error = ex.GetType().Name + " " + ex.Message; }
        }

        private Instrument Find(string sym)
        {
            List<Instrument> list = InstrumentsManager.GetInstruments(sym);
            if (list == null || list.Count == 0) return null;
            foreach (Instrument i in list) if (i != null && i.Symbol == sym) return i;
            foreach (Instrument i in list)
                if (i != null && (i.Symbol ?? "").Replace("/", "").Equals(sym, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }

        private void OnLoaded(HistoricalData d)
        {
            try
            {
                if (d == null) return;
                var tr = d.HistoricalRequest as TimeHistoricalRequest;
                lock (sync)
                {
                    Job j = current;
                    if (j == null || !j.Sent || j.Request == null || tr == null) return;
                    bool same = ReferenceEquals(tr, j.Request);
                    bool similar = !same && j.Data == null && tr.Period == j.Period && tr.Value == j.Value
                        && tr.Instrument != null && tr.Instrument.Symbol == j.Instrument.Symbol;
                    if (same || similar) { j.Data = d; j.LastCount = -1; j.LastChangeUtc = DateTime.UtcNow; }
                }
            }
            catch { }
        }

        private void Poll()
        {
            if (System.Threading.Interlocked.Exchange(ref polling, 1) == 1) return;
            try { PollOnce(); }
            finally { System.Threading.Interlocked.Exchange(ref polling, 0); }
        }

        private void PollOnce()
        {
            DateTime now = DateTime.UtcNow;
            Job done = null;
            bool due = false;
            lock (sync)
            {
                if (finished) return;
                if (current == null)
                {
                    if (next >= jobs.Count)
                    {
                        finished = true;
                        results.Add("全部完成 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    }
                    else { current = jobs[next++]; current.ReadyUtc = now; }
                }
                else if (!current.Sent) due = (now - current.ReadyUtc).TotalSeconds >= 20;
                else if (current.NotFound || current.Error != null) { done = current; current = null; }
                else
                {
                    double waited = (now - current.SentUtc).TotalSeconds;
                    int n = -1;
                    if (current.Data != null) { try { n = current.Data.Count; } catch { n = -1; } }
                    if (n != current.LastCount) { if (current.LastCount >= 0) current.Changes++; current.LastCount = n; current.LastChangeUtc = now; }
                    bool stable = current.Data != null && (now - current.LastChangeUtc).TotalSeconds >= StableSeconds && waited >= StableSeconds * 2;
                    if (stable || waited >= MaxWaitSeconds) { done = current; current = null; }
                }
            }
            if (due) new System.Threading.Thread(() => { try { SendPending(true); } catch { } }) { IsBackground = true }.Start();
            if (done != null) Analyze(done);
            if (finished || done != null) WriteReport();
            ShowProgress();
        }

        // 一項完成：只分析時間，不存 K 棒
        private void Analyze(Job j)
        {
            string head = j.Symbol + " " + j.Tf + (j.Round == 0 ? "" : " 往前第 " + j.Round + " 輪（終點 " + j.To.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）") + "｜";
            string line;
            DateTime walkTo = DateTime.MinValue;   // 要再往前追時的新終點
            if (j.NotFound) line = head + "平台找不到這個商品";
            else if (j.Error != null) line = head + "請求失敗 " + j.Error;
            else if (j.Data == null) line = head + "平台沒回應（等了 " + MaxWaitSeconds + " 秒）";
            else
            {
                try
                {
                    HistoricalData d = j.Data;
                    int n = d.Count;
                    var times = new List<DateTime>(n);
                    for (int i = n - 1; i >= 0; i--)
                    {
                        DateTime t = d.GetTimeUtc(i);
                        // 往前追時只算終點之前的（平台可能多給重疊的）
                        if (j.Round > 0 && t >= j.To) continue;
                        times.Add(t);
                    }
                    times.Sort();
                    if (times.Count > 0 && j.Tf != "D1") walkTo = times[0];
                    if (times.Count == 0) line = head + "0 根（平台沒有更早的資料）";
                    else
                    {
                        double th = GapDays(j.Tf);
                        var gaps = new List<KeyValuePair<DateTime, double>>();
                        for (int i = 1; i < times.Count; i++)
                        {
                            double days = (times[i] - times[i - 1]).TotalDays;
                            if (days > th) gaps.Add(new KeyValuePair<DateTime, double>(times[i - 1], days));
                        }
                        DateTime first = times[0], last = times[times.Count - 1];
                        DateTime segStart = gaps.Count == 0 ? first : times[0];
                        if (gaps.Count > 0)
                        {
                            // 最後一個缺口之後的第一根
                            DateTime lastGapFrom = gaps[gaps.Count - 1].Key;
                            int k = times.BinarySearch(lastGapFrom);
                            segStart = times[k + 1];
                        }
                        gaps.Sort((a, b) => b.Value.CompareTo(a.Value));
                        var gtxt = new StringBuilder();
                        for (int i = 0; i < Math.Min(3, gaps.Count); i++)
                            gtxt.Append(i == 0 ? "" : "、").Append(gaps[i].Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" 起 ").Append(Math.Round(gaps[i].Value)).Append(" 天");
                        line = string.Format(CultureInfo.InvariantCulture,
                            "{0}{1} 根｜{2:yyyy-MM-dd HH:mm}～{3:yyyy-MM-dd HH:mm}｜連續 {4:yyyy-MM-dd}～{5:yyyy-MM-dd}（{6:N0} 天）｜缺口 {7} 個{8}｜等 {9:N0} 秒、變動 {10} 次",
                            head, times.Count, first, last, segStart, last, (last - segStart).TotalDays, gaps.Count,
                            gaps.Count > 0 ? "：" + gtxt : "", (DateTime.UtcNow - j.SentUtc).TotalSeconds, j.Changes);
                    }
                }
                catch (Exception ex) { line = head + "分析失敗 " + ex.Message; }
            }
            lock (sync)
            {
                results.Add(line);
                // 還有資料、而且最舊那根比上一輪更早，就排下一輪（插在下一項之前，同一商品週期連著跑）
                bool progressed = walkTo != DateTime.MinValue && (j.Round == 0 || walkTo < j.To)
                    && walkTo > new DateTime(StartYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                if (progressed && j.Round < WalkBackRounds)
                    jobs.Insert(next, new Job { Symbol = j.Symbol, Tf = j.Tf, Period = j.Period, Value = j.Value, Round = j.Round + 1, To = walkTo });
            }
            // 不留著資料，讓平台可以釋放
            try { if (j.Data != null) HistoricalDataManager.Remove(j.Data); } catch { }
            lock (sync) j.Data = null;
        }

        private void WriteReport()
        {
            try
            {
                var sb = new StringBuilder();
                string path;
                lock (sync) { path = reportPath; foreach (string l in results) sb.Append(l).Append("\r\n"); }
                if (path != null) File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        private void ShowProgress()
        {
            try
            {
                var sb = new StringBuilder();
                lock (sync)
                {
                    int doneCount = next - (current != null ? 1 : 0);
                    sb.Append("HistoryProbe：").Append(finished ? jobs.Count : doneCount).Append(" / ").Append(jobs.Count)
                      .Append(finished ? "，全部完成，可以移除指標" : "，請保持圖表開著").Append('\n');
                    if (current != null)
                        sb.Append("目前 ").Append(current.Symbol).Append(' ').Append(current.Tf).Append(current.Sent
                            ? "，等平台回應 " + (int)(DateTime.UtcNow - current.SentUtc).TotalSeconds + " 秒" + (current.LastCount >= 0 ? "（" + current.LastCount + " 根）" : "")
                            : "，準備送出").Append('\n');
                    for (int i = Math.Max(2, results.Count - 5); i < results.Count; i++) sb.Append(results[i]).Append('\n');
                }
                Notification.Comment(sb.ToString());
            }
            catch { }
        }

        public override void Complete()
        {
            lock (sync)
            {
                if (timer != null) { try { timer.Dispose(); } catch { } timer = null; }
                try { HistoricalDataManager.OnLoaded -= OnLoaded; } catch { }
                current = null;
                started = false;
            }
        }
    }
}
