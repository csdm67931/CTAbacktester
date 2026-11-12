using System;
using System.Collections.Generic;
using Runtime.Script;
using TradeApi.History;

// 模擬交易王內建指標（IndicatorsManager.BuildIn）。方法簽名照真平台；
// 公式照 MT5 慣例，未與交易王逐值核對（spec.md 第 8 節）。
// 沒實作的指標（ADX、Stochastic 等）用到時編譯會報錯。

namespace TradeApi.Indicators
{
    public enum MAMode { SMA = 0, EMA = 1, SMMA = 2, LWMA = 3 }
    public enum RSIMode { Simple = 0, Exponential = 1 }

    public sealed class IndicatorsManager
    {
        private readonly CSharpScript script;
        private readonly BuiltInIndicators buildIn;
        internal IndicatorsManager(CSharpScript script)
        {
            this.script = script;
            buildIn = new BuiltInIndicators(script);
        }
        public BuiltInIndicators BuildIn { get { return buildIn; } }
    }

    public sealed class BuiltInLine
    {
        private readonly BuiltInIndicator owner;
        private readonly int line;
        internal BuiltInLine(BuiltInIndicator owner, int line) { this.owner = owner; this.line = line; }
        public double GetValue(int offset = 0) { return owner.GetValue(offset, line); }
        public double this[int offset] { get { return owner.GetValue(offset, line); } }
    }

    public sealed class BuiltInLines
    {
        private readonly BuiltInLine[] lines;
        internal BuiltInLines(BuiltInIndicator owner, int count)
        {
            lines = new BuiltInLine[count];
            for (int i = 0; i < count; i++) lines[i] = new BuiltInLine(owner, i);
        }
        public int Count { get { return lines.Length; } }
        public BuiltInLine this[int index] { get { return lines[index]; } }
    }

    public sealed class BuiltInIndicator
    {
        private readonly LocalHistoricalData data;
        private readonly Calc calc;
        private readonly int shift;
        private readonly List<double[]> memo = new List<double[]>();   // 已走完的 K 棒算過就存（值不會再變）

        internal BuiltInIndicator(string name, LocalHistoricalData data, Calc calc, int shift)
        {
            Name = name;
            this.data = data;
            this.calc = calc;
            this.shift = Math.Max(0, shift);
            calc.Owner = this;
            Lines = new BuiltInLines(this, calc.LineCount);
        }

        public string Name { get; private set; }
        public int Count { get { return data.Count; } }
        public HistoricalData HistoryDataSeries { get { return data; } }
        public BuiltInLines Lines { get; private set; }

        public double GetValue(int offset = 0, int lineNumber = 0)
        {
            if (lineNumber < 0 || lineNumber >= calc.LineCount || offset < 0) return double.NaN;
            int forming = data.FormingAbs;
            int abs = forming - offset - shift;
            if (abs < 0 || forming < 0) return double.NaN;
            if (abs < forming) return Completed(abs)[lineNumber];
            if (forming > 0) Completed(forming - 1);
            return calc.Compute(abs)[lineNumber];
        }

        // 給計算用：取第 abs 根（由舊到新）已算好的值
        internal double Prev(int abs, int line)
        {
            if (abs < 0) return double.NaN;
            if (abs < data.FormingAbs) return Completed(abs)[line];
            return calc.Compute(abs)[line];
        }

        private double[] Completed(int abs)
        {
            while (memo.Count <= abs) memo.Add(calc.Compute(memo.Count));
            return memo[abs];
        }
    }

    // 指標計算：Compute(abs) 只能用 Owner.Prev(abs-1...) 與 Price(abs...)，由舊到新依序被呼叫
    internal abstract class Calc
    {
        internal BuiltInIndicator Owner;
        internal abstract int LineCount { get; }
        internal abstract double[] Compute(int abs);
    }

