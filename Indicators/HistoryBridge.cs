﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Runtime.Script;
using TradeApi.History;
using TradeApi.Indicators;
using TradeApi.Instruments;

// ============================================================================
// HistoryBridge 資料橋 v0.2（規格見 文件\群益交易王CTA\Backtester\spec.md 3.5）
// 掛在任一圖表一次、常駐不移除。自製回測器把請求單寫到 Export\Bridge\requests\，
// 本指標向平台要資料，合併進 Export\HistoryData\<商品>\<週期>.csv（平台為準、只增不減），
// 結果寫到 Export\Bridge\responses\。每 5 秒寫心跳 heartbeat.txt。不畫線、不下單。
// ============================================================================

namespace HistoryBridgeIndicator
{
    public class HistoryBridge : IndicatorBuilder
    {
        [InputParameter(InputType.Numeric, "筆數幾秒不變視為載完 StableSeconds", 0)]
        [SimpleNumeric(2D, 600D, 0, 1)]
        public int StableSeconds = 15;

        [InputParameter(InputType.Numeric, "每個週期最多等幾秒 MaxWaitSeconds", 1)]
        [SimpleNumeric(30D, 3600D, 0, 1)]
        public int MaxWaitSeconds = 300;

        private const string Version = "0.2";   // 0.2：各週期自己的起始日（增量抓取）

        // 一張請求單
        private class Request
        {
            public string Id, Symbol, ReqPath, Error;
            public List<string> Tfs = new List<string>();
            public DateTime FromUtc;
            public readonly Dictionary<string, DateTime> TfFrom = new Dictionary<string, DateTime>();   // 各週期自己的起始日（增量抓取）
            public int TfIndex;                                  // 目前處理到第幾個週期
            public Instrument Instrument;
            public readonly List<string> Lines = new List<string>();   // 每個週期一行結果
        }

        // 一個週期的平台請求
        private class Job
        {
            public string Tf;
            public Period Period;
            public int Value;
            public TimeHistoricalRequest Request;
            public HistoricalData Data;
            public bool Sent, NotFound;
            public string Error;
            public DateTime ReadyUtc, SentUtc, LastChangeUtc;
            public int LastCount = -1;
        }

        private readonly object sync = new object();
        private Request req;
        private Job job;
        private bool started;
        private DataType dataType = DataType.Trade;
        private string chartSymbol = "";
        private System.Threading.Timer timer;   // 休市沒有報價時 Update 不會被呼叫，靠計時器推進
        private int polling;
        private DateTime lastBeatUtc = DateTime.MinValue, lastShownUtc = DateTime.MinValue;
        private string dataRoot, bridgeRoot, reqDir, resDir;
        private int queued;
        private readonly List<string> recent = new List<string>();   // 最近幾張請求的摘要（顯示用）

        public HistoryBridge() : base()
        {
            Credentials.ProjectName = "HistoryBridge";
            Lines.Set("none");
            Lines["none"].Color = Color.Transparent;
            SeparateWindow = false;
        }

