using System;
using System.Collections.Generic;

namespace CtaBacktest
{
    // K 線圖上一筆交易的標記位置：進出場落在第幾根 K 棒（spec 9 節）
    public sealed class TradeMark
    {
        public TradeRecord Trade;
        public int EntryIndex, ExitIndex;   // 在 ChartData.Bars 裡的索引；-1 = 不在圖的範圍內
    }

    // K 線圖上的一條疊加線（均線或通道）
    public sealed class OverlayLine
    {
        public string Name;
        public int ColorSlot;      // 第幾個週期欄，決定顏色
        public bool Dashed;
        public double[] Values;    // 對齊 ChartData.Bars；NaN = 不畫
    }

    public sealed class ChartData
    {
        public string Timeframe;
        public List<Bar> Bars = new List<Bar>();
        public List<TradeMark> Marks = new List<TradeMark>();
        public int PriceDigits = 5;
        public double Spread;
        public BarSeries Source;    // 整段歷史，算均線用（回測開始前的 K 棒也算進去）
        public int Offset;          // Bars[0] 在 Source 裡的索引
        public List<OverlayLine> Lines = new List<OverlayLine>();
        // 回放用（spec.md 14）：資金曲線（每根結束時的餘額、淨值）、回測第一根在 Bars 的位置
        public List<EquityPoint> Curve;
        public double InitialDeposit;
        public int StartIndex;
        public DateTime FirstBarTime;

        public bool CanReplay { get { return Bars.Count > 0 && Curve != null && Curve.Count > 0; } }

        // 第 i 根結束時的餘額與淨值；回測開始前（暖機段）是初始資金
        public void EquityAt(int i, out double balance, out double equity)
        {
            balance = equity = InitialDeposit;
            if (Curve == null || Curve.Count == 0 || i < 0 || i >= Bars.Count || Bars[i].Time < FirstBarTime) return;
            // 曲線點的時間是 K 棒結束時間：找第一個晚於這根開盤時間的點
            DateTime t = Bars[i].Time;
            int lo = 0, hi = Curve.Count - 1, found = Curve.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Curve[mid].Time > t) { found = mid; hi = mid - 1; } else lo = mid + 1;
            }
            balance = Curve[found].Balance;
            equity = Curve[found].Equity;
        }

        // 依週期重算均線；periods 裡 0 或負數略過；bandPct > 0 時在第一條均線上下畫通道
        public void SetSma(IList<int> periods, double bandPct)
        {
            Lines.Clear();
            if (Source == null) return;
            bool firstLine = true;
            int slot = 0;
            foreach (int p in periods)
            {
                int color = slot++;
                if (p <= 0) continue;
                double[] v = Sma(p);
                Lines.Add(new OverlayLine { Name = "SMA" + p, ColorSlot = color, Values = v });
                if (firstLine && bandPct > 0)
                {
                    double up = 1 + bandPct / 100.0, dn = 1 - bandPct / 100.0;
                    var hi = new double[v.Length]; var lo = new double[v.Length];
                    for (int i = 0; i < v.Length; i++) { hi[i] = v[i] * up; lo[i] = v[i] * dn; }
                    string tag = "SMA" + p + " +" + bandPct.ToString("0.##") + "%";
                    Lines.Add(new OverlayLine { Name = tag, ColorSlot = color, Values = hi, Dashed = true });
                    Lines.Add(new OverlayLine { Name = "SMA" + p + " -" + bandPct.ToString("0.##") + "%", ColorSlot = color, Values = lo, Dashed = true });
                }
                firstLine = false;
            }
        }

        // 收盤價簡單均線，對齊 Bars；歷史不足的位置是 NaN
        public double[] Sma(int period)
        {
            var v = new double[Bars.Count];
            var src = Source.Bars;
            // 滾動加總（M1 長期間根數多）；起點往前補 period 根
            int start = Math.Max(0, Offset - period + 1);
            double sum = 0;
            for (int j = start; j < Offset + v.Length && j < src.Count; j++)
            {
                sum += src[j].Close;
                if (j - period >= start) sum -= src[j - period].Close;
                int i = j - Offset;
                if (i < 0) continue;
                v[i] = j - period + 1 >= 0 ? sum / period : double.NaN;
            }
            return v;
        }

        // 從策略參數猜要畫的均線：說明含「均線」、名稱以 Period 結尾的整數 -> 週期；說明含「均線」且含「%」-> 通道
        public static bool SuggestSma(BacktestResult r, List<int> periods, out double bandPct)
        {
            bandPct = 0;
            periods.Clear();
            if (r == null) return false;
            foreach (var kv in r.Parameters)
            {
                string label;
                if (!r.ParamLabels.TryGetValue(kv.Key, out label) || label == null || label.IndexOf("均線", StringComparison.Ordinal) < 0) continue;
                double v;
                if (!double.TryParse(kv.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                if (label.IndexOf('%') >= 0) { if (v > 0) bandPct = v; }
                else if (kv.Key.EndsWith("Period", StringComparison.Ordinal) && v >= 1 && v == Math.Floor(v) && periods.Count < 4) periods.Add((int)v);
            }
            return periods.Count > 0;
        }

        // 取回測期間的 K 棒（前面多留一點暖機段當背景），把每筆交易對應到 K 棒
        public static ChartData Build(BarSeries series, BacktestResult r, int lead = 50)
        {
            var cd = new ChartData { Timeframe = series.Timeframe };
            if (r.Settings != null) { cd.PriceDigits = r.Settings.PriceDigits; cd.Spread = r.Settings.Spread; }
            if (series.Count == 0) return cd;
            int first = series.IndexAtOrBefore(r.FirstBar);
            if (first < 0) first = 0;
            first = Math.Max(0, first - lead);
            int last = series.IndexAtOrBefore(r.LastBar);
            if (last < 0) last = series.Count - 1;
            // 最後一筆可能在 LastBar 之後才平倉（測試結束）：多包到它
            foreach (var t in r.Trades)
            {
                int k = series.IndexAtOrBefore(t.CloseTime);
                if (k > last) last = k;
            }
            for (int i = first; i <= last; i++) cd.Bars.Add(series.Bars[i]);
            cd.Source = series;
            cd.Offset = first;
            var sub = new BarSeries(series.Timeframe, cd.Bars);
            cd.Curve = r.Curve;
            cd.InitialDeposit = r.Settings != null ? r.Settings.InitialDeposit : 0;
            cd.FirstBarTime = r.FirstBar;
            cd.StartIndex = Math.Max(0, sub.IndexAtOrBefore(r.FirstBar));
            foreach (var t in r.Trades)
                cd.Marks.Add(new TradeMark { Trade = t, EntryIndex = sub.IndexAtOrBefore(t.OpenTime), ExitIndex = sub.IndexAtOrBefore(t.CloseTime) });
            return cd;
        }

        // 檢測用：價格是否落在該根 K 棒的高低範圍（買價可能高出賣價一個點差，所以上下各容許點差與一個跳動點）
        public bool PriceInBar(int index, double price, double tick)
        {
            if (index < 0 || index >= Bars.Count) return false;
            double tol = Math.Abs(Spread) + Math.Abs(tick) + 1e-9;
            return price >= Bars[index].Low - tol && price <= Bars[index].High + tol;
        }
    }
}
