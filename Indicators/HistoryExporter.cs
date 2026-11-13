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
// HistoryExporter 歷史資料匯出 v0.2（給自製回測器用，規格見 文件\群益交易王CTA\Backtester\spec.md 第 3 節）
// 掛在任一圖表：對該商品向平台要 M1 到月線，起點預設 2000-01-01（等於要平台給最大範圍），
// 載完後存成 文件\群益交易王CTA\Export\HistoryData\<商品>\<週期>.csv（時間為 K 棒開盤時間 UTC）。
// 另存 symbol_info.txt（商品規格、點差）與 export_log.txt（每個週期拿到幾根、從哪天開始、補了哪些段）。
// 不畫線、不下單。圖表左上角（Comment）顯示進度。
// 判斷「載完」：平台送來 OnLoaded，或連續兩次檢查筆數不變且間隔 StableSeconds 秒以上。
//
// v0.2（spec 3.1）：
// 1. 寫檔改成和資料夾裡現有的 CSV 合併（同一時間以平台這次為準），重新匯出不會讓資料變少。
// 2. 主資料存好後，M1～H4 對「中間缺口」與「比日線晚開始的前段」一段一段再向平台要，補進檔案。
// ============================================================================

namespace HistoryExporterIndicator
{
    public class HistoryExporter : IndicatorBuilder
    {
        [InputParameter(InputType.Numeric, "起始年份 StartYear（越早越接近平台最大範圍）", 0)]
        [SimpleNumeric(1990D, 2100D, 0, 1)]
        public int StartYear = 2000;

        [InputParameter(InputType.Checkbox, "匯出 M1", 1)] public bool ExportM1 = true;
        [InputParameter(InputType.Checkbox, "匯出 M5", 2)] public bool ExportM5 = true;
        [InputParameter(InputType.Checkbox, "匯出 M15", 3)] public bool ExportM15 = true;
        [InputParameter(InputType.Checkbox, "匯出 M30", 4)] public bool ExportM30 = true;
        [InputParameter(InputType.Checkbox, "匯出 H1", 5)] public bool ExportH1 = true;
        [InputParameter(InputType.Checkbox, "匯出 H4", 6)] public bool ExportH4 = true;
        [InputParameter(InputType.Checkbox, "匯出 D1", 7)] public bool ExportD1 = true;
        [InputParameter(InputType.Checkbox, "匯出 W1", 8)] public bool ExportW1 = true;
        [InputParameter(InputType.Checkbox, "匯出 MN1", 9)] public bool ExportMN1 = true;

        [InputParameter(InputType.Numeric, "筆數幾秒不變視為載完 StableSeconds", 10)]
        [SimpleNumeric(2D, 600D, 0, 1)]
        public int StableSeconds = 15;

        [InputParameter(InputType.Checkbox, "補缺口 BackfillGaps（主資料存好後，分段再向平台要缺的那幾段）", 11)]
        public bool BackfillGaps = true;

        [InputParameter(InputType.Numeric, "相隔幾天以上算缺口 GapDays（週末假日不到 4 天）", 12)]
        [SimpleNumeric(2D, 60D, 0, 1)]
        public int GapDays = 5;

        [InputParameter(InputType.Numeric, "每個週期最多補幾段 MaxBackfillChunks", 13)]
        [SimpleNumeric(1D, 1000D, 0, 1)]
        public int MaxBackfillChunks = 60;