        public override void Init()
        {
            lock (sync)
            {
                req = null;
                job = null;
                started = false;
                recent.Clear();
            }
        }

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
                HistoricalRequest r = HistoryDataSeries.HistoricalRequest;
                if (r != null) dataType = r.DataType;
                var tr = r as TimeHistoricalRequest;
                if (tr != null && tr.Instrument != null) chartSymbol = tr.Instrument.Symbol ?? "";
            }
            catch { }
            string export = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export");
            dataRoot = Path.Combine(export, "HistoryData");
            bridgeRoot = Path.Combine(export, "Bridge");
            reqDir = Path.Combine(bridgeRoot, "requests");
            resDir = Path.Combine(bridgeRoot, "responses");
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(reqDir);
            Directory.CreateDirectory(resDir);
            HistoricalDataManager.OnLoaded -= OnLoaded;
            HistoricalDataManager.OnLoaded += OnLoaded;
            lock (sync)
            {
                if (timer == null) timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, 1000, 2000);
            }
            WriteHeartbeat();
        }

        // ------------------------------------------------------------ 平台請求

        // 送出目前週期的請求；Update 執行緒優先，10 秒沒 Update 才由計時器另開執行緒送（Get 在鎖外呼叫）
        private void SendPending(bool fromTimer)
        {
            Job j; Request r;
            lock (sync)
            {
                j = job; r = req;
                if (j == null || r == null || j.Sent) return;
                if (fromTimer && (DateTime.UtcNow - j.ReadyUtc).TotalSeconds < 10) return;
                j.Sent = true;
                j.SentUtc = DateTime.UtcNow;
                j.LastChangeUtc = j.SentUtc;
            }
            try
            {
                Instrument inst;
                lock (sync) inst = r.Instrument;
                if (inst == null)
                {
                    inst = Find(r.Symbol);
                    if (inst == null) { lock (sync) j.NotFound = true; return; }
                    lock (sync) r.Instrument = inst;
                }
                var request = new TimeHistoricalRequest(inst, dataType, j.Period, j.Value);
                lock (sync) j.Request = request;
                DateTime from;
                lock (sync) { if (!r.TfFrom.TryGetValue(j.Tf, out from)) from = r.FromUtc; }
                HistoricalData d = HistoricalDataManager.Get(request, new Interval(from, DateTime.UtcNow.AddDays(1)));
                lock (sync) { if (d != null && j.Data == null) j.Data = d; }
            }
            catch (Exception ex) { lock (sync) j.Error = ex.GetType().Name + " " + ex.Message; }
        }

        // 代號完全相同者優先，其次忽略大小寫與斜線
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
                    Job j = job;
                    if (j == null || !j.Sent || j.Request == null || tr == null || req == null || req.Instrument == null) return;
                    bool same = ReferenceEquals(tr, j.Request);
                    bool similar = !same && j.Data == null && tr.Period == j.Period && tr.Value == j.Value
                        && tr.Instrument != null && tr.Instrument.Symbol == req.Instrument.Symbol;
                    if (same || similar) { j.Data = d; j.LastCount = -1; j.LastChangeUtc = DateTime.UtcNow; }
                }
            }
            catch { }
        }

        private static bool ParseTf(string tf, out Period p, out int v)
        {
            p = Period.Minute; v = 1;
            switch (tf)
            {
                case "M1": return true;
                case "M5": v = 5; return true;
                case "M15": v = 15; return true;
                case "M30": v = 30; return true;
                case "H1": p = Period.Hour; v = 1; return true;
                case "H4": p = Period.Hour; v = 4; return true;
                case "D1": p = Period.Day; v = 1; return true;
                case "W1": p = Period.Week; v = 1; return true;
                case "MN1": p = Period.Month; v = 1; return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 主迴圈

        private void Poll()
        {
            if (System.Threading.Interlocked.Exchange(ref polling, 1) == 1) return;
            try { PollOnce(); }
            finally { System.Threading.Interlocked.Exchange(ref polling, 0); }
        }

        private void PollOnce()
        {
            DateTime now = DateTime.UtcNow;
            if (bridgeRoot == null) return;
            bool idle;
            lock (sync) idle = req == null;
            if (idle) TakeNextRequest();

            Job done = null;
            bool due = false, reqDone = false;
            Request r;
            lock (sync)
            {
                r = req;
                if (r != null)
                {
                    if (r.Error != null) reqDone = true;
                    else if (job == null)
                    {
                        if (r.TfIndex >= r.Tfs.Count) reqDone = true;
                        else
                        {
                            string tf = r.Tfs[r.TfIndex];
                            Period p; int v;
                            ParseTf(tf, out p, out v);
                            job = new Job { Tf = tf, Period = p, Value = v, ReadyUtc = now };
                        }
                    }
                    else if (!job.Sent) due = (now - job.ReadyUtc).TotalSeconds >= 10;
                    else if (job.NotFound || job.Error != null) { done = job; job = null; }
                    else
                    {
                        double waited = (now - job.SentUtc).TotalSeconds;
                        int n = -1;
                        if (job.Data != null) { try { n = job.Data.Count; } catch { n = -1; } }
                        if (n != job.LastCount) { job.LastCount = n; job.LastChangeUtc = now; }
                        bool stable = job.Data != null && (now - job.LastChangeUtc).TotalSeconds >= StableSeconds && waited >= StableSeconds * 2;
                        if (stable || waited >= MaxWaitSeconds) { done = job; job = null; }
                    }
                }
            }
            if (due) new System.Threading.Thread(() => { try { SendPending(true); } catch { } }) { IsBackground = true }.Start();
            if (done != null) FinishTf(r, done);
            if (reqDone) FinishRequest(r);
            else if (r != null) WriteStatus(r);
            if ((now - lastBeatUtc).TotalSeconds >= 5) WriteHeartbeat();
            ShowProgress();
        }

        // 取最早的一張請求單；格式錯的直接回 error
        private void TakeNextRequest()
        {
            string[] files;
            try { files = Directory.GetFiles(reqDir, "*.req"); }
            catch { return; }
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0) return;
            string path = files[0];
            var r = new Request { ReqPath = path, Id = Path.GetFileNameWithoutExtension(path), FromUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim();
                    if (k == "id" && v.Length > 0) r.Id = v;
                    else if (k == "symbol") r.Symbol = v.ToUpperInvariant();
                    else if (k == "timeframes")
                    {
                        foreach (string t in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string tf = t.Trim().ToUpperInvariant();
                            Period p; int n;
                            if (!ParseTf(tf, out p, out n)) { r.Error = "不支援的週期 " + tf; break; }
                            if (!r.Tfs.Contains(tf)) r.Tfs.Add(tf);
                        }
                    }
                    else if (k == "from" && v.Length > 0)
                    {
                        DateTime f;
                        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out f))
                            r.FromUtc = DateTime.SpecifyKind(f, DateTimeKind.Utc);
                        else r.Error = "起始日格式錯誤 " + v;
                    }
                    else if (k.StartsWith("from.", StringComparison.Ordinal) && v.Length > 0)
                    {
                        DateTime f;
                        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out f))
                            r.TfFrom[k.Substring(5).ToUpperInvariant()] = DateTime.SpecifyKind(f, DateTimeKind.Utc);
                        else r.Error = "起始日格式錯誤 " + v;
                    }
                }
            }
            catch (IOException) { return; }   // 回測器還在寫，下一輪再讀
            catch (Exception ex) { r.Error = "讀請求單失敗 " + ex.Message; }
            if (r.Error == null && string.IsNullOrEmpty(r.Symbol)) r.Error = "請求單沒有商品代號";
            if (r.Error == null && r.Tfs.Count == 0) r.Error = "請求單沒有週期";
            lock (sync) { req = r; job = null; }
        }

        // 一個週期完成：合併寫檔、記一行結果、釋放資料
        private void FinishTf(Request r, Job j)
        {
            string line;
            if (j.NotFound)
            {
                lock (sync) r.Error = "平台找不到商品 " + r.Symbol;
                return;
            }
            if (j.Error != null) line = Row(j.Tf, 0, null, null, 0, -1, "請求失敗 " + j.Error);
            else if (j.Data == null) line = Row(j.Tf, 0, null, null, 0, -1, "平台沒回應（等了 " + MaxWaitSeconds + " 秒）");
            else
            {
                try
                {
                    string folder = Path.Combine(dataRoot, Safe(r.Instrument.Symbol ?? r.Symbol));
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, j.Tf + ".csv");
                    var store = Load(file);
                    int got = 0, added = 0;
                    DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
                    HistoricalData d = j.Data;
                    int n = d.Count;
                    // 索引 0 是進行中的 K 棒，不收
                    for (int i = n - 1; i >= 1; i--)
                    {
                        DateTime t = d.GetTimeUtc(i);
                        double o = d.GetValue(PriceType.Open, i), h = d.GetValue(PriceType.High, i);
                        double l = d.GetValue(PriceType.Low, i), c = d.GetValue(PriceType.Close, i);
                        if (double.IsNaN(o) || double.IsNaN(h) || double.IsNaN(l) || double.IsNaN(c)) continue;
                        got++;
                        if (t < first) first = t;
                        if (t > last) last = t;
                        if (!store.ContainsKey(t)) added++;
                        store[t] = new[] { o, h, l, c };
                    }
                    if (got > 0) Save(file, store);
                    line = got == 0 ? Row(j.Tf, 0, null, null, 0, store.Count, "平台沒有資料")
                                    : Row(j.Tf, got, first, last, added, store.Count, "");
                }
                catch (Exception ex) { line = Row(j.Tf, 0, null, null, 0, -1, "寫檔失敗 " + ex.Message); }
                // 圖表自己的商品不釋放，免得影響圖表
                try { if (!string.Equals(r.Instrument.Symbol, chartSymbol, StringComparison.Ordinal)) HistoricalDataManager.Remove(j.Data); } catch { }
            }
            lock (sync) { r.Lines.Add(line); r.TfIndex++; }
        }

        private static string Row(string tf, int got, DateTime? first, DateTime? last, int added, int total, string msg)
        {
            return "tf=" + tf + "|got=" + got
                + "|first=" + (first.HasValue ? first.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")
                + "|last=" + (last.HasValue ? last.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")
                + "|added=" + added + "|total=" + (total < 0 ? "" : total.ToString(CultureInfo.InvariantCulture))
                + "|msg=" + (msg ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        // 整張請求完成：寫 symbol_info、結果檔，刪請求單
        private void FinishRequest(Request r)
        {
            try
            {
                if (r.Error == null && r.Instrument != null)
                {
                    string folder = Path.Combine(dataRoot, Safe(r.Instrument.Symbol ?? r.Symbol));
                    Directory.CreateDirectory(folder);
                    WriteSymbolInfo(r.Instrument, folder);
                }
                var sb = new StringBuilder();
                sb.Append("id=").Append(r.Id).Append("\r\n");
                sb.Append("symbol=").Append(r.Symbol ?? "").Append("\r\n");
                sb.Append("state=").Append(r.Error == null ? "done" : "error").Append("\r\n");
                sb.Append("msg=").Append(r.Error ?? "").Append("\r\n");
                sb.Append("finished=").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                foreach (string l in r.Lines) sb.Append(l).Append("\r\n");
                // 回測器已取消（請求單被刪）就不寫結果，免得留下沒人讀的檔；CSV 照樣已寫入
                if (File.Exists(r.ReqPath)) WriteAtomic(Path.Combine(resDir, Safe(r.Id) + ".res"), sb.ToString());
                try { File.Delete(Path.Combine(resDir, Safe(r.Id) + ".status")); } catch { }
            }
            catch { }
            try { File.Delete(r.ReqPath); } catch { }
            lock (sync)
            {
                recent.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + (r.Symbol ?? "?") + " "
                    + (r.Error ?? (string.Join(",", r.Tfs) + " 完成")));
                while (recent.Count > 5) recent.RemoveAt(0);
                req = null;
                job = null;
            }
            WriteHeartbeat();
        }

        // ------------------------------------------------------------ 檔案

        private void WriteStatus(Request r)
        {
            try
            {
                var sb = new StringBuilder();
                lock (sync)
                {
                    sb.Append("id=").Append(r.Id).Append("\r\n");
                    sb.Append("symbol=").Append(r.Symbol ?? "").Append("\r\n");
                    sb.Append("index=").Append(Math.Min(r.TfIndex + 1, r.Tfs.Count)).Append("\r\n");
                    sb.Append("count=").Append(r.Tfs.Count).Append("\r\n");
                    if (job != null)
                    {
                        sb.Append("tf=").Append(job.Tf).Append("\r\n");
                        sb.Append("waited=").Append(job.Sent ? (int)(DateTime.UtcNow - job.SentUtc).TotalSeconds : 0).Append("\r\n");
                        sb.Append("bars=").Append(job.LastCount < 0 ? 0 : job.LastCount).Append("\r\n");
                    }
                }
                WriteAtomic(Path.Combine(resDir, Safe(r.Id) + ".status"), sb.ToString());
            }
            catch { }
        }

        private void WriteHeartbeat()
        {
            try
            {
                lastBeatUtc = DateTime.UtcNow;
                // 排隊張數現場算，不含正在處理的那張
                int files = 0;
                try { files = Directory.GetFiles(reqDir, "*.req").Length; } catch { }
                lock (sync) queued = Math.Max(0, files - (req != null && File.Exists(req.ReqPath) ? 1 : 0));
                var sb = new StringBuilder();
                lock (sync)
                {
                    sb.Append("utc=").Append(lastBeatUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                    sb.Append("version=").Append(Version).Append("\r\n");
                    sb.Append("chart=").Append(chartSymbol).Append("\r\n");
                    sb.Append("busy=").Append(req == null ? "" : req.Id).Append("\r\n");
                    sb.Append("queue=").Append(queued).Append("\r\n");
                    sb.Append("data=").Append(dataRoot).Append("\r\n");
                }
                WriteAtomic(Path.Combine(bridgeRoot, "heartbeat.txt"), sb.ToString());
            }
            catch { }
        }

        // 寫暫存檔再換名，對方不會讀到寫一半的檔
        private static void WriteAtomic(string target, string text)
        {
            string temp = target + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }

        private static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static SortedDictionary<DateTime, double[]> Load(string path)
        {
            var store = new SortedDictionary<DateTime, double[]>();
            if (!File.Exists(path)) return store;
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || char.IsLetter(raw[0])) continue;
                string[] p = raw.Split(',');
                if (p.Length < 5) continue;
                DateTime t;
                if (!DateTime.TryParseExact(p[0].Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) continue;
                double o, h, l, c;
                if (!Num(p[1], out o) || !Num(p[2], out h) || !Num(p[3], out l) || !Num(p[4], out c)) continue;
                store[t] = new[] { o, h, l, c };
            }
            return store;
        }

        private static void Save(string target, SortedDictionary<DateTime, double[]> store)
        {
            string temp = target + ".tmp";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false), 1 << 16))
            {
                w.Write("time_utc,open,high,low,close\r\n");
                foreach (var kv in store)
                {
                    double[] v = kv.Value;
                    w.Write(kv.Key.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    w.Write(','); w.Write(v[0].ToString("R", CultureInfo.InvariantCulture));
                    w.Write(','); w.Write(v[1].ToString("R", CultureInfo.InvariantCulture));
                    w.Write(','); w.Write(v[2].ToString("R", CultureInfo.InvariantCulture));
                    w.Write(','); w.Write(v[3].ToString("R", CultureInfo.InvariantCulture));
                    w.Write("\r\n");
                }
            }
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        }

        private static bool Num(string s, out double v) { return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        // 格式同 HistoryExporter，回測器讀合約大小、跳動點、點差用；每次覆寫以更新點差
        private void WriteSymbolInfo(Instrument inst, string folder)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("Symbol=").Append(inst.Symbol).Append("\r\n");
                sb.Append("DataType=").Append(dataType).Append("\r\n");
                sb.Append("ExportedUtc=").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                try { sb.Append("InstrumentType=").Append(inst.Type).Append("\r\n"); } catch { }
                try { sb.Append("MinimalTickSize=").Append(inst.MinimalTickSize.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n"); } catch { }
                try { sb.Append("LotStep=").Append(inst.LotStep.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n"); } catch { }
                try { sb.Append("MinimalLot=").Append(inst.MinimalLot(TradeApi.Trading.ProductType.General).ToString("R", CultureInfo.InvariantCulture)).Append("\r\n"); } catch { }
                try { sb.Append("MaximalLot=").Append(inst.MaximalLot(TradeApi.Trading.ProductType.General).ToString("R", CultureInfo.InvariantCulture)).Append("\r\n"); } catch { }
                try
                {
                    if (inst.DayInfo != null)
                    {
                        sb.Append("Ask=").Append(inst.DayInfo.Ask.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
                        sb.Append("Bid=").Append(inst.DayInfo.Bid.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
                    }
                }
                catch { }
                sb.Append("[SymbolInfo]\r\n");
                try
                {
                    foreach (var kv in inst.GetSymbolInfo())
                        sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
                }
                catch (Exception ex) { sb.Append("# GetSymbolInfo 失敗 ").Append(ex.Message).Append("\r\n"); }
                string target = Path.Combine(folder, "symbol_info.txt"), temp = target + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(true));
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
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
                    sb.Append("HistoryBridge v").Append(Version).Append("：資料橋運作中，請保持圖表開著\n");
                    if (req == null) sb.Append("閒置，等回測器的請求").Append('\n');
                    else
                    {
                        sb.Append("處理 ").Append(req.Symbol ?? "?").Append(" 第 ").Append(Math.Min(req.TfIndex + 1, req.Tfs.Count)).Append(" / ").Append(req.Tfs.Count).Append(" 個週期");
                        if (job != null)
                            sb.Append("（").Append(job.Tf).Append(job.Sent
                                ? "，等平台 " + (int)(DateTime.UtcNow - job.SentUtc).TotalSeconds + " 秒" + (job.LastCount >= 0 ? "，" + job.LastCount + " 根" : "")
                                : "，準備送出").Append("）");
                        sb.Append('\n');
                    }
                    foreach (string l in recent) sb.Append(l).Append('\n');
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
                job = null;
                req = null;
                started = false;
            }
        }
    }
}