    // 價格來源：資料欄位或使用者的委派（委派參數是 offset，0 = 最新）
    internal sealed class Source
    {
        private readonly LocalHistoricalData data;
        private readonly PriceType type;
        private readonly Func<int, double> func;
        internal Source(LocalHistoricalData data, PriceType type, Func<int, double> func)
        {
            this.data = data;
            this.type = type;
            this.func = func;
        }
        internal double At(int abs)
        {
            if (abs < 0) return double.NaN;
            if (func != null) return func(data.FormingAbs - abs);
            return data.ValueAbs(type, abs);
        }
    }

    internal static class MaMath
    {
        // 不遞迴的均線（SMA、LWMA），以及 SMMA、EMA 的第一個值
        internal static double Window(Func<int, double> p, int abs, int period, MAMode mode)
        {
            if (period <= 0 || abs - period + 1 < 0) return double.NaN;
            double sum = 0, wsum = 0;
            for (int k = 0; k < period; k++)
            {
                double v = p(abs - k);
                if (double.IsNaN(v)) return double.NaN;
                double w = mode == MAMode.LWMA ? period - k : 1;
                sum += v * w;
                wsum += w;
            }
            return sum / wsum;
        }

        // 遞迴均線：EMA 第一個值用第一根價格（MT5），SMMA 第一個值用 SMA
        internal static double Next(Func<int, double> p, int abs, int period, MAMode mode, double prev)
        {
            switch (mode)
            {
                case MAMode.SMA:
                case MAMode.LWMA:
                    return Window(p, abs, period, mode);
                case MAMode.EMA:
                {
                    double v = p(abs);
                    if (double.IsNaN(prev)) return v;
                    double a = 2.0 / (period + 1);
                    return double.IsNaN(v) ? double.NaN : prev + a * (v - prev);
                }
                default:
                {
                    if (double.IsNaN(prev)) return Window(p, abs, period, MAMode.SMA);
                    double v = p(abs);
                    return double.IsNaN(v) ? double.NaN : (prev * (period - 1) + v) / period;
                }
            }
        }
    }

    internal sealed class MaCalc : Calc
    {
        private readonly Source src; private readonly int period; private readonly MAMode mode;
        internal MaCalc(Source src, int period, MAMode mode) { this.src = src; this.period = period; this.mode = mode; }
        internal override int LineCount { get { return 1; } }
        internal override double[] Compute(int abs)
        {
            double prev = abs > 0 ? Owner.Prev(abs - 1, 0) : double.NaN;
            return new[] { MaMath.Next(src.At, abs, period, mode, prev) };
        }
    }

    // ATR：真實區間的均線（預設 SMA，與 MT5 相同）
    internal sealed class AtrCalc : Calc
    {
        private readonly LocalHistoricalData d; private readonly int period; private readonly MAMode mode;
        internal AtrCalc(LocalHistoricalData d, int period, MAMode mode) { this.d = d; this.period = period; this.mode = mode; }
        internal override int LineCount { get { return 1; } }
        private double Tr(int abs)
        {
            double h = d.ValueAbs(PriceType.High, abs), l = d.ValueAbs(PriceType.Low, abs);
            if (abs == 0) return h - l;
            double pc = d.ValueAbs(PriceType.Close, abs - 1);
            return Math.Max(h, pc) - Math.Min(l, pc);
        }
        internal override double[] Compute(int abs)
        {
            double prev = abs > 0 ? Owner.Prev(abs - 1, 0) : double.NaN;
            return new[] { MaMath.Next(Tr, abs, period, mode, prev) };
        }
    }