        private class Slot
        {
            public string Name;          // 檔名用：M1、H4、D1...
            public Period Period;
            public int Value;
            public TimeHistoricalRequest Request;
            public HistoricalData Data;
            public int LastCount = -1;
            public DateTime LastChangeUtc;
            public int WrittenCount = -1;
            public DateTime OldestWritten = DateTime.MaxValue;   // 上次寫入時平台資料最舊那根的時間
            public string Status = "等待";
            // 合併後的完整資料（時間 -> 開高低收）；第一次寫檔時從現有 CSV 載入
            public SortedDictionary<DateTime, double[]> Store;
            // 補缺口
            public bool Intraday;
            public bool Planned;                                   // 已排好要補的段
            public readonly Queue<Chunk> Chunks = new Queue<Chunk>();
            public int ChunksTotal, ChunksDone, AddedTotal, HeadEmptyRun;
            public string BackfillStatus = "";
            public bool Dirty;            // Store 有新資料還沒存成檔（補段累積或上次存檔失敗）
            public int UnsavedChunks;     // 累積幾段沒存；每 5 段或補完才存一次，避免 100 萬根每段整檔重寫
        }

        private class Chunk
        {
            public Slot Slot;
            public DateTime From, To;
            public bool Head;            // 前段（往更舊要）；連續 3 段沒新資料就停
            public TimeHistoricalRequest Request;
            public HistoricalData Data;
            public bool Sent;
            public DateTime ReadyUtc;    // 排進來的時間（等 Update 送；太久沒 Update 改由計時器送）
            public DateTime SentUtc, LastChangeUtc;
            public int LastCount = -1;
        }

        private readonly object sync = new object();
        private readonly List<Slot> slots = new List<Slot>();
        private bool requested;
        private string symbol = "UNKNOWN";
        private string folder;
        private Instrument instrument;
        private DataType dataType = DataType.Trade;
        private System.Threading.Timer timer;   // 休市沒有報價時 Update 不會被呼叫，靠計時器每 5 秒檢查一次
        private Chunk active;                   // 目前在等平台回應的那一段（一次只送一段）
        private readonly HashSet<object> chunkRequests = new HashSet<object>(new RefEq());   // 送過的補段請求：晚到的回呼直接丟掉，不蓋主資料

        // 只認同一個物件：平台的請求類別若改寫 Equals，「相似」的主請求不會被誤認成補段請求
        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        private readonly List<string> backfillLog = new List<string>();

        public HistoryExporter() : base()
        {
            Credentials.ProjectName = "HistoryExporter";
            Lines.Set("none");          // 不畫線，只為了讓平台當成正常指標
            Lines["none"].Color = Color.Transparent;
            SeparateWindow = false;
        }

        public override void Init()
        {
            lock (sync)
            {
                slots.Clear();
                requested = false;
                active = null;
                backfillLog.Clear();
                chunkRequests.Clear();
                Add(ExportM1, "M1", Period.Minute, 1);
                Add(ExportM5, "M5", Period.Minute, 5);
                Add(ExportM15, "M15", Period.Minute, 15);
                Add(ExportM30, "M30", Period.Minute, 30);
                Add(ExportH1, "H1", Period.Hour, 1);
                Add(ExportH4, "H4", Period.Hour, 4);
                Add(ExportD1, "D1", Period.Day, 1);
                Add(ExportW1, "W1", Period.Week, 1);
                Add(ExportMN1, "MN1", Period.Month, 1);
            }
        }

        private void Add(bool on, string name, Period p, int v)
        {
            if (on) slots.Add(new Slot { Name = name, Period = p, Value = v, Intraday = p == Period.Minute || p == Period.Hour });
        }

        public override void Update(TickStatus args)
        {
            try
            {
                bool needRequest;
                lock (sync) needRequest = !requested;
                if (needRequest) Request();
                // 補缺口的請求優先在平台的 Update 執行緒送
                SendPendingChunk();
                // 寫檔交給計時器執行緒，不在平台的 Update 執行緒做大檔寫入；這裡只更新畫面，每秒最多一次
                DateTime now = DateTime.UtcNow;
                if ((now - lastShownUtc).TotalSeconds >= 1) { lastShownUtc = now; ShowProgress(); }
            }
            catch { }
        }

        private DateTime lastShownUtc = DateTime.MinValue;
        private readonly object writeLock = new object();

