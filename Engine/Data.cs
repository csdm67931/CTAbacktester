using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CtaBacktest
{
    public struct Bar
    {
        public DateTime Time;   // 開盤時間（UTC）
        public double Open, High, Low, Close;
    }

    // 一個週期的完整 K 棒（由舊到新）
    public sealed class BarSeries
    {
        public readonly string Timeframe;
        public readonly List<Bar> Bars;

        public BarSeries(string timeframe, List<Bar> bars)
        {
            Timeframe = timeframe;
            Bars = bars;
        }

        public int Count { get { return Bars.Count; } }

        // 最後一根開盤時間 <= t 的索引；沒有則 -1
        public int IndexAtOrBefore(DateTime t)
        {
            int lo = 0, hi = Bars.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Bars[mid].Time <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return ans;
        }

        // 第一根開盤時間 >= t 的索引；沒有則 Count
        public int FirstIndexAtOrAfter(DateTime t)
        {
            int lo = 0, hi = Bars.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Bars[mid].Time < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // 讀 HistoryExporter 匯出的 CSV：time_utc,open,high,low,close
        public static BarSeries LoadCsv(string path, string timeframe)
        {
            var list = new List<Bar>();
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || char.IsLetter(line[0])) continue;   // 表頭
                string[] p = line.Split(',');
                if (p.Length < 5) continue;
                DateTime t;
                if (!DateTime.TryParseExact(p[0].Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" },
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                    continue;
                double o, h, l, c;
                if (!Num(p[1], out o) || !Num(p[2], out h) || !Num(p[3], out l) || !Num(p[4], out c)) continue;
                list.Add(new Bar { Time = DateTime.SpecifyKind(t, DateTimeKind.Utc), Open = o, High = Math.Max(h, Math.Max(o, c)), Low = Math.Min(l, Math.Min(o, c)), Close = c });
            }
            list.Sort((a, b) => a.Time.CompareTo(b.Time));
            // 去掉重複時間（保留後出現的那根）
            var clean = new List<Bar>(list.Count);
            foreach (Bar b in list)
            {
                if (clean.Count > 0 && clean[clean.Count - 1].Time == b.Time) clean[clean.Count - 1] = b;
                else clean.Add(b);
            }
            return new BarSeries(timeframe, clean);
        }

        private static bool Num(string s, out double v)
        {
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    public static class Timeframes
    {
        public static readonly string[] All = { "M1", "M5", "M15", "M30", "H1", "H4", "D1", "W1", "MN1" };

        public static int Minutes(string tf)
        {
            switch (tf)
            {
                case "M1": return 1;
                case "M5": return 5;
                case "M15": return 15;
                case "M30": return 30;
                case "H1": return 60;
                case "H4": return 240;
                case "D1": return 1440;
                case "W1": return 10080;
                case "MN1": return 44640;   // 31 天，只用來限制最後一根的報價時間範圍
                default: return 0;
            }
        }

        // 平台的 Period＋數值 轉成檔名用的週期代號；沒有對應的回傳 null
        public static string FromPeriod(TradeApi.History.Period period, int value)
        {
            int minutes;
            switch (period)
            {
                case TradeApi.History.Period.Minute: minutes = value; break;
                case TradeApi.History.Period.Hour: minutes = value * 60; break;
                case TradeApi.History.Period.Day: minutes = value * 1440; break;
                case TradeApi.History.Period.Week: minutes = value * 10080; break;
                case TradeApi.History.Period.Month: return value == 1 ? "MN1" : null;
                default: return null;
            }
            foreach (string tf in All) if (tf != "MN1" && Minutes(tf) == minutes) return tf;
            return null;
        }

        public static string Label(string tf)
        {
            switch (tf)
            {
                case "M1": return "M1（1 分鐘）";
                case "M5": return "M5（5 分鐘）";
                case "M15": return "M15（15 分鐘）";
                case "M30": return "M30（30 分鐘）";
                case "H1": return "H1（1 小時）";
                case "H4": return "H4（4 小時）";
                case "D1": return "D1（日線）";
                case "W1": return "W1（週線）";
                case "MN1": return "MN1（月線）";
                default: return tf;
            }
        }
    }

    // 一個商品資料夾：<週期>.csv ＋ symbol_info.txt
    public sealed class SymbolData
    {
        public string Folder { get; private set; }
        public string Symbol { get; private set; }
        public Dictionary<string, string> Header { get; private set; }          // symbol_info.txt 上半部
        public List<KeyValuePair<string, string>> SymbolInfo { get; private set; } // [SymbolInfo] 區段（平台 GetSymbolInfo 原樣）

        private readonly Dictionary<string, BarSeries> cache = new Dictionary<string, BarSeries>();

        public static SymbolData Open(string folder)
        {
            var d = new SymbolData
            {
                Folder = folder,
                Symbol = Path.GetFileName(folder.TrimEnd('\\', '/')),
                Header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                SymbolInfo = new List<KeyValuePair<string, string>>()
            };
            string info = Path.Combine(folder, "symbol_info.txt");
            if (File.Exists(info))
            {
                bool section = false;
                foreach (string raw in File.ReadAllLines(info, Encoding.UTF8))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.Trim() == "[SymbolInfo]") { section = true; continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (section) d.SymbolInfo.Add(new KeyValuePair<string, string>(k, v));
                    else d.Header[k] = v;
                }
                string sym;
                if (d.Header.TryGetValue("Symbol", out sym) && sym.Length > 0) d.Symbol = sym;
            }
            return d;
        }

        public bool HasTimeframe(string tf)
        {
            return File.Exists(Path.Combine(Folder, tf + ".csv"));
        }

        public List<string> AvailableTimeframes()
        {
            return Timeframes.All.Where(HasTimeframe).ToList();
        }

        public BarSeries Load(string tf)
        {
            lock (cache)
            {
                BarSeries s;
                if (!cache.TryGetValue(tf, out s))
                {
                    string path = Path.Combine(Folder, tf + ".csv");
                    s = File.Exists(path) ? BarSeries.LoadCsv(path, tf) : new BarSeries(tf, new List<Bar>());
                    cache[tf] = s;
                }
                return s;
            }
        }

        public double HeaderNumber(string key)
        {
            string s;
            double v;
            if (Header.TryGetValue(key, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return double.NaN;
        }

        public string Info(params string[] keys)
        {
            foreach (string k in keys)
                foreach (var kv in SymbolInfo)
                    if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // 取字串第一段數字（例如「100000 EUR」），去掉千分位逗號；和 RoyAuto.ParseNumber 相同
        public static bool ParseNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string first = s.Trim().Split(' ')[0].Replace(",", "");
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        public double ContractSize
        {
            get { double v; return ParseNumber(Info("合約規格", "Lot size"), out v) && v > 0 ? v : double.NaN; }
        }

        public string QuoteCurrency
        {
            get
            {
                string q = Info("計價幣別", "Quoting currency");
                if (!string.IsNullOrEmpty(q)) return q.Trim().Split(' ')[0];
                string sector = Info("部門別", "Sector");
                if (sector == "外匯" || sector == "currency")
                {
                    string s = Symbol.Split(':')[0];
                    if (s.Length >= 6) return s.Substring(s.Length - 3);
                }
                return null;
            }
        }

        public static List<string> ListSymbols(string root)
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.GetDirectories(root)
                .Where(d => Timeframes.All.Any(tf => File.Exists(Path.Combine(d, tf + ".csv"))))
                .Select(Path.GetFileName).OrderBy(x => x).ToList();
        }
    }
}