    // RSI：Simple = 漲跌幅 SMA；Exponential = Wilder 平滑（MT5 的 RSI）
    internal sealed class RsiCalc : Calc
    {
        private readonly Source src; private readonly int period; private readonly RSIMode mode;
        internal RsiCalc(Source src, int period, RSIMode mode) { this.src = src; this.period = period; this.mode = mode; }
        internal override int LineCount { get { return 3; } }   // 0 = RSI；1、2 = 內部用的平均漲幅、跌幅
        private double Up(int abs) { if (abs < 1) return double.NaN; double c = src.At(abs) - src.At(abs - 1); return c > 0 ? c : 0; }
        private double Dn(int abs) { if (abs < 1) return double.NaN; double c = src.At(abs - 1) - src.At(abs); return c > 0 ? c : 0; }
        internal override double[] Compute(int abs)
        {
            double up, dn;
            if (mode == RSIMode.Simple)
            {
                up = MaMath.Window(Up, abs, period, MAMode.SMA);
                dn = MaMath.Window(Dn, abs, period, MAMode.SMA);
            }
            else
            {
                double pu = abs > 0 ? Owner.Prev(abs - 1, 1) : double.NaN;
                double pd = abs > 0 ? Owner.Prev(abs - 1, 2) : double.NaN;
                up = MaMath.Next(Up, abs, period, MAMode.SMMA, pu);
                dn = MaMath.Next(Dn, abs, period, MAMode.SMMA, pd);
            }
            double rsi = double.IsNaN(up) || double.IsNaN(dn) ? double.NaN : dn == 0 ? (up == 0 ? 50 : 100) : 100 - 100 / (1 + up / dn);
            return new[] { rsi, up, dn };
        }
    }

    // 標準差：價格對均線的母體標準差
    internal sealed class StdDevCalc : Calc
    {
        private readonly Source src; private readonly int period; private readonly MAMode mode;
        internal StdDevCalc(Source src, int period, MAMode mode) { this.src = src; this.period = period; this.mode = mode; }
        internal override int LineCount { get { return 2; } }   // 0 = 標準差；1 = 內部用的均線
        internal override double[] Compute(int abs)
        {
            double prev = abs > 0 ? Owner.Prev(abs - 1, 1) : double.NaN;
            double ma = MaMath.Next(src.At, abs, period, mode, prev);
            if (double.IsNaN(ma) || abs - period + 1 < 0) return new[] { double.NaN, ma };
            double s = 0;
            for (int k = 0; k < period; k++) { double v = src.At(abs - k) - ma; s += v * v; }
            return new[] { Math.Sqrt(s / period), ma };
        }
    }

    // 布林通道：線 0 = 中線、1 = 上軌、2 = 下軌（MT5 順序，交易王順序未確認）
    internal sealed class BandsCalc : Calc
    {
        private readonly Source src; private readonly int period; private readonly MAMode mode; private readonly double dev;
        internal BandsCalc(Source src, int period, MAMode mode, double dev) { this.src = src; this.period = period; this.mode = mode; this.dev = dev; }
        internal override int LineCount { get { return 3; } }
        internal override double[] Compute(int abs)
        {
            double prev = abs > 0 ? Owner.Prev(abs - 1, 0) : double.NaN;
            double mid = MaMath.Next(src.At, abs, period, mode, prev);
            if (double.IsNaN(mid) || abs - period + 1 < 0) return new[] { mid, double.NaN, double.NaN };
            double s = 0;
            for (int k = 0; k < period; k++) { double v = src.At(abs - k) - mid; s += v * v; }
            double sd = Math.Sqrt(s / period);
            return new[] { mid, mid + dev * sd, mid - dev * sd };
        }
    }

    // MACD：線 0 = 快 EMA − 慢 EMA，線 1 = 訊號線（主線的 SMA，MT5 做法）
    internal sealed class MacdCalc : Calc
    {
        private readonly Source src; private readonly int fast, slow, signal;
        internal MacdCalc(Source src, int fast, int slow, int signal) { this.src = src; this.fast = fast; this.slow = slow; this.signal = signal; }
        internal override int LineCount { get { return 4; } }   // 2、3 = 內部用的快、慢 EMA
        internal override double[] Compute(int abs)
        {
            double pf = abs > 0 ? Owner.Prev(abs - 1, 2) : double.NaN;
            double ps = abs > 0 ? Owner.Prev(abs - 1, 3) : double.NaN;
            double f = MaMath.Next(src.At, abs, fast, MAMode.EMA, pf);
            double s = MaMath.Next(src.At, abs, slow, MAMode.EMA, ps);
            double main = f - s;
            double sig = double.NaN;
            if (abs - signal + 1 >= 0)
            {
                double sum = main;
                for (int k = 1; k < signal; k++) sum += Owner.Prev(abs - k, 0);
                sig = sum / signal;
            }
            return new[] { main, sig, f, s };
        }
    }