        // 要不要（重）寫：沒寫過、往更舊的歷史延伸了，或尾端新增超過 max(100, 1%) 根
        // 只有尾端（即時新 K 棒）增加才套門檻，避免掛著不移除時每根新 K 棒就整檔重寫
        private static bool NeedWrite(Slot s, HistoricalData d, int n)
        {
            if (s.WrittenCount <= 0) return true;
            try { if (d.GetTimeUtc(n - 1) < s.OldestWritten) return true; } catch { }
            return n - s.WrittenCount > Math.Max(100, s.WrittenCount / 100);
        }

        // 在鎖外呼叫 Get：平台的 Get 可能等載入執行緒，而 OnLoaded 要拿同一把鎖（Part7PullbackSignals 的經驗）
        private void Request()
        {
            Instrument inst = null;
            try
            {
                HistoricalRequest req = HistoryDataSeries.HistoricalRequest;
                if (req != null) { inst = req.Instrument; dataType = req.DataType; }
            }
            catch { }
            if (inst == null) inst = InstrumentsManager.Current;
            if (inst == null) return;   // 商品還沒就緒，下次 Update 再試

            string sym = inst.Symbol ?? "UNKNOWN";
            foreach (char c in Path.GetInvalidFileNameChars()) sym = sym.Replace(c, '_');
            List<Slot> copy;
            lock (sync)
            {
                requested = true;
                symbol = sym;
                instrument = inst;
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                      "群益交易王CTA", "Export", "HistoryData", symbol);
                copy = new List<Slot>(slots);
            }
            Directory.CreateDirectory(folder);
            WriteSymbolInfo(inst);

            HistoricalDataManager.OnLoaded -= OnLoaded;
            HistoricalDataManager.OnLoaded += OnLoaded;
            var interval = new Interval(new DateTime(StartYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.AddDays(1));
            foreach (Slot s in copy)
            {
                try
                {
                    var request = new TimeHistoricalRequest(inst, dataType, s.Period, s.Value);
                    lock (sync) { s.Request = request; s.Status = "載入中"; s.LastChangeUtc = DateTime.UtcNow; }
                    HistoricalData d = HistoricalDataManager.Get(request, interval);
                    lock (sync) { if (d != null) s.Data = d; }
                }
                catch (Exception ex) { lock (sync) s.Status = "失敗 " + ex.GetType().Name + " " + ex.Message; }
            }
            WriteLog();
            lock (sync)
            {
                if (timer == null)
                    timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, 5000, 5000);
            }
        }

        // 平台載完資料時呼叫（可能在別的執行緒，也可能是別張圖的資料）
        private void OnLoaded(HistoricalData d)
        {
            try
            {
                if (d == null) return;
                var tr = d.HistoricalRequest as TimeHistoricalRequest;
                lock (sync)
                {
                    // 補缺口那一段的請求：目前那段就收下；之前送過的（晚到）直接丟掉
                    if (tr != null && chunkRequests.Contains(tr))
                    {
                        if (active != null && ReferenceEquals(tr, active.Request)) { active.Data = d; active.LastCount = -1; active.LastChangeUtc = DateTime.UtcNow; }
                        return;
                    }
                    foreach (Slot s in slots)
                    {
                        if (s.Request == null) continue;
                        bool same = ReferenceEquals(tr, s.Request);
                        bool similar = !same && tr != null && tr.Period == s.Request.Period && tr.Value == s.Request.Value
                                && tr.DataType == s.Request.DataType && tr.Instrument != null && s.Request.Instrument != null
                                && tr.Instrument.Symbol == s.Request.Instrument.Symbol;
                        // 正在補這個週期時，同週期的「相似」資料可能是補的那一段：那段還沒拿到資料就給它，不拿來蓋主資料
                        if (similar && active != null && active.Slot == s)
                        {
                            if (active.Sent && active.Data == null) { active.Data = d; active.LastCount = -1; active.LastChangeUtc = DateTime.UtcNow; }
                            return;
                        }
                        // 只記下資料並重設穩定計時；寫檔一律交給計時器，不在平台回呼的執行緒寫大檔
                        if (same || similar) { s.Data = d; s.LastCount = -1; s.LastChangeUtc = DateTime.UtcNow; break; }
                    }
                }
            }
            catch { }
        }

