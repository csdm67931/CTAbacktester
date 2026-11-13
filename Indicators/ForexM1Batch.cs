using System;
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
// ForexM1Batch 外匯 M1 批次匯出 v0.1（規格見 文件\群益交易王CTA\Backtester\spec.md 3.3）
// 掛在任一圖表一次：依 Symbols 清單逐一向平台要 M1（預設最近 95 天，群益伺服器的 M1 只保留約三個月），
// 和 文件\群益交易王CTA\Export\HistoryData\<商品>\M1.csv 合併（同一時間以平台為準、只增不減）。
// 一次只處理一個商品。記錄寫在 Export\HistoryData\_forex_m1_batch_log.txt。不畫線、不下單。
// ============================================================================

namespace ForexM1BatchIndicator
{
    public class ForexM1Batch : IndicatorBuilder
    {
        [InputParameter(InputType.String, "商品代號 Symbols（逗號分隔）", 0)]
        public string Symbols = "AUDCAD,AUDJPY,AUDNZD,AUDUSD,CADJPY,EURAUD,EURCAD,EURCHF,EURGBP,EURJPY,EURNZD,EURUSD,EURZAR,GBPAUD,GBPCAD,GBPCHF,GBPJPY,GBPNZD,GBPUSD,NZDCAD,NZDJPY,NZDUSD,USDCAD,USDCHF,USDCNH,USDJPY,USDMXN,USDSEK,USDSGD,USDZAR";

        [InputParameter(InputType.Numeric, "往前要幾天 Days", 1)]
        [SimpleNumeric(1D, 3650D, 0, 1)]
        public int Days = 95;

        [InputParameter(InputType.Numeric, "筆數幾秒不變視為載完 StableSeconds", 2)]
        [SimpleNumeric(2D, 600D, 0, 1)]
        public int StableSeconds = 15;

        [InputParameter(InputType.Numeric, "每個商品最多等幾秒 MaxWaitSeconds", 3)]
        [SimpleNumeric(30D, 3600D, 0, 1)]
        public int MaxWaitSeconds = 180;

        private class Job
        {
            public string Symbol;
            public Instrument Instrument;
            public TimeHistoricalRequest Request;
            public HistoricalData Data;
            public bool Sent, NotFound;
            public string Error;
            public DateTime ReadyUtc, SentUtc, LastChangeUtc;
            public int LastCount = -1;
        }

        private readonly object sync = new object();
        private readonly List<string> symbols = new List<string>();
        private readonly List<string> results = new List<string>();
        private int next;                       // 下一個要處理的 symbols 索引
        private Job current;
        private bool started, finished;
        private DataType dataType = DataType.Trade;
        private System.Threading.Timer timer;   // 休市沒有報價時 Update 不會被呼叫，靠計時器推進
        private int polling;
        private DateTime lastShownUtc = DateTime.MinValue;
        private string root;

        public ForexM1Batch() : base()
        {
            Credentials.ProjectName = "ForexM1Batch";
            Lines.Set("none");
            Lines["none"].Color = Color.Transparent;
            SeparateWindow = false;
        }