    // 動量：價格 ÷ period 根前價格 × 100（MT5）
    internal sealed class MomentumCalc : Calc
    {
        private readonly Source src; private readonly int period;
        internal MomentumCalc(Source src, int period) { this.src = src; this.period = period; }
        internal override int LineCount { get { return 1; } }
        internal override double[] Compute(int abs)
        {
            if (abs - period < 0) return new[] { double.NaN };
            double b = src.At(abs - period);
            return new[] { b == 0 ? double.NaN : src.At(abs) / b * 100 };
        }
    }

    // CCI：(價格 − 均線) ÷ (0.015 × 平均絕對偏差)
    internal sealed class CciCalc : Calc
    {
        private readonly Source src; private readonly int period; private readonly MAMode mode;
        internal CciCalc(Source src, int period, MAMode mode) { this.src = src; this.period = period; this.mode = mode; }
        internal override int LineCount { get { return 2; } }
        internal override double[] Compute(int abs)
        {
            double prev = abs > 0 ? Owner.Prev(abs - 1, 1) : double.NaN;
            double ma = MaMath.Next(src.At, abs, period, mode, prev);
            if (double.IsNaN(ma) || abs - period + 1 < 0) return new[] { double.NaN, ma };
            double md = 0;
            for (int k = 0; k < period; k++) md += Math.Abs(src.At(abs - k) - ma);
            md /= period;
            return new[] { md == 0 ? 0 : (src.At(abs) - ma) / (0.015 * md), ma };
        }
    }

    public sealed class BuiltInIndicators
    {
        private readonly CSharpScript script;
        internal BuiltInIndicators(CSharpScript script) { this.script = script; }

        private LocalHistoricalData D(HistoricalData data)
        {
            var d = (data ?? script.RequireTester().Chart) as LocalHistoricalData;
            if (d == null) throw new ArgumentException("資料來源必須是回測提供的 HistoryDataSeries 或 HistoricalDataManager.Get 的結果");
            return d;
        }

        private BuiltInIndicator Make(string name, LocalHistoricalData d, Calc c, int shift = 0)
        {
            return new BuiltInIndicator(name, d, c, shift);
        }

        // ---- MA ----
        public BuiltInIndicator MA(HistoricalData data, int period, MAMode mode)
        {
            return MA(data, period, mode, 0, PriceType.Close);
        }
        public BuiltInIndicator MA(HistoricalData data, int period, MAMode mode, int ma_shift, PriceType priceType)
        {
            var d = D(data);
            return Make("MA", d, new MaCalc(new Source(d, priceType, null), period, mode), ma_shift);
        }
        public BuiltInIndicator MA(Func<int, double> func, int period, MAMode mode)
        {
            return MA(func, period, mode, 0);
        }
        public BuiltInIndicator MA(Func<int, double> func, int period, MAMode mode, int ma_shift)
        {
            var d = D(null);
            return Make("MA", d, new MaCalc(new Source(d, PriceType.Close, func), period, mode), ma_shift);
        }
        public BuiltInIndicator MA(Func<int, double> func, int period, MAMode mode, int ma_shift, int startWithBarIndex)
        {
            return MA(func, period, mode, ma_shift);
        }

        // ---- ATR ----
        public BuiltInIndicator ATR(HistoricalData data, int period) { return ATR(data, period, MAMode.SMA); }
        public BuiltInIndicator ATR(HistoricalData data, int period, MAMode mode)
        {
            var d = D(data);
            return Make("ATR", d, new AtrCalc(d, period, mode));
        }

        // ---- RSI ----
        public BuiltInIndicator RSI(HistoricalData data, int period) { return RSI(data, period, RSIMode.Exponential, PriceType.Close); }
        public BuiltInIndicator RSI(HistoricalData data, int period, RSIMode mode) { return RSI(data, period, mode, PriceType.Close); }
        public BuiltInIndicator RSI(HistoricalData data, int period, RSIMode mode, PriceType priceType)
        {
            var d = D(data);
            return Make("RSI", d, new RsiCalc(new Source(d, priceType, null), period, mode));
        }
        public BuiltInIndicator RSI(Func<int, double> func, int period, RSIMode mode)
        {
            var d = D(null);
            return Make("RSI", d, new RsiCalc(new Source(d, PriceType.Close, func), period, mode));
        }

