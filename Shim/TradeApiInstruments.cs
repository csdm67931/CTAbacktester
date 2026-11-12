using System;
using System.Collections.Generic;
using CtaBacktest;

namespace TradeApi
{
    // 讓 using TradeApi; 能編譯（真平台這個命名空間另有型別，回測用不到）
    public static class CtaBacktestInfo
    {
        public const string Version = "0.1";

        // 模擬時鐘：編譯策略時 DateTime.UtcNow／Now 會被換成這兩個（見 spec 4.1）
        public static DateTime UtcNow
        {
            get
            {
                Tester t = Tester.Current;
                if (t == null || t.Now == default(DateTime)) return DateTime.UtcNow;
                return DateTime.SpecifyKind(t.Now, DateTimeKind.Utc);
            }
        }

        public static DateTime Now { get { return UtcNow.ToLocalTime(); } }
    }
}

namespace TradeApi.Instruments
{
    public enum InstrumentType
    {
        Crypto = 0, Equity = 1, Forex = 2, Future = 3, Option = 4, CFD = 5, Forward = 6, Indice = 7, SpreadBet = 8,
        FixedIncome = 9, ETF = 10, TBill = 11, Spot = 12, Corporate = 13, Synthetic = 14, Fund = 15
    }

    public enum TradingStatus { Close = 0, Open = 1, Halt = 2, CAMaintenance = 4 }

    // 回測時跟著模擬報價走：Bid = 資料價格，Ask = Bid + 點差
    public sealed class DayInfo
    {
        internal DayInfo() { Ask = Bid = Last = Open = High = Low = PrevClose = double.NaN; }
        public double Ask { get; internal set; }
        public double Bid { get; internal set; }
        public double Last { get; internal set; }
        public DateTime LastTimeUtc { get; internal set; }
        public double Open { get; internal set; }
        public double High { get; internal set; }
        public double Low { get; internal set; }
        public double PrevClose { get; internal set; }
        public double Change { get { return Last - PrevClose; } }
        public double AskSize { get { return double.NaN; } }
        public double BidSize { get { return double.NaN; } }
        public double LastSize { get { return double.NaN; } }
        public double Volume { get { return double.NaN; } }
        public double MatchVolume { get { return double.NaN; } }
        public double OpenInterest { get { return double.NaN; } }
        public double SettlementPrice { get { return double.NaN; } }
        public double PastSettlementPrice { get { return double.NaN; } }
        public double IndAuctionPrice { get { return double.NaN; } }
        public long Ticks { get; internal set; }
        public string Instrument { get; internal set; }

        private DateTime day = DateTime.MinValue;

        internal void OnTick(DateTime time, double bid, double ask)
        {
            if (time.Date != day)
            {
                if (day != DateTime.MinValue) PrevClose = Last;
                day = time.Date;
                Open = High = Low = bid;
            }
            if (bid > High) High = bid;
            if (bid < Low) Low = bid;
            Bid = bid;
            Ask = ask;
            Last = bid;
            LastTimeUtc = time;
            Ticks++;
        }
    }

    public class Instrument
    {
        // 真平台有這兩個運算子，EvoCode 編好的 DLL 會直接呼叫（spec.md 13.1）；比較是否同一個物件
        public static bool operator ==(Instrument a, Instrument b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Instrument a, Instrument b) { return !ReferenceEquals(a, b); }
        public override bool Equals(object obj) { return ReferenceEquals(this, obj); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }

        private readonly List<KeyValuePair<string, string>> info;
        private readonly double minLot, maxLot;

        internal Instrument(string symbol, InstrumentType type, double tickSize, double lotStep, double minLot, double maxLot,
                            List<KeyValuePair<string, string>> info)
        {
            Symbol = symbol;
            Type = type;
            MinimalTickSize = tickSize;
            LotStep = lotStep;
            this.minLot = minLot;
            this.maxLot = maxLot;
            this.info = info ?? new List<KeyValuePair<string, string>>();
            DayInfo = new DayInfo { Instrument = symbol };
        }

        public string Symbol { get; private set; }
        public InstrumentType Type { get; private set; }
        public double MinimalTickSize { get; private set; }
        public double LotStep { get; private set; }
        public DayInfo DayInfo { get; private set; }
        public TradingStatus TradingStatus { get { return TradingStatus.Open; } }
        public double LimitHigh { get { return double.NaN; } }
        public double LimitLow { get { return double.NaN; } }

        public double MinimalLot(TradeApi.Trading.ProductType productType) { return minLot; }
        public double MaximalLot(TradeApi.Trading.ProductType productType) { return maxLot; }

        // 回傳匯出時平台給的商品資訊（合約大小以回測設定為準）
        public Dictionary<string, string> GetSymbolInfo()
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in info) d[kv.Key] = kv.Value;
            return d;
        }

        public override string ToString() { return Symbol; }
    }

    public sealed class InstrumentsManager
    {
        private readonly Runtime.Script.CSharpScript script;
        internal InstrumentsManager(Runtime.Script.CSharpScript script) { this.script = script; }

        public Instrument Current { get { return script.RequireTester().Instrument; } }

        // 本商品、或換算匯率用的「報價幣別USD」「USD報價幣別」（價格用回測設定的匯率）
        public List<Instrument> GetInstruments(string symbol)
        {
            return script.RequireTester().FindInstruments(symbol);
        }
    }
}

namespace TradeApi.Quotes
{
    public sealed class BBO { internal BBO() { } }
    public sealed class Trade { internal Trade() { } }
}
