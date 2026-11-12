using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CtaBacktest
{
    // 資料橋回測器端（規格見 spec.md 3.5）：寫請求單給交易王的 HistoryBridge 指標，等它回寫結果
    public static class Bridge
    {
        public static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export", "Bridge"); }
        }

        public sealed class Heartbeat
        {
            public DateTime Utc;
            public string Version = "", Chart = "", Busy = "", DataRoot = "";
            public int Queue;
            public bool Exists;
            // 20 秒內有心跳就算連線中
            public bool Online { get { return Exists && Math.Abs((DateTime.UtcNow - Utc).TotalSeconds) <= 20; } }
        }

        public sealed class TfResult
        {
            public string Tf = "", Msg = "";
            public int Got, Added, Total = -1;
            public DateTime? First, Last;
        }

        public sealed class Result
        {
            public bool Ok;
            public string Msg = "";
            public readonly List<TfResult> Tfs = new List<TfResult>();
        }

        public sealed class Progress
        {
            public string Tf = "";
            public int Index, Count, Waited, Bars;
        }

        public static Heartbeat ReadHeartbeat(string root)
        {
            var h = new Heartbeat();
            Dictionary<string, string> kv = ReadKv(Path.Combine(root, "heartbeat.txt"));
            if (kv == null) return h;
            DateTime t;
            if (!DateTime.TryParseExact(Get(kv, "utc"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return h;
            h.Exists = true;
            h.Utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            h.Version = Get(kv, "version");
            h.Chart = Get(kv, "chart");
            h.Busy = Get(kv, "busy");
            h.DataRoot = Get(kv, "data");
            int q; int.TryParse(Get(kv, "queue"), NumberStyles.Integer, CultureInfo.InvariantCulture, out q);
            h.Queue = q;
            return h;
        }

        // 一個週期要從哪天抓；From = null 表示要平台能給的全部
        public sealed class TfPlan
        {
            public string Tf;
            public DateTime? From;
        }

        // ------------------------------------------------------------ 不重複抓（spec 3.5）

        public const string StateFile = "bridge_state.txt";

        // 上次成功抓取後多久內不再自動抓
        public static TimeSpan FreshWindow(string tf)
        {
            switch ((tf ?? "").ToUpperInvariant())
            {
                case "D1": case "W1": case "MN1": return TimeSpan.FromHours(12);
                default: return TimeSpan.FromMinutes(60);
            }
        }

        public static Dictionary<string, DateTime> ReadState(string folder)
        {
            var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> kv = ReadKv(Path.Combine(folder, StateFile));
            if (kv == null) return d;
            foreach (var p in kv)
            {
                DateTime t;
                if (DateTime.TryParseExact(p.Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) d[p.Key] = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }
            return d;
        }

        // 記下這些週期剛成功抓過
        public static void MarkFetched(string folder, IEnumerable<string> tfs, DateTime utc)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                Dictionary<string, DateTime> d = ReadState(folder);
                foreach (string tf in tfs) d[tf.ToUpperInvariant()] = utc;
                var sb = new StringBuilder();
                foreach (var p in d) sb.Append(p.Key).Append('=').Append(p.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
                File.WriteAllText(Path.Combine(folder, StateFile), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // CSV 最後一根的時間；只讀檔尾，M1 幾十 MB 也很快。沒有檔或讀不到回 null
        public static DateTime? LastBarTime(string csv)
        {
            try
            {
                if (!File.Exists(csv)) return null;
                using (var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    int n = (int)Math.Min(len, 4096);
                    fs.Seek(len - n, SeekOrigin.Begin);
                    var buf = new byte[n];
                    int got = 0;
                    while (got < n) { int k = fs.Read(buf, got, n - got); if (k <= 0) break; got += k; }
                    string[] lines = Encoding.UTF8.GetString(buf, 0, got).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        int comma = lines[i].IndexOf(',');
                        if (comma <= 0) continue;
                        DateTime? t = ParseTime(lines[i].Substring(0, comma).Trim());
                        if (t.HasValue) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        // 決定每個週期要不要抓、從哪天抓。skipFresh = 自動觸發時略過剛抓過的週期；skipped 回傳被略過的週期與原因
        public static List<TfPlan> PlanFetch(string dataRoot, string symbol, IList<string> tfs, bool skipFresh, DateTime nowUtc, List<string> skipped)
        {
            var plans = new List<TfPlan>();
            string folder = Path.Combine(dataRoot ?? "", (symbol ?? "").Trim().ToUpperInvariant());
            Dictionary<string, DateTime> state = ReadState(folder);
            foreach (string raw in tfs)
            {
                string tf = raw.ToUpperInvariant();
                DateTime? last = LastBarTime(Path.Combine(folder, tf + ".csv"));
                DateTime fetched;
                if (skipFresh && last.HasValue && state.TryGetValue(tf, out fetched) && nowUtc - fetched < FreshWindow(tf) && nowUtc >= fetched)
                {
                    if (skipped != null) skipped.Add(tf + "（" + Math.Max(1, (int)(nowUtc - fetched).TotalMinutes) + " 分鐘前抓過）");
                    continue;
                }
                plans.Add(new TfPlan { Tf = tf, From = last.HasValue ? (DateTime?)last.Value.Date.AddDays(-2) : null });
            }
            return plans;
        }

        public static List<TfPlan> FullPlan(IList<string> tfs)
        {
            var plans = new List<TfPlan>();
            foreach (string tf in tfs) plans.Add(new TfPlan { Tf = tf.ToUpperInvariant() });
            return plans;
        }

        // 寫請求單，回傳 id；先寫 .tmp 再改名，指標不會讀到寫一半的檔
        public static string Submit(string root, string symbol, IList<string> tfs, DateTime? fromUtc)
        {
            var plans = new List<TfPlan>();
            foreach (string tf in tfs) plans.Add(new TfPlan { Tf = tf, From = fromUtc });
            return Submit(root, symbol, plans);
        }

        public static string Submit(string root, string symbol, IList<TfPlan> plans)
        {
            string reqDir = Path.Combine(root, "requests");
            Directory.CreateDirectory(reqDir);
            Directory.CreateDirectory(Path.Combine(root, "responses"));
            string id = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);   // 同一毫秒連送也不會撞名
            var sb = new StringBuilder();
            sb.Append("id=").Append(id).Append("\r\n");
            sb.Append("symbol=").Append(symbol.Trim().ToUpperInvariant()).Append("\r\n");
            var names = new List<string>();
            foreach (TfPlan p in plans) names.Add(p.Tf);
            sb.Append("timeframes=").Append(string.Join(",", names)).Append("\r\n");
            sb.Append("from=\r\n");
            // 各週期自己的起始日；空白 = 要全部
            foreach (TfPlan p in plans)
                sb.Append("from.").Append(p.Tf).Append('=').Append(p.From.HasValue ? p.From.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "").Append("\r\n");
            sb.Append("created=").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
            string target = Path.Combine(reqDir, id + ".req"), temp = Path.Combine(reqDir, id + ".tmp");
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            File.Move(temp, target);
            return id;
        }

        public static Progress ReadProgress(string root, string id)
        {
            Dictionary<string, string> kv = ReadKv(Path.Combine(root, "responses", id + ".status"));
            if (kv == null) return null;
            var p = new Progress { Tf = Get(kv, "tf") };
            int.TryParse(Get(kv, "index"), out p.Index);
            int.TryParse(Get(kv, "count"), out p.Count);
            int.TryParse(Get(kv, "waited"), out p.Waited);
            int.TryParse(Get(kv, "bars"), out p.Bars);
            return p;
        }

        public static Result ReadResult(string root, string id)
        {
            string path = Path.Combine(root, "responses", id + ".res");
            string[] lines;
            try { if (!File.Exists(path)) return null; lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (IOException) { return null; }
            var r = new Result();
            foreach (string raw in lines)
            {
                if (raw.StartsWith("tf=", StringComparison.Ordinal))
                {
                    var t = new TfResult();
                    foreach (string part in raw.Split('|'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                        if (k == "tf") t.Tf = v;
                        else if (k == "got") int.TryParse(v, out t.Got);
                        else if (k == "added") int.TryParse(v, out t.Added);
                        else if (k == "total") { int n; t.Total = int.TryParse(v, out n) ? n : -1; }
                        else if (k == "first") t.First = ParseTime(v);
                        else if (k == "last") t.Last = ParseTime(v);
                        else if (k == "msg") t.Msg = v;
                    }
                    r.Tfs.Add(t);
                    continue;
                }
                int e = raw.IndexOf('=');
                if (e <= 0) continue;
                string key = raw.Substring(0, e), val = raw.Substring(e + 1);
                if (key == "state") r.Ok = val == "done";
                else if (key == "msg") r.Msg = val;
            }
            return r;
        }

        // 讀完刪掉自己的結果與進度檔；逾時或取消時連請求單一起收掉
        public static void Cleanup(string root, string id, bool withRequest)
        {
            foreach (string p in new[] { Path.Combine(root, "responses", id + ".res"), Path.Combine(root, "responses", id + ".status") })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            if (withRequest)
                try { string q = Path.Combine(root, "requests", id + ".req"); if (File.Exists(q)) File.Delete(q); } catch { }
        }

        // 送出並等結果（在背景執行緒呼叫）；每 500 毫秒讀一次進度。逾時或取消回傳 null
        public static Result Run(string root, string symbol, IList<string> tfs, DateTime? fromUtc, TimeSpan timeout,
                                 Action<Progress> onProgress, Func<bool> cancel, out string error)
        {
            var plans = new List<TfPlan>();
            foreach (string tf in tfs) plans.Add(new TfPlan { Tf = tf, From = fromUtc });
            return Run(root, symbol, plans, timeout, onProgress, cancel, out error);
        }

        public static Result Run(string root, string symbol, IList<TfPlan> plans, TimeSpan timeout,
                                 Action<Progress> onProgress, Func<bool> cancel, out string error)
        {
            error = null;
            string id = Submit(root, symbol, plans);
            DateTime end = DateTime.UtcNow + timeout;
            while (true)
            {
                Result r = ReadResult(root, id);
                if (r != null) { Cleanup(root, id, false); return r; }
                if (cancel != null && cancel()) { Cleanup(root, id, true); error = "已取消"; return null; }
                if (DateTime.UtcNow > end) { Cleanup(root, id, true); error = "等了 " + (timeout.TotalMinutes >= 1 ? (int)timeout.TotalMinutes + " 分鐘" : (int)timeout.TotalSeconds + " 秒") + "沒有結果"; return null; }
                if (onProgress != null) { Progress p = ReadProgress(root, id); if (p != null) onProgress(p); }
                Thread.Sleep(500);
            }
        }

        // 要哪些週期：選的週期，加 M1（M1 OHLC 模式用）；重複的去掉
        public static List<string> PlanTimeframes(string tf, bool needM1)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(tf)) list.Add(tf.ToUpperInvariant());
            if (needM1 && !list.Contains("M1")) list.Add("M1");
            return list;
        }

        private static DateTime? ParseTime(string v)
        {
            DateTime t;
            if (DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return null;
        }

        private static Dictionary<string, string> ReadKv(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq > 0) kv[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
                }
                return kv;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string Get(Dictionary<string, string> kv, string k) { string v; return kv.TryGetValue(k, out v) ? v : ""; }
    }
}