        // ---- Bands ----
        public BuiltInIndicator Bands(HistoricalData data, int period) { return Bands(data, period, MAMode.SMA, 2.0, PriceType.Close); }
        public BuiltInIndicator Bands(HistoricalData data, int period, MAMode mode) { return Bands(data, period, mode, 2.0, PriceType.Close); }
        public BuiltInIndicator Bands(HistoricalData data, int period, MAMode mode, double deviation) { return Bands(data, period, mode, deviation, PriceType.Close); }
        public BuiltInIndicator Bands(HistoricalData data, int period, MAMode mode, double deviation, PriceType priceType)
        {
            var d = D(data);
            return Make("Bands", d, new BandsCalc(new Source(d, priceType, null), period, mode, deviation));
        }
        public BuiltInIndicator Bands(HistoricalData data, Func<int, double> func, int period, MAMode mode, double deviation)
        {
            var d = D(data);
            return Make("Bands", d, new BandsCalc(new Source(d, PriceType.Close, func), period, mode, deviation));
        }

        // ---- MACD ----
        public BuiltInIndicator MACD(HistoricalData data, int fast_ema_period, int slow_ema_period, int signal_period)
        {
            return MACD(data, fast_ema_period, slow_ema_period, signal_period, PriceType.Close);
        }
        public BuiltInIndicator MACD(HistoricalData data, int fast_ema_period, int slow_ema_period, int signal_period, PriceType priceType)
        {
            var d = D(data);
            return Make("MACD", d, new MacdCalc(new Source(d, priceType, null), fast_ema_period, slow_ema_period, signal_period));
        }

        // ---- StdDev ----
        public BuiltInIndicator StdDev(HistoricalData data, int period, MAMode mode, PriceType priceType)
        {
            return StdDev(data, period, mode, 0, priceType);
        }
        public BuiltInIndicator StdDev(HistoricalData data, int period, MAMode mode, int ma_shift, PriceType priceType)
        {
            var d = D(data);
            return Make("StdDev", d, new StdDevCalc(new Source(d, priceType, null), period, mode), ma_shift);
        }
        public BuiltInIndicator StdDev(Func<int, double> func, int period, MAMode mode)
        {
            var d = D(null);
            return Make("StdDev", d, new StdDevCalc(new Source(d, PriceType.Close, func), period, mode));
        }

        // ---- Momentum ----
        public BuiltInIndicator Momentum(HistoricalData data, int period) { return Momentum(data, period, PriceType.Close); }
        public BuiltInIndicator Momentum(HistoricalData data, int period, PriceType priceType)
        {
            var d = D(data);
            return Make("Momentum", d, new MomentumCalc(new Source(d, priceType, null), period));
        }
        public BuiltInIndicator Momentum(Func<int, double> func, int period)
        {
            var d = D(null);
            return Make("Momentum", d, new MomentumCalc(new Source(d, PriceType.Close, func), period));
        }

        // ---- CCI ----
        public BuiltInIndicator CCI(HistoricalData data, int period) { return CCI(data, period, MAMode.SMA, PriceType.Typical); }
        public BuiltInIndicator CCI(HistoricalData data, int period, MAMode mode) { return CCI(data, period, mode, PriceType.Typical); }
        public BuiltInIndicator CCI(HistoricalData data, int period, MAMode mode, PriceType priceType)
        {
            var d = D(data);
            return Make("CCI", d, new CciCalc(new Source(d, priceType, null), period, mode));
        }
        public BuiltInIndicator CCI(HistoricalData data, Func<int, double> func, int period, MAMode mode)
        {
            var d = D(data);
            return Make("CCI", d, new CciCalc(new Source(d, PriceType.Close, func), period, mode));
        }
    }
}