        public override void Init()
        {
            lock (sync)
            {
                symbols.Clear();
                results.Clear();
                next = 0;
                current = null;
                started = false;
                finished = false;
                foreach (string raw in (Symbols ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string s = raw.Trim().ToUpperInvariant();
                    if (s.Length > 0 && !symbols.Contains(s)) symbols.Add(s);
                }
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
                HistoricalRequest req = HistoryDataSeries.HistoricalRequest;
                if (req != null) dataType = req.DataType;
            }
            catch { }
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export", "HistoryData");
            Directory.CreateDirectory(root);
            HistoricalDataManager.OnLoaded -= OnLoaded;
            HistoricalDataManager.OnLoaded += OnLoaded;
            lock (sync)
            {
                results.Add("ForexM1Batch 開始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "，" + symbols.Count + " 個商品，往前 " + Days + " 天，DataType " + dataType);
                if (timer == null) timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, 3000, 3000);
            }
            WriteLog();
        }

        // 送出目前商品的請求；Update 執行緒優先，太久沒 Update 才由計時器另開執行緒送（Get 在鎖外呼叫）
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
                var request = new TimeHistoricalRequest(inst, dataType, Period.Minute, 1);
                lock (sync) { j.Instrument = inst; j.Request = request; }
                var interval = new Interval(DateTime.UtcNow.AddDays(-Days), DateTime.UtcNow.AddDays(1));
                HistoricalData d = HistoricalDataManager.Get(request, interval);
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
                    Job j = current;
                    if (j == null || !j.Sent || j.Request == null || tr == null) return;
                    bool same = ReferenceEquals(tr, j.Request);
                    bool similar = !same && j.Data == null && tr.Period == Period.Minute && tr.Value == 1
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
                    if (next >= symbols.Count)
                    {
                        finished = true;
                        results.Add("全部完成 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    }
                    else current = new Job { Symbol = symbols[next++], ReadyUtc = now };
                }
                else if (!current.Sent) due = (now - current.ReadyUtc).TotalSeconds >= 20;
                else if (current.NotFound || current.Error != null) { done = current; current = null; }
                else
                {
                    double waited = (now - current.SentUtc).TotalSeconds;
                    int n = -1;
                    if (current.Data != null) { try { n = current.Data.Count; } catch { n = -1; } }
                    if (n != current.LastCount) { current.LastCount = n; current.LastChangeUtc = now; }
                    bool stable = current.Data != null && (now - current.LastChangeUtc).TotalSeconds >= StableSeconds && waited >= StableSeconds * 2;
                    if (stable || waited >= MaxWaitSeconds) { done = current; current = null; }
                }
            }
            if (due) new System.Threading.Thread(() => { try { SendPending(true); } catch { } }) { IsBackground = true }.Start();
            if (done != null) Finish(done);
            if (finished || done != null) WriteLog();
            ShowProgress();
        }

        // 一個商品完成：合併寫檔、記一行結果
        private void Finish(Job j)
        {
            string line;
            if (j.NotFound) line = j.Symbol + "：平台找不到這個商品";
            else if (j.Error != null) line = j.Symbol + "：請求失敗 " + j.Error;
            else if (j.Data == null) line = j.Symbol + "：平台沒回應（等了 " + MaxWaitSeconds + " 秒）";
            else
            {
                try
                {
                    string folder = Path.Combine(root, Safe(j.Instrument.Symbol ?? j.Symbol));
                    Directory.CreateDirectory(folder);
                    if (!File.Exists(Path.Combine(folder, "symbol_info.txt"))) WriteSymbolInfo(j.Instrument, folder);
                    var store = Load(Path.Combine(folder, "M1.csv"));
                    int before = store.Count, got = 0, added = 0;
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
                    if (got > 0) Save(Path.Combine(folder, "M1.csv"), store);
                    line = got == 0
                        ? j.Symbol + "：平台沒有資料（0 根）"
                        : string.Format(CultureInfo.InvariantCulture, "{0}：平台給 {1} 根，{2:yyyy-MM-dd HH:mm} 到 {3:yyyy-MM-dd HH:mm}，新增 {4} 根，合併後 {5} 根（原 {6} 根）",
                            j.Symbol, got, first, last, added, store.Count, before);
                }
                catch (Exception ex) { line = j.Symbol + "：寫檔失敗 " + ex.Message; }
            }
            lock (sync) results.Add(line);
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

        // 逐行寫暫存檔再換名，回測器不會讀到寫一半的檔
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

        // 格式同 HistoryExporter，回測器讀合約大小、跳動點、點差用
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
                File.WriteAllText(Path.Combine(folder, "symbol_info.txt"), sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        private void WriteLog()
        {
            try
            {
                var sb = new StringBuilder();
                lock (sync) foreach (string l in results) sb.Append(l).Append("\r\n");
                if (root != null) File.WriteAllText(Path.Combine(root, "_forex_m1_batch_log.txt"), sb.ToString(), new UTF8Encoding(true));
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
                    sb.Append("ForexM1Batch：").Append(finished ? symbols.Count : doneCount).Append(" / ").Append(symbols.Count)
                      .Append(finished ? "，全部完成，可以移除指標" : "，請保持圖表開著").Append('\n');
                    if (current != null)
                        sb.Append("目前 ").Append(current.Symbol).Append(current.Sent
                            ? "，等平台回應 " + (int)(DateTime.UtcNow - current.SentUtc).TotalSeconds + " 秒" + (current.LastCount >= 0 ? "（" + current.LastCount + " 根）" : "")
                            : "，準備送出").Append('\n');
                    // 最近 5 筆結果
                    for (int i = Math.Max(1, results.Count - 5); i < results.Count; i++) sb.Append(results[i]).Append('\n');
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