        // 計時器每 5 秒：主資料筆數超過 StableSeconds 秒沒變就寫檔；再推進補缺口
        // 計時器上一輪還沒做完（寫大檔）時下一輪直接跳過：所有寫檔與補缺口都只在這裡、一次一輪
        private int polling;

        private void Poll()
        {
            if (System.Threading.Interlocked.Exchange(ref polling, 1) == 1) return;
            try { PollOnce(); }
            finally { System.Threading.Interlocked.Exchange(ref polling, 0); }
        }

        private void PollOnce()
        {
            var ready = new List<Slot>();
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                foreach (Slot s in slots)
                {
                    if (s.Data == null) continue;
                    int n;
                    try { n = s.Data.Count; } catch { continue; }
                    if (n != s.LastCount) { s.LastCount = n; s.LastChangeUtc = now; continue; }
                    if (n > 1 && NeedWrite(s, s.Data, n) && (now - s.LastChangeUtc).TotalSeconds >= StableSeconds) ready.Add(s);
                }
            }
            foreach (Slot s in ready) WriteCsv(s, "筆數穩定");
            SaveDirty();
            if (BackfillGaps) StepBackfill(now);
            ShowProgress();
        }

        // ====================================================================
        // 合併寫檔
        // ====================================================================
        private void WriteCsv(Slot s, string reason)
        {
            // 計時器回呼可能重疊；正在寫就略過，下一輪再判斷
            if (!System.Threading.Monitor.TryEnter(writeLock)) return;
            try
            {
                HistoricalData d;
                lock (sync) d = s.Data;
                if (d == null) return;
                int n = d.Count;
                bool need;
                lock (sync) need = NeedWrite(s, d, n);
                if (n < 2 || !need) return;

                EnsureStore(s);
                DateTime pfirst;
                Merge(s, d, DateTime.MinValue, out pfirst);
                SaveStore(s);
                lock (sync)
                {
                    s.WrittenCount = n;
                    if (pfirst != DateTime.MaxValue) s.OldestWritten = pfirst;
                    s.Status = StoreText(s) + "（" + reason + (s.AddedTotal > 0 ? "，補缺口新增 " + s.AddedTotal + " 根" : "") + "）";
                }
                WriteLog();
            }
            catch (Exception ex) { lock (sync) s.Status = "寫檔失敗，5 秒後重試（若回測器正開著這個檔，等它讀完即可）：" + ex.Message; if (s.Store != null) s.Dirty = true; }
            finally { System.Threading.Monitor.Exit(writeLock); }
        }

        // 上次存檔失敗、或補段累積還沒存的：這個週期沒有在補時重存
        private void SaveDirty()
        {
            List<Slot> copy;
            lock (sync) copy = new List<Slot>(slots);
            foreach (Slot s in copy)
            {
                bool idle;
                lock (sync) idle = s.Dirty && s.Store != null && s.Chunks.Count == 0 && (active == null || active.Slot != s);
                if (!idle) continue;
                try { lock (writeLock) SaveStore(s); }
                catch (Exception ex) { lock (sync) s.Status = StoreText(s) + "（存檔失敗，5 秒後重試：" + ex.Message + "）"; }
            }
        }

        // 第一次寫檔前，把資料夾裡現有的 CSV 讀進來（之前匯出過的資料不會因這次平台給得比較少而消失）
        private void EnsureStore(Slot s)
        {
            if (s.Store != null) return;
            var store = new SortedDictionary<DateTime, double[]>();
            string f;
            lock (sync) f = folder;
            string path = Path.Combine(f, s.Name + ".csv");
            if (File.Exists(path))
            {
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
            }
            s.Store = store;
        }

        // 平台資料疊進 Store；回傳新增幾根（原本沒有的時間）
        // offset 0：主資料是進行中的 K 棒，不收；補缺口那段是過去的資料，開盤時間早於 newestBefore 就收
        private static int Merge(Slot s, HistoricalData d, DateTime newestBefore, out DateTime oldest)
        {
            oldest = DateTime.MaxValue;
            int n = d.Count, added = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                DateTime t = d.GetTimeUtc(i);
                if (i == 0 && !(t < newestBefore)) continue;
                double o = d.GetValue(PriceType.Open, i), h = d.GetValue(PriceType.High, i);
                double l = d.GetValue(PriceType.Low, i), c = d.GetValue(PriceType.Close, i);
                if (double.IsNaN(o) || double.IsNaN(h) || double.IsNaN(l) || double.IsNaN(c)) continue;
                if (t < oldest) oldest = t;
                if (!s.Store.ContainsKey(t)) added++;
                s.Store[t] = new[] { o, h, l, c };
            }
            return added;
        }

        // 逐行寫暫存檔（不先組成一整個字串：M1 100 萬根會多佔近 200 MB），寫完再換名，回測器不會讀到寫一半的檔
        private void SaveStore(Slot s)
        {
            string f;
            lock (sync) f = folder;
            string target = Path.Combine(f, s.Name + ".csv"), temp = target + ".tmp";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false), 1 << 16))
            {
                w.Write("time_utc,open,high,low,close\r\n");
                foreach (var kv in s.Store)
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
            s.Dirty = false;
            s.UnsavedChunks = 0;
        }

        private static string StoreText(Slot s)
        {
            if (s.Store == null || s.Store.Count == 0) return "已存 0 根";
            DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
            foreach (var k in s.Store.Keys) { if (first == DateTime.MaxValue) first = k; last = k; }
            return string.Format(CultureInfo.InvariantCulture, "已存 {0} 根，{1:yyyy-MM-dd HH:mm} 到 {2:yyyy-MM-dd HH:mm}", s.Store.Count, first, last);
        }

        // ====================================================================
        // 補缺口（spec 3.1）
        // ====================================================================
        private void StepBackfill(DateTime now)
        {
            Chunk finished = null;
            lock (sync)
            {
                // 1. 該週期主資料寫過、日線也寫過（前段要知道日線最舊一根；沒匯出日線就不等）才排段
                bool dailyReady = true;
                foreach (Slot s in slots) if (s.Name == "D1" && s.WrittenCount < 0) dailyReady = false;
                if (dailyReady)
                    foreach (Slot s in slots) if (s.Intraday && !s.Planned && s.WrittenCount >= 0) Plan(s);

                // 2. 目前那段：已送出、筆數穩定 -> 完成；沒送出、太久沒 Update -> 計時器自己送
                if (active != null)
                {
                    if (!active.Sent)
                    {
                        if ((now - active.ReadyUtc).TotalSeconds < 20) return;   // 先等 Update 送
                    }
                    else
                    {
                        int n = 0;
                        try { n = active.Data == null ? 0 : active.Data.Count; } catch { }
                        if (n != active.LastCount) { active.LastCount = n; active.LastChangeUtc = now; return; }
                        if ((now - active.LastChangeUtc).TotalSeconds < StableSeconds) return;
                        if ((now - active.SentUtc).TotalSeconds < StableSeconds * 2) return;
                        finished = active;
                        active = null;
                    }
                }
                else
                {
                    // 3. 排下一段
                    foreach (Slot s in slots)
                    {
                        if (s.Chunks.Count == 0) continue;
                        Chunk c = s.Chunks.Dequeue();
                        c.ReadyUtc = now;
                        active = c;
                        s.BackfillStatus = string.Format(CultureInfo.InvariantCulture, "補第 {0}/{1} 段 {2:yyyy-MM-dd}～{3:yyyy-MM-dd}",
                            s.ChunksDone + 1, s.ChunksTotal, c.From, c.To);
                        break;
                    }
                    if (active == null) return;
                }
            }
            if (finished != null) { FinishChunk(finished); return; }
            // 計時器代送（週末休市沒有 Update）：另開執行緒，平台的 Get 若卡住也不會擋住寫檔與進度
            bool due;
            lock (sync) due = active != null && !active.Sent && (now - active.ReadyUtc).TotalSeconds >= 20;
            if (due)
                new System.Threading.Thread(() => { try { SendPendingChunk(true); } catch { } }) { IsBackground = true }.Start();
        }

        // 送出目前那段的請求；fromTimer = false 時由 Update 呼叫
        private void SendPendingChunk(bool fromTimer = false)
        {
            Chunk c;
            Instrument inst;
            lock (sync)
            {
                c = active;
                inst = instrument;
                if (c == null || c.Sent || inst == null) return;
                if (fromTimer && (DateTime.UtcNow - c.ReadyUtc).TotalSeconds < 20) return;
                c.Sent = true;   // 先標記，避免 Update 與計時器重複送
                c.SentUtc = DateTime.UtcNow;
                c.LastChangeUtc = c.SentUtc;
                c.Request = new TimeHistoricalRequest(inst, dataType, c.Slot.Period, c.Slot.Value);
                chunkRequests.Add(c.Request);
            }
            try
            {
                HistoricalData d = HistoricalDataManager.Get(c.Request, new Interval(c.From, c.To));
                lock (sync) { if (d != null && c.Data == null) c.Data = d; }
            }
            catch (Exception ex)
            {
                lock (sync) backfillLog.Add(c.Slot.Name + " " + Range(c) + "：請求失敗 " + ex.Message);
            }
        }

        // 一段完成：合併、寫檔、記錄；前段連續 3 段沒新資料就不再往前要
        private void FinishChunk(Chunk c)
        {
            Slot s = c.Slot;
            int added = 0, got = 0;
            bool noReply;
            lock (sync) noReply = c.Data == null;
            System.Threading.Monitor.Enter(writeLock);
            try
            {
                HistoricalData d;
                lock (sync) d = c.Data;
                if (d != null)
                {
                    got = d.Count;
                    EnsureStore(s);
                    DateTime ignore;
                    added = Merge(s, d, c.To, out ignore);
                    if (added > 0) { s.Dirty = true; s.UnsavedChunks++; }
                }
                // 每 5 段有新資料、或這個週期補完才存一次
                bool last;
                lock (sync) last = s.Chunks.Count == 0;
                if (s.Dirty && (s.UnsavedChunks >= 5 || last)) SaveStore(s);
            }
            catch (Exception ex) { lock (sync) backfillLog.Add(s.Name + " " + Range(c) + "：寫檔失敗，稍後重試 " + ex.Message); }
            finally { System.Threading.Monitor.Exit(writeLock); }

            lock (sync)
            {
                s.ChunksDone++;
                s.AddedTotal += added;
                backfillLog.Add(s.Name + " " + Range(c) + "：" + (noReply ? "平台沒回應（可能是沒有這段，也可能是請求卡住）" : got == 0 ? "平台沒有這段" : "平台給 " + got + " 根，新增 " + added + " 根"));
                if (c.Head)
                {
                    s.HeadEmptyRun = added == 0 ? s.HeadEmptyRun + 1 : 0;
                    if (s.HeadEmptyRun >= 3)
                    {
                        // 丟掉剩下的前段（佇列裡前段排在缺口之後）
                        var keep = new List<Chunk>();
                        foreach (Chunk x in s.Chunks) if (!x.Head) keep.Add(x);
                        int dropped = s.Chunks.Count - keep.Count;
                        s.Chunks.Clear();
                        foreach (Chunk x in keep) s.Chunks.Enqueue(x);
                        s.ChunksTotal -= dropped;
                        backfillLog.Add(s.Name + "：往前連續 3 段都沒有新資料，平台應該沒有更早的了，停止往前要");
                    }
                }
                s.Status = StoreText(s) + "（補缺口新增 " + s.AddedTotal + " 根）";
                s.BackfillStatus = s.Chunks.Count == 0 ? "補完 " + s.ChunksDone + " 段，新增 " + s.AddedTotal + " 根" : "";
            }
            WriteLog();
        }

        // 排要補的段：中間缺口先，再往前；每個週期最多 MaxBackfillChunks 段
        private void Plan(Slot s)
        {
            s.Planned = true;
            if (s.Store == null || s.Store.Count < 2) { s.BackfillStatus = "資料太少，不補"; return; }
            var list = new List<Chunk>();
            TimeSpan step = ChunkSpan(s);
            DateTime prev = DateTime.MinValue, first = DateTime.MinValue;
            foreach (DateTime t in s.Store.Keys)
            {
                if (first == DateTime.MinValue) first = t;
                if (prev != DateTime.MinValue && (t - prev).TotalDays > GapDays)
                    for (DateTime a = prev; a < t && list.Count < MaxBackfillChunks; a += step)
                        list.Add(new Chunk { Slot = s, From = a, To = Min(a + step, t) });
                prev = t;
            }
            // 前段：比日線最舊一根晚開始（且晚超過 GapDays）就往前要，最遠到日線最舊、也不早於 StartYear
            DateTime floor = DailyOldest();
            DateTime startYear = new DateTime(StartYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (floor < startYear) floor = startYear;
            if (floor != DateTime.MaxValue && (first - floor).TotalDays > GapDays)
                for (DateTime b = first; b > floor && list.Count < MaxBackfillChunks; b -= step)
                    list.Add(new Chunk { Slot = s, From = Max(b - step, floor), To = b, Head = true });
            if (list.Count >= MaxBackfillChunks)
                backfillLog.Add(s.Name + "：要補的段數超過上限 " + MaxBackfillChunks + "，只補最近的部分；想補更多請調高 MaxBackfillChunks 再掛一次（已存的不會重複要）");
            foreach (Chunk c in list) s.Chunks.Enqueue(c);
            s.ChunksTotal = list.Count;
            if (list.Count == 0) s.BackfillStatus = "沒有缺口";
        }

        // 日線最舊一根（日線沒匯出就不往前補）
        private DateTime DailyOldest()
        {
            foreach (Slot s in slots)
                if (s.Name == "D1" && s.Store != null)
                    foreach (DateTime t in s.Store.Keys) return t;
            return DateTime.MaxValue;
        }

        private static TimeSpan ChunkSpan(Slot s)
        {
            if (s.Period == Period.Minute && s.Value <= 1) return TimeSpan.FromDays(31);
            if (s.Period == Period.Minute && s.Value <= 5) return TimeSpan.FromDays(92);
            return TimeSpan.FromDays(366);
        }

        private static DateTime Min(DateTime a, DateTime b) { return a < b ? a : b; }
        private static DateTime Max(DateTime a, DateTime b) { return a > b ? a : b; }
        private static string Range(Chunk c) { return c.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "～" + c.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static bool Num(string s, out double v) { return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        // 分鐘線已存好、日線還沒：補缺口在等日線
        private bool WaitingDaily()
        {
            if (!BackfillGaps) return false;
            foreach (Slot s in slots) if (s.Name == "D1" && s.WrittenCount < 0) return true;
            return false;
        }

        private bool BackfillDone()
        {
            if (!BackfillGaps) return true;
            if (active != null) return false;
            foreach (Slot s in slots) if (s.Intraday && (!s.Planned || s.Chunks.Count > 0)) return false;
            foreach (Slot s in slots) if (s.Dirty) return false;   // 還有沒存成檔的
            return true;
        }

        // ====================================================================
        // 商品規格、記錄、進度
        // ====================================================================
        private void WriteSymbolInfo(Instrument inst)
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
                    {
                        sb.Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
                        if (kv.Key == "計價幣別") lock (sync) fxHint = FxHint(inst.Symbol, (kv.Value ?? "").Trim().ToUpperInvariant());
                    }
                }
                catch (Exception ex) { sb.Append("# GetSymbolInfo 失敗 ").Append(ex.Message).Append("\r\n"); }
                File.WriteAllText(Path.Combine(folder, "symbol_info.txt"), sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        private string fxHint = "";   // 非美元報價時，提醒回測前還要匯出哪個貨幣對

        // 回測器換算損益要用的貨幣對：EUR、GBP、AUD、NZD 在前，其他美元在前；商品本身就是它時不用提醒
        private static string FxHint(string sym, string ccy)
        {
            if (ccy.Length != 3 || ccy == "USD") return "";
            string[] front = { "EUR", "GBP", "AUD", "NZD" };
            string pair = Array.IndexOf(front, ccy) >= 0 ? ccy + "USD" : "USD" + ccy;
            string s = (sym ?? "").Replace("/", "").ToUpperInvariant();
            if (s == pair) return "";
            return "這個商品用 " + ccy + " 報價：回測前請再對 " + pair + " 掛一次本指標（回測器要用它換算美元損益）";
        }

        private void WriteLog()
        {
            try
            {
                var sb = new StringBuilder();
                string f;
                lock (sync)
                {
                    f = folder;
                    sb.Append("HistoryExporter v0.2 ").Append(symbol).Append("，起始年份 ").Append(StartYear)
                      .Append("，更新 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                    foreach (Slot s in slots) sb.Append(s.Name).Append('：').Append(s.Status).Append("\r\n");
                    if (fxHint.Length > 0) sb.Append("提醒：").Append(fxHint).Append("\r\n");
                    if (backfillLog.Count > 0)
                    {
                        sb.Append("\r\n補缺口紀錄：\r\n");
                        foreach (string line in backfillLog) sb.Append(line).Append("\r\n");
                    }
                }
                if (f != null) File.WriteAllText(Path.Combine(f, "export_log.txt"), sb.ToString(), new UTF8Encoding(true));
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
                    // 第一行就是總結：做完幾個週期、能不能移除指標
                    int done = 0;
                    foreach (Slot x in slots) if (x.WrittenCount >= 0) done++;
                    bool all = done == slots.Count;
                    bool filled = all && BackfillDone();
                    sb.Append("HistoryExporter ").Append(symbol).Append("：已完成 ").Append(done).Append(" / ").Append(slots.Count)
                      .Append(filled ? "，全部已存，可以移除指標" : all ? "，正在補缺口，請保持圖表開著"
                              : done > 0 && WaitingDaily() ? "，等日線存好才開始補缺口，請保持圖表開著" : "，請保持圖表開著").Append('\n');
                    if (fxHint.Length > 0) sb.Append("提醒：").Append(fxHint).Append('\n');
                    foreach (Slot s in slots)
                    {
                        string extra = s.WrittenCount < 0 && s.Data != null && s.LastCount >= 0 ? "（目前 " + s.LastCount + " 根）" : "";
                        string bf = s.BackfillStatus.Length > 0 ? "　" + s.BackfillStatus : "";
                        if (active != null && active.Slot == s && active.Sent)
                            bf += "，等平台回應 " + (int)(DateTime.UtcNow - active.SentUtc).TotalSeconds + " 秒";
                        sb.Append(s.Name).Append('：').Append(s.Status).Append(extra).Append(bf).Append('\n');
                    }
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
                if (requested) { try { HistoricalDataManager.OnLoaded -= OnLoaded; } catch { } }
                slots.Clear();
                active = null;
                requested = false;
            }
        }
    }
}
