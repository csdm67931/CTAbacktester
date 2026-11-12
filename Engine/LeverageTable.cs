using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CtaBacktest
{
    // 群益官網「全商品槓桿」表（https://cfmt.capitalfutures.com.tw/zh-TW/Product/Lever）
    // 存在 Backtester\data\official_leverage.csv；設定頁按鈕可重新下載更新（spec.md 6.1）
    public static class LeverageTable
    {
        public const string Url = "https://cfmt.capitalfutures.com.tw/zh-TW/Product/Lever";
        public static readonly string[] RequiredGroups = { "外匯", "商品", "指數", "美股", "港股" };
        public const int MinRows = 100;

        public sealed class Entry
        {
            public string Symbol, Name, Group;
            public double MarginPercent;
            public double Leverage { get { return Math.Round(100.0 / MarginPercent, 2); } }
        }

        public sealed class UpdateResult
        {
            public bool Ok;
            public string Error;
            public int Total;
            public List<string> Added = new List<string>(), Changed = new List<string>(), Removed = new List<string>();
        }

        private static readonly object sync = new object();
        private static Dictionary<string, Entry> current;
        private static string path;
        private static string fetchedAt = "";

        // 預設路徑：CtaBacktest.dll 所在的 bin 的上一層 data 資料夾；測試可改
        public static string FilePath
        {
            get
            {
                if (path != null) return path;
                string bin = Path.GetDirectoryName(typeof(LeverageTable).Assembly.Location) ?? ".";
                return Path.GetFullPath(Path.Combine(bin, "..", "data", "official_leverage.csv"));
            }
            set { lock (sync) { path = value; current = null; } }
        }

        public static string FetchedAt { get { lock (sync) { Ensure(); return fetchedAt; } } }
        public static int Count { get { return Ensure().Count; } }

        // 用 Ensure 回傳的字典參考，背景更新同時把 current 換掉也不會讀到 null
        public static Entry Find(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;
            Entry e;
            return Ensure().TryGetValue(symbol, out e) ? e : null;
        }

        public static void Reload() { lock (sync) current = null; }

        private static Dictionary<string, Entry> Ensure()
        {
            lock (sync)
            {
                if (current != null) return current;
                string at = "";
                // 讀不到（被占用、格式壞掉）時當作沒有表，改用內建值，不讓整個程式掛掉
                try { current = File.Exists(FilePath) ? Load(FilePath, out at) : null; }
                catch (Exception) { current = null; at = ""; }
                if (current == null) current = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                fetchedAt = at;
                return current;
            }
        }

        // ---------------------------------------------------------------- 解析官網頁面
        // 頁面結構（2026-11-09）：<div id="tabN">類別名</div> 之後的 <div id="tabN-table"> 內有一張表，
        // 欄位：商品代碼、連結標的、槓桿倍數、最低原始保證金(%)、交易時段
        public static List<Entry> Parse(string html) { int dropped; return Parse(html, out dropped); }

        // dropped = 有資料但看不懂的列數（格式變了）；Update 用它判斷要不要拒絕覆蓋
        public static List<Entry> Parse(string html, out int dropped)
        {
            dropped = 0;
            var list = new List<Entry>();
            var titles = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(html, "id=\"tab(\\d+)\"[^>]*>([^<]*)</div>"))
                titles[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            foreach (Match tm in Regex.Matches(html, "id=\"tab(\\d+)-table\"(.*?)</table>", RegexOptions.Singleline))
            {
                string title;
                titles.TryGetValue(tm.Groups[1].Value, out title);
                string group = GroupOf(title ?? "");
                foreach (Match rm in Regex.Matches(tm.Groups[2].Value, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline))
                {
                    var cells = Regex.Matches(rm.Groups[1].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                        .Cast<Match>().Select(c => WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]+>", "")).Trim()).ToList();
                    if (cells.Count < 4) continue;
                    double pct, lev;
                    bool hasPct = ParsePercent(cells[3], out pct);
                    bool hasLev = double.TryParse(cells[2].Replace("倍", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lev) && lev >= 1;
                    string sym = cells[0].Replace("/", "").Replace(" ", "");
                    // 槓桿倍數欄是準的；看不懂就整列不收（不改用四捨五入的保證金欄，免得 30 倍變 30.03 倍）
                    if (!hasLev || sym.Length == 0) { dropped++; continue; }
                    // 官網保證金欄是四捨五入的（30 倍寫 3.33%），只在兩欄一致時（例 6.67 倍 = 15%）用它的精確值
                    if (!(hasPct && Math.Abs(Math.Round(100.0 / pct, 2) - lev) < 1e-9)) pct = 100.0 / lev;
                    list.Add(new Entry { Symbol = sym, Name = Regex.Replace(cells[1], "[,\\r\\n]", " "), Group = group, MarginPercent = pct });
                }
            }
            return list;
        }

        private static string GroupOf(string title)
        {
            foreach (string g in new[] { "外匯", "商品", "指數", "美股", "港股" })
                if (title.Contains(g)) return g;
            return title.Length > 0 ? title : "其他";
        }

        private static bool ParsePercent(string s, out double v)
        {
            s = s.Replace("%", "").Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v <= 100;
        }

        // 解析結果是否像完整的官網表；不像就不覆蓋原檔
        public static string Validate(List<Entry> list)
        {
            if (list.Count < MinRows) return "只讀到 " + list.Count + " 項（少於 " + MinRows + "），官網頁面可能改版。";
            var missing = RequiredGroups.Where(g => !list.Any(e => e.Group == g)).ToList();
            if (missing.Count > 0) return "缺少類別：" + string.Join("、", missing) + "，官網頁面可能改版。";
            return null;
        }

        // ---------------------------------------------------------------- 檔案讀寫
        public static Dictionary<string, Entry> Load(string file, out string at)
        {
            var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            at = "";
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (line.StartsWith("#")) { int i = line.IndexOf("fetched_at="); if (i >= 0) at = line.Substring(i + 11).Trim(); continue; }
                string[] f = line.Split(',');
                double pct;
                if (f.Length < 5 || f[0] == "symbol" || !ParsePercent(f[4], out pct)) continue;
                d[f[0]] = new Entry { Symbol = f[0], Name = f[1], Group = f[2], MarginPercent = pct };
            }
            return d;
        }

        // 先寫暫存檔再替換，寫到一半失敗不會毀掉原檔
        public static void Save(string file, IEnumerable<Entry> list, string at)
        {
            var sb = new StringBuilder();
            sb.Append("# source=").Append(Url).Append(" fetched_at=").Append(at).Append('\n');
            sb.Append("symbol,name,group,leverage,margin_pct\n");
            foreach (Entry e in list)
                sb.Append(e.Symbol).Append(',').Append(Regex.Replace(e.Name, "[,\\r\\n]", " ")).Append(',').Append(e.Group).Append(',')
                  .Append(e.Leverage.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.MarginPercent.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        // ---------------------------------------------------------------- 更新
        public static string Download()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "CtaBacktest";
                return wc.DownloadString(Url);
            }
        }

        // html 為 null 時從官網下載；成功才覆蓋檔案並重新載入
        public static UpdateResult Update(string html = null)
        {
            var r = new UpdateResult();
            try
            {
                if (html == null) html = Download();
                int dropped;
                List<Entry> list = Parse(html, out dropped);
                string bad = Validate(list);
                // 部分列看不懂：寧可不更新，也不要把看不懂的商品當成下架
                if (bad == null && dropped > 0)
                    bad = "有 " + dropped + " 列看不懂（共 " + (list.Count + dropped) + " 列），官網格式可能改了。";
                string dummy;
                var old = File.Exists(FilePath) ? Load(FilePath, out dummy) : new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                if (bad == null && old.Count > 0 && list.Count < old.Count * 0.9)
                    bad = "新表只有 " + list.Count + " 項，比原表 " + old.Count + " 項少一成以上，可能沒抓完整。";
                if (bad != null) { r.Error = bad + "原檔未變更。"; return r; }
                // 同代號重複時取最後一筆
                var fresh = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry e in list) fresh[e.Symbol] = e;
                foreach (Entry e in fresh.Values)
                {
                    Entry o;
                    if (!old.TryGetValue(e.Symbol, out o)) r.Added.Add(e.Symbol + " " + e.Name + " " + Fmt(e.Leverage) + " 倍");
                    else if (Math.Abs(o.MarginPercent - e.MarginPercent) > 1e-9)
                        r.Changed.Add(e.Symbol + " " + e.Name + "：" + Fmt(o.Leverage) + " 倍 → " + Fmt(e.Leverage) + " 倍");
                }
                foreach (Entry o in old.Values)
                    if (!fresh.ContainsKey(o.Symbol)) r.Removed.Add(o.Symbol + " " + o.Name);
                Save(FilePath, fresh.Values, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                Reload();
                r.Total = fresh.Count;
                r.Ok = true;
            }
            catch (Exception ex)
            {
                r.Error = "更新失敗：" + ex.Message + "。原檔未變更。";
            }
            return r;
        }

        private static string Fmt(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
