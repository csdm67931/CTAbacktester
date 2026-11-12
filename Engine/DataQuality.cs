using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CtaBacktest
{
    // 歷史資料品質檢測（spec 3.2）
    public sealed class DataGap
    {
        public DateTime From, To;   // From = 缺口前最後一根，To = 缺口後第一根
        public double Days { get { return (To - From).TotalDays; } }
    }

    public sealed class TfQuality
    {
        public string Timeframe;
        public int Bars;
        public DateTime First, Last;
        public int RawRows, Duplicates, OutOfOrder;   // 讀原始 CSV 才知道（載入後已排序去重）
        public int BadOhlc, NonPositive, Misaligned, Flat, Spikes, Jumps;
        public List<DataGap> Gaps = new List<DataGap>();
        public List<string> Examples = new List<string>();   // 每類問題的第一個例子
        public double FlatPct { get { return Bars == 0 ? 0 : 100.0 * Flat / Bars; } }
    }

    public sealed class SymbolQuality
    {
        public string Symbol;
        public List<TfQuality> Frames = new List<TfQuality>();
        public int H1Compared, H1Mismatch;   // M1 合成 H1 與 H1 比對
        public string H1MismatchExample;
    }

    public static class DataQuality
    {
        public const double DefaultGapDays = 5;

        // 缺口門檻：分鐘線到 H4 用 gapDays；日線以上用 10 天（週線一根就 7 天）
        public static double GapThresholdDays(string tf, double gapDays)
        {
            int m = Timeframes.Minutes(tf);
            if (m >= 44640) return 70;
            if (m >= 10080) return 21;
            if (m >= 1440) return 10;
            return gapDays;
        }

        // 只找缺口（回測器設定頁用，快）
        public static List<DataGap> FindGaps(BarSeries s, DateTime from, DateTime to, double gapDays = DefaultGapDays)
        {
            var list = new List<DataGap>();
            double th = GapThresholdDays(s.Timeframe, gapDays);
            int a = Math.Max(1, s.FirstIndexAtOrAfter(from));
            for (int i = a; i < s.Count; i++)
            {
                if (s.Bars[i - 1].Time > to) break;
                if ((s.Bars[i].Time - s.Bars[i - 1].Time).TotalDays > th)
                    list.Add(new DataGap { From = s.Bars[i - 1].Time, To = s.Bars[i].Time });
            }
            return list;
        }

        public static SymbolQuality Check(SymbolData d, double tick, double gapDays = DefaultGapDays)
        {
            var q = new SymbolQuality { Symbol = d.Symbol };
            foreach (string tf in d.AvailableTimeframes())
                q.Frames.Add(CheckFrame(d, tf, tick, gapDays));
            if (d.HasTimeframe("M1") && d.HasTimeframe("H1")) CompareH1(d, tick, q);
            return q;
        }

        private static TfQuality CheckFrame(SymbolData d, string tf, double tick, double gapDays)
        {
            var r = new TfQuality { Timeframe = tf };
            // 原始檔：重複與順序（載入後已整理，這裡另外數）
            DateTime prev = DateTime.MinValue;
            foreach (string raw in File.ReadLines(Path.Combine(d.Folder, tf + ".csv"), Encoding.UTF8))
            {
                if (raw.Length == 0 || char.IsLetter(raw[0])) continue;
                int comma = raw.IndexOf(',');
                if (comma <= 0) continue;
                DateTime t;
                if (!DateTime.TryParseExact(raw.Substring(0, comma).Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                r.RawRows++;
                if (t == prev) r.Duplicates++;
                else if (t < prev) r.OutOfOrder++;
                prev = t;
            }
            var s = d.Load(tf);
            var b = s.Bars;
            r.Bars = b.Count;
            if (b.Count == 0) return r;
            r.First = b[0].Time; r.Last = b[b.Count - 1].Time;
            int minutes = Timeframes.Minutes(tf);
            double th = GapThresholdDays(tf, gapDays);
            // 中位數振幅，當作「正常大小」
            var ranges = new List<double>(b.Count);
            foreach (var x in b) if (x.High > x.Low) ranges.Add(x.High - x.Low);
            ranges.Sort();
            double med = ranges.Count > 0 ? ranges[ranges.Count / 2] : 0;
            double tol = Math.Max(Math.Abs(tick), 1e-12) * 0.5;
            string firstBad = null, firstSpike = null, firstJump = null, firstMis = null;
            // 對齊：H4 依券商一天的起點（例 UTC 22:00）切，不一定是 0 點；以最多數的餘數為準，和它不同才算沒對齊
            int mainRem = 0;
            if (minutes > 0 && minutes < 1440)
            {
                var cnt = new Dictionary<int, int>();
                foreach (var x in b) { int rem = ((int)x.Time.TimeOfDay.TotalMinutes) % minutes; int c0; cnt.TryGetValue(rem, out c0); cnt[rem] = c0 + 1; }
                mainRem = cnt.OrderByDescending(kv => kv.Value).First().Key;
            }
            for (int i = 0; i < b.Count; i++)
            {
                var x = b[i];
                if (x.Open <= 0 || x.High <= 0 || x.Low <= 0 || x.Close <= 0) r.NonPositive++;
                // LoadCsv 已把高低撐開到包住開收；原始值的檢查在 CheckRawOhlc
                if (x.Open == x.High && x.High == x.Low && x.Low == x.Close) r.Flat++;
                if (minutes > 0 && minutes < 1440 && ((int)x.Time.TimeOfDay.TotalMinutes) % minutes != mainRem)
                { r.Misaligned++; if (firstMis == null) firstMis = T(x.Time); }
                if (med > 0 && x.High - x.Low > med * 30) { r.Spikes++; if (firstSpike == null) firstSpike = T(x.Time) + " 振幅 " + (x.High - x.Low).ToString("G6", CultureInfo.InvariantCulture); }
                if (i > 0)
                {
                    var p = b[i - 1];
                    double days = (x.Time - p.Time).TotalDays;
                    if (days > th) r.Gaps.Add(new DataGap { From = p.Time, To = x.Time });
                    // 跳空只看連續交易中（相隔不到 3 根）；週末、休市後開盤的跳空是正常的
                    else if (med > 0 && Math.Abs(x.Open - p.Close) > med * 30 && minutes < 1440 && (x.Time - p.Time).TotalMinutes <= minutes * 3)
                    { r.Jumps++; if (firstJump == null) firstJump = T(x.Time) + " 開 " + x.Open.ToString("G6", CultureInfo.InvariantCulture) + "，前收 " + p.Close.ToString("G6", CultureInfo.InvariantCulture); }
                }
            }
            CheckRawOhlc(d, tf, tol, r, ref firstBad);
            if (firstBad != null) r.Examples.Add("開高低收不合理：" + firstBad);
            if (firstMis != null) r.Examples.Add("時間沒對齊週期：" + firstMis);
            if (firstSpike != null) r.Examples.Add("異常大 K 棒：" + firstSpike);
            if (firstJump != null) r.Examples.Add("異常跳空：" + firstJump);
            return r;
        }

        // 讀原始 CSV 的開高低收（LoadCsv 會修正，所以另外讀）
        private static void CheckRawOhlc(SymbolData d, string tf, double tol, TfQuality r, ref string firstBad)
        {
            foreach (string raw in File.ReadLines(Path.Combine(d.Folder, tf + ".csv"), Encoding.UTF8))
            {
                if (raw.Length == 0 || char.IsLetter(raw[0])) continue;
                string[] p = raw.Split(',');
                if (p.Length < 5) continue;
                double o, h, l, c;
                if (!N(p[1], out o) || !N(p[2], out h) || !N(p[3], out l) || !N(p[4], out c)) { r.BadOhlc++; continue; }
                if (h + tol < Math.Max(o, c) || l - tol > Math.Min(o, c) || h + tol < l)
                {
                    r.BadOhlc++;
                    if (firstBad == null) firstBad = p[0] + " 開 " + p[1] + " 高 " + p[2] + " 低 " + p[3] + " 收 " + p[4];
                }
            }
        }

        // M1 合成 H1，和 H1 比對兩邊都有的時間
        private static void CompareH1(SymbolData d, double tick, SymbolQuality q)
        {
            var m1 = d.Load("M1").Bars;
            var h1 = d.Load("H1");
            double tol = Math.Max(Math.Abs(tick), 1e-12) * 2.01;
            int i = 0;
            while (i < m1.Count)
            {
                DateTime hour = new DateTime(m1[i].Time.Year, m1[i].Time.Month, m1[i].Time.Day, m1[i].Time.Hour, 0, 0, DateTimeKind.Utc);
                double o = m1[i].Open, hi = m1[i].High, lo = m1[i].Low, c = m1[i].Close;
                int j = i + 1;
                while (j < m1.Count && m1[j].Time < hour.AddHours(1)) { hi = Math.Max(hi, m1[j].High); lo = Math.Min(lo, m1[j].Low); c = m1[j].Close; j++; }
                int k = h1.IndexAtOrBefore(hour);
                if (k >= 0 && h1.Bars[k].Time == hour)
                {
                    var h = h1.Bars[k];
                    q.H1Compared++;
                    if (Math.Abs(h.Open - o) > tol || Math.Abs(h.High - hi) > tol || Math.Abs(h.Low - lo) > tol || Math.Abs(h.Close - c) > tol)
                    {
                        q.H1Mismatch++;
                        if (q.H1MismatchExample == null)
                            q.H1MismatchExample = T(hour) + " H1 開高低收 " + F(h.Open) + "/" + F(h.High) + "/" + F(h.Low) + "/" + F(h.Close)
                                + "，M1 合成 " + F(o) + "/" + F(hi) + "/" + F(lo) + "/" + F(c);
                    }
                }
                i = j;
            }
        }

        // 一個商品的 Markdown 段落
        public static string ToMarkdown(SymbolQuality q)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### " + q.Symbol);
            sb.AppendLine();
            sb.AppendLine("| 週期 | 根數 | 起 | 迄 | 缺口 | 最大缺口 | 重複 | 順序錯 | 開高低收錯 | 時間沒對齊 | 平盤棒 | 異常大K | 異常跳空 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var f in q.Frames)
            {
                var big = f.Gaps.OrderByDescending(g => g.Days).FirstOrDefault();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1:N0} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10:F1}% | {11} | {12} |",
                    f.Timeframe, f.Bars, D(f.First), D(f.Last), f.Gaps.Count,
                    big == null ? "—" : D(big.From) + "～" + D(big.To) + "（" + big.Days.ToString("F0", CultureInfo.InvariantCulture) + " 天）",
                    f.Duplicates, f.OutOfOrder, f.BadOhlc, f.Misaligned, f.FlatPct, f.Spikes, f.Jumps));
            }
            sb.AppendLine();
            foreach (var f in q.Frames)
            {
                foreach (var g in f.Gaps)
                    sb.AppendLine("- " + f.Timeframe + " 缺口：" + T(g.From) + " → " + T(g.To) + "（" + g.Days.ToString("F1", CultureInfo.InvariantCulture) + " 天）");
                foreach (var e in f.Examples) sb.AppendLine("- " + f.Timeframe + " " + e);
            }
            if (q.H1Compared > 0)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "- M1 合成 H1 與 H1 比對 {0:N0} 根，差超過 2 個跳動點的 {1:N0} 根（{2:F2}%）{3}",
                    q.H1Compared, q.H1Mismatch, 100.0 * q.H1Mismatch / q.H1Compared, q.H1MismatchExample == null ? "" : "，例：" + q.H1MismatchExample));
            sb.AppendLine();
            return sb.ToString();
        }

        private static bool N(string s, out double v) { return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }
        private static string T(DateTime t) { return t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        private static string D(DateTime t) { return t == DateTime.MinValue ? "—" : t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static string F(double v) { return v.ToString("G7", CultureInfo.InvariantCulture); }
    }
}
