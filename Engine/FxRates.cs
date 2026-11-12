using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CtaBacktest
{
    // 自動匯率（spec.md 6.2）：報價幣別換算帳戶幣別，從同資料夾的貨幣對匯出資料取報價
    public static class FxRates
    {
        // 貨幣對報價 → 匯率：報價幣別在前（EURUSD）匯率 = 報價；帳戶幣別在前（USDJPY）匯率 = 1 ÷ 報價
        public static double RateFromPair(string pair, string quote, double price)
        {
            if (!(price > 0) || string.IsNullOrEmpty(pair) || string.IsNullOrEmpty(quote)) return double.NaN;
            return pair.StartsWith(quote, StringComparison.OrdinalIgnoreCase) ? price : 1.0 / price;
        }

        // 貨幣對報價的小數位數：日圓相關 3 位，其他 5 位（使用者 2026-11-10 說明的報價慣例）
        public static int PairDigits(string pair)
        {
            return pair != null && pair.ToUpperInvariant().Contains("JPY") ? 3 : 5;
        }

        // 依報價幣別填 s.QuoteCurrency、FxPair、FxPairPrice、FxRate、FxSource；找不到就維持 1 並提醒
        public static void Apply(SymbolData d, TesterSettings s, string quote, List<string> notes)
        {
            s.QuoteCurrency = string.IsNullOrEmpty(quote) ? null : quote.ToUpperInvariant();
            s.FxPair = null; s.FxPairPrice = double.NaN; s.FxSource = null; s.FxRate = 1;
            string acc = s.AccountCurrency.ToUpperInvariant();
            if (s.QuoteCurrency == null)
            {
                if (notes != null) notes.Add("讀不到報價幣別，匯率先用 1（當作美元報價），請確認。");
                return;
            }
            if (s.QuoteCurrency == acc) return;

            string root = Path.GetDirectoryName(d.Folder.TrimEnd('\\', '/'));
            foreach (string pair in new[] { s.QuoteCurrency + acc, acc + s.QuoteCurrency })
            {
                DateTime at;
                double price;
                if (pair.Equals(d.Symbol, StringComparison.OrdinalIgnoreCase)) price = LastClose(d, s.ToUtc, out at);
                else if (root != null && Directory.Exists(Path.Combine(root, pair))) price = CachedLastClose(Path.Combine(root, pair), s.ToUtc, out at);
                else continue;
                if (!(price > 0)) continue;
                s.FxPair = pair;
                s.FxPairPrice = Math.Round(price, PairDigits(pair));
                s.FxRate = RateFromPair(pair, s.QuoteCurrency, s.FxPairPrice);
                s.FxSource = pair + " " + at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 收盤";
                return;
            }
            // 沒有匯出資料：仍給出應該用的貨幣對，讓使用者自己填報價
            s.FxPair = KnownPair(s.QuoteCurrency, acc);
            if (notes != null) notes.Add("報價幣別是 " + s.QuoteCurrency + "，損益要用 " + s.FxPair
                + " 換算成美元，資料夾裡還沒有它的資料。交易王有連線（掛著 HistoryBridge）時回測器會自動抓；沒連線時，也可以在本頁下方「帳戶與交易成本」的「匯率」欄自己填 " + s.FxPair + " 報價。");
        }

        // 交叉外匯保證金（spec.md 第 6 節）：基礎、報價幣別都不是帳戶幣別時，回傳基礎貨幣對（EURAUD → EURUSD）。
        // 不是交叉外匯回傳 null；資料夾有那個貨幣對時 series 為它的 K 棒（M1 優先），否則 series = null。
        // baseFirst = 基礎幣別在前（EURUSD，匯率 = 報價）；否則匯率 = 1 ÷ 報價
        public static string MarginPair(SymbolData d, string quote, string acc, out BarSeries series, out bool baseFirst)
        {
            series = null; baseFirst = true;
            string sym = d.Symbol.Split(':')[0].Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(quote) || string.IsNullOrEmpty(acc)) return null;
            quote = quote.ToUpperInvariant(); acc = acc.ToUpperInvariant();
            if (sym.Length != 6 || !sym.EndsWith(quote)) return null;
            foreach (char c in sym) if (c < 'A' || c > 'Z') return null;
            string bas = sym.Substring(0, 3);
            if (bas == acc || quote == acc) return null;

            string root = Path.GetDirectoryName(d.Folder.TrimEnd('\\', '/'));
            foreach (string pair in new[] { bas + acc, acc + bas })
            {
                if (root == null || !Directory.Exists(Path.Combine(root, pair))) continue;
                BarSeries s = CachedMarginSeries(Path.Combine(root, pair));
                if (s == null) continue;
                series = s; baseFirst = pair.StartsWith(bas);
                return pair;
            }
            return KnownPair(bas, acc);
        }

        // 保證金貨幣對的 K 棒（M1 優先，其次 H1 等）；同一資料夾只解析一次（最佳化每組都會呼叫），
        // 資料夾裡任何檔案更新過就重讀。BarSeries 讀進來後只讀不寫，可多執行緒共用
        private static readonly Dictionary<string, BarSeries> marginCache = new Dictionary<string, BarSeries>(StringComparer.OrdinalIgnoreCase);

        private static BarSeries CachedMarginSeries(string folder)
        {
            DateTime stamp = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(folder)) { var w = File.GetLastWriteTimeUtc(f); if (w > stamp) stamp = w; }
            string key = folder + "|" + stamp.Ticks;
            lock (marginCache)
            {
                BarSeries hit;
                if (marginCache.TryGetValue(key, out hit)) return hit;
                SymbolData pd = SymbolData.Open(folder);
                var order = new List<string> { "M1", "H1" };
                foreach (string tf in Timeframes.All) if (!order.Contains(tf)) order.Add(tf);
                BarSeries found = null;
                foreach (string tf in order)
                {
                    if (!pd.HasTimeframe(tf)) continue;
                    BarSeries s = pd.Load(tf);
                    if (s.Count > 0) { found = s; break; }
                }
                if (marginCache.Count > 20) marginCache.Clear();   // 只留最近用到的
                marginCache[key] = found;
                return found;
            }
        }

        // 損益換算的歷史匯率（spec.md 6.2）：pair 是 s.FxPair（GBPJPY 用 USDJPY）。
        // 商品本身就是該貨幣對時用自己的資料夾；資料夾沒有 M1、H1、D1 任一週期時回傳 null
        public static PairRates ProfitRates(SymbolData d, string pair, string quote)
        {
            if (string.IsNullOrEmpty(pair) || string.IsNullOrEmpty(quote)) return null;
            string folder;
            if (pair.Equals(d.Symbol.Split(':')[0].Trim(), StringComparison.OrdinalIgnoreCase)) folder = d.Folder;
            else
            {
                string root = Path.GetDirectoryName(d.Folder.TrimEnd('\\', '/'));
                if (root == null) return null;
                folder = Path.Combine(root, pair);
            }
            if (!Directory.Exists(folder)) return null;
            List<BarSeries> list = CachedRateSeries(folder);
            return list.Count == 0 ? null : new PairRates(pair, quote, list);
        }

        // 歷史匯率用的 M1、H1、D1 K 棒；同一資料夾只解析一次，檔案更新過就重讀。讀進來後只讀不寫，可多執行緒共用
        private static readonly Dictionary<string, List<BarSeries>> rateCache = new Dictionary<string, List<BarSeries>>(StringComparer.OrdinalIgnoreCase);

        private static List<BarSeries> CachedRateSeries(string folder)
        {
            DateTime stamp = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(folder)) { var w = File.GetLastWriteTimeUtc(f); if (w > stamp) stamp = w; }
            string key = folder + "|" + stamp.Ticks;
            lock (rateCache)
            {
                List<BarSeries> hit;
                if (rateCache.TryGetValue(key, out hit)) return hit;
                SymbolData pd = SymbolData.Open(folder);
                var list = new List<BarSeries>();
                foreach (string tf in new[] { "M1", "H1", "D1" })
                {
                    if (!pd.HasTimeframe(tf)) continue;
                    BarSeries s = pd.Load(tf);
                    if (s.Count > 0) list.Add(s);
                }
                if (rateCache.Count > 20) rateCache.Clear();   // 只留最近用到的
                rateCache[key] = list;
                return list;
            }
        }

        // 慣用報價方向：歐元、英鎊、澳幣、紐幣在美元前面，其他美元在前
        private static string KnownPair(string quote, string acc)
        {
            switch (quote)
            {
                case "EUR": case "GBP": case "AUD": case "NZD": return quote + acc;
                default: return acc + quote;
            }
        }

        // 同一個貨幣對資料夾、同一個截止日只讀一次檔（貨幣對只有 M1 時整檔解析很慢）；
        // 資料夾裡任何檔案更新過（使用者重新匯出）就重讀
        private static readonly Dictionary<string, Tuple<double, DateTime>> cache = new Dictionary<string, Tuple<double, DateTime>>(StringComparer.OrdinalIgnoreCase);

        private static double CachedLastClose(string folder, DateTime until, out DateTime at)
        {
            DateTime stamp = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(folder)) { var w = File.GetLastWriteTimeUtc(f); if (w > stamp) stamp = w; }
            string key = folder + "|" + until.Ticks + "|" + stamp.Ticks;
            lock (cache) if (cache.Count > 200) cache.Clear();   // 長時間使用也不會一直變大
            Tuple<double, DateTime> hit;
            lock (cache)
                if (cache.TryGetValue(key, out hit)) { at = hit.Item2; return hit.Item1; }
            double price = LastClose(SymbolData.Open(folder), until, out at);
            lock (cache) cache[key] = Tuple.Create(price, at);
            return price;
        }

        // 不晚於 until 的最後一根收盤價；依序找 D1、H1、其他週期；都沒有時用匯出檔的 Bid
        public static double LastClose(SymbolData d, DateTime until, out DateTime at)
        {
            at = DateTime.MinValue;
            var order = new List<string> { "D1", "H1" };
            foreach (string tf in Timeframes.All) if (!order.Contains(tf)) order.Add(tf);
            foreach (string tf in order)
            {
                if (!d.HasTimeframe(tf)) continue;
                BarSeries s = d.Load(tf);
                for (int i = s.Count - 1; i >= 0; i--)
                {
                    if (until != default(DateTime) && s.Bars[i].Time >= until) continue;
                    at = s.Bars[i].Time;
                    return s.Bars[i].Close;
                }
            }
            double bid = d.HeaderNumber("Bid");
            string t;
            if (bid > 0 && d.Header.TryGetValue("ExportedUtc", out t)) DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out at);
            return bid > 0 ? bid : double.NaN;
        }
    }

    // 跟著模擬時間走的換算匯率（spec.md 6.2）：依序看 M1、H1、D1，取開盤時間不晚於當下、
    // 且距當下不超過 4 天的最後一根的開盤價。每個 Broker 各用一份（記住上次查詢，同一時間不重查）
    public sealed class PairRates
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(4);
        public readonly string Pair, Quote;
        private readonly List<BarSeries> series;
        private DateTime lastT = DateTime.MinValue;
        private double lastR = double.NaN;

        internal PairRates(string pair, string quote, List<BarSeries> series)
        {
            Pair = pair; Quote = quote; this.series = series;
        }

        // 報告用：各週期的起點，例「M1 2026-02-01 起、D1 2013-01-01 起」
        public string Describe()
        {
            var parts = new List<string>();
            foreach (BarSeries s in series) parts.Add(s.Timeframe + " " + s.Bars[0].Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 起");
            return string.Join("、", parts.ToArray());
        }

        // 當下的匯率（報價幣別 → 帳戶幣別）；找不到回傳 NaN
        public double At(DateTime t)
        {
            if (t == lastT) return lastR;
            double r = double.NaN;
            foreach (BarSeries s in series)
            {
                int i = s.IndexAtOrBefore(t);
                if (i < 0 || t - s.Bars[i].Time > MaxAge) continue;
                double p = s.Bars[i].Open;
                if (!(p > 0)) continue;
                r = FxRates.RateFromPair(Pair, Quote, p);
                break;
            }
            lastT = t; lastR = r;
            return r;
        }
    }
}
