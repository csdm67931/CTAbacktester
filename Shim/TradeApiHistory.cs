using System;
using System.Collections.Generic;
using CtaBacktest;
using TradeApi.Instruments;

// 模擬交易王 TradeApi.History（列舉值照真平台）

namespace TradeApi.History
{
    public enum Period { Second = 1, Minute = 2, Hour = 3, Day = 4, Week = 5, Month = 6, Year = 7 }
    public enum DataType { Bid = 1, Trade = 4, Ask = 1000 }
    public enum PriceType { Open = 0, Close = 1, High = 2, Low = 3, Medium = 4, Typical = 5, Weighted = 6 }

    public sealed class Interval
    {
        public Interval(DateTime fromUtc, DateTime toUtc) { FromUtc = fromUtc; ToUtc = toUtc; }
        public DateTime FromUtc { get; private set; }
        public DateTime ToUtc { get; private set; }
    }

    public abstract class HistoricalRequest
    {
        protected HistoricalRequest(Instrument instrument, DataType dataType)
        {
            Instrument = instrument;
            DataType = dataType;
        }
        public DataType DataType { get; private set; }
        public Instrument Instrument { get; private set; }
        public bool LoadExtendedSession { get; set; }
        public bool SubscribeToQuotes { get; set; }
        public abstract HistoricalRequest GetRequest(Instrument instrument);
    }

    public sealed class TimeHistoricalRequest : HistoricalRequest
    {
        public TimeHistoricalRequest(Instrument instrument, DataType dataType, Period period, int value)
            : base(instrument, dataType)
        {
            Period = period;
            Value = value;
        }
        public Period Period { get; private set; }
        public int Value { get; private set; }
        public override HistoricalRequest GetRequest(Instrument instrument)
        {
            return new TimeHistoricalRequest(instrument, DataType, Period, Value);
        }
    }

    // 索引方向照平台：0 = 最新（進行中），越大越舊
    public abstract class HistoricalData
    {
        internal HistoricalData() { }
        public abstract int Count { get; }
        public abstract HistoricalRequest HistoricalRequest { get; }
        public abstract DateTime GetTimeUtc(int offset = 0);
        public abstract double GetValue(PriceType priceType, int offset = 0);

        // 時間落在第幾根（offset）；比最舊還早回傳 -1
        public int FindInterval(DateTime timeUtc)
        {
            for (int i = 0; i < Count; i++) if (GetTimeUtc(i) <= timeUtc) return i;
            return -1;
        }

        internal static double Pick(PriceType t, double o, double h, double l, double c)
        {
            switch (t)
            {
                case PriceType.Open: return o;
                case PriceType.High: return h;
                case PriceType.Low: return l;
                case PriceType.Close: return c;
                case PriceType.Medium: return (h + l) / 2;
                case PriceType.Typical: return (h + l + c) / 3;
                case PriceType.Weighted: return (h + l + c + c) / 4;
                default: return c;
            }
        }
    }

    // 回測用的 K 棒序列：只看得到開盤時間 <= 目前模擬時間的 K 棒；
    // 已走完的 K 棒用檔案的值，進行中那根的高低收只用到目前為止的報價，不會偷看未來。
    internal sealed class LocalHistoricalData : HistoricalData
    {
        private readonly BarSeries src;
        private readonly HistoricalRequest request;
        private readonly int baseIndex;   // 請求區間起點之前的 K 棒不給看
        internal int Forming = -1;        // 進行中那根在 src 的索引；-1 = 還沒有
        private double fo, fh, fl, fc;

        internal LocalHistoricalData(BarSeries src, HistoricalRequest request, int baseIndex)
        {
            this.src = src;
            this.request = request;
            this.baseIndex = Math.Max(0, baseIndex);
        }

        internal BarSeries Source { get { return src; } }

        public override int Count { get { return Forming < baseIndex ? 0 : Forming - baseIndex + 1; } }
        public override HistoricalRequest HistoricalRequest { get { return request; } }

        // 把進行中的 K 棒定在 index，高低收用已知的報價區間
        internal void SetForming(int index, double high, double low, double close)
        {
            Forming = index;
            if (index < 0 || index >= src.Count) return;
            fo = src.Bars[index].Open;
            fh = double.IsNaN(high) ? fo : Math.Max(fo, high);
            fl = double.IsNaN(low) ? fo : Math.Min(fo, low);
            fc = double.IsNaN(close) ? fo : close;
        }

        // 收到一個報價；有換新 K 棒時回傳 true
        internal bool OnTick(DateTime time, double price)
        {
            bool newBar = false;
            while (Forming + 1 < src.Count && src.Bars[Forming + 1].Time <= time)
            {
                Forming++;
                newBar = true;
            }
            if (Forming < 0) return false;
            if (newBar)
            {
                fo = src.Bars[Forming].Open;
                fh = Math.Max(fo, price);
                fl = Math.Min(fo, price);
                fc = price;
            }
            else
            {
                if (price > fh) fh = price;
                if (price < fl) fl = price;
                fc = price;
            }
            return newBar;
        }

        // 以「由舊到新的絕對位置」取值（0 = 可見的最舊一根）；給內建指標用
        internal int FormingAbs { get { return Forming - baseIndex; } }

        internal double ValueAbs(PriceType t, int abs)
        {
            int i = abs + baseIndex;
            if (abs < 0 || i > Forming) return double.NaN;
            if (i == Forming) return Pick(t, fo, fh, fl, fc);
            Bar b = src.Bars[i];
            return Pick(t, b.Open, b.High, b.Low, b.Close);
        }

        public override double GetValue(PriceType priceType, int offset = 0)
        {
            if (offset < 0) return double.NaN;
            return ValueAbs(priceType, FormingAbs - offset);
        }

        public override DateTime GetTimeUtc(int offset = 0)
        {
            int i = Forming - offset;
            if (offset < 0 || i < baseIndex || i > Forming) return DateTime.MinValue;
            return src.Bars[i].Time;
        }
    }

    public sealed class HistoricalDataManager
    {
        private readonly Runtime.Script.CSharpScript script;
        internal HistoricalDataManager(Runtime.Script.CSharpScript script) { this.script = script; }

        public Action<HistoricalData> OnLoaded;
        public Action<HistoricalData> OnRemoved;

        // 回傳本地資料：只含目前模擬時間以前的 K 棒；OnLoaded 在下一次 Update 前觸發
        public HistoricalData Get(HistoricalRequest request, Interval interval)
        {
            return script.RequireTester().GetHistory(this, request, interval);
        }

        public void Remove(HistoricalData historicalData)
        {
            script.RequireTester().RemoveHistory(historicalData);
            var h = OnRemoved;
            if (h != null) h(historicalData);
        }

        public void ChangeInterval(HistoricalData historicalData, Interval interval)
        {
            script.RequireTester().Log("ChangeInterval 在回測中不支援，已忽略");
        }
    }
}
