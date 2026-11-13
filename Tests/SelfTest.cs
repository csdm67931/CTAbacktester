using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CtaBacktest;
using Runtime.Script;
using TradeApi.History;
using TradeApi.Indicators;
using TradeApi.Trading;

// 回測器自我檢查：撮合、不偷看未來、指標、統計，以及用真實策略檔跑完整流程
// 用法：SelfTest.exe <資料夾（預設 samples\data）> <策略檔資料夾（預設 samples\strategies）> <輸出資料夾> [--real <私人策略資料夾>]
namespace SelfTestNs
{
    // ---- 測試用策略 ----
    public class ProbeStrategy : StrategyBuilder
    {
        public static Action<ProbeStrategy, TickStatus> OnUpdate;
        public static Action<ProbeStrategy> OnInit;
        public ProbeStrategy() { Credentials.ProjectName = "Probe"; }
        public override void Init() { if (OnInit != null) OnInit(this); }
        public override void Update(TickStatus args) { if (OnUpdate != null) OnUpdate(this, args); }
    }

    // 收盤價上穿 / 下穿 SMA 就反手（給統計與整體流程用）
    public class MaCross : StrategyBuilder
    {
        [InputParameter(InputType.Numeric, "均線週期", 0)]
        [SimpleNumeric(2D, 500D, 0, 1)]
        public int Period = 20;

        [InputParameter(InputType.Numeric, "手數", 1)]
        [SimpleNumeric(0.01D, 100D, 2, 0.01)]
        public double Lots = 0.1;

        private BuiltInIndicator ma;
        public MaCross() { Credentials.ProjectName = "MaCross"; }
        public override void Init() { ma = IndicatorsManager.BuildIn.MA(HistoryDataSeries, Period, MAMode.SMA); }
        public override void Update(TickStatus args)
        {
            if (args != TickStatus.IsBar || HistoryDataSeries.Count < Period + 3) return;
            double c1 = HistoryDataSeries.GetValue(PriceType.Close, 1), c2 = HistoryDataSeries.GetValue(PriceType.Close, 2);
            double m1 = ma.GetValue(1), m2 = ma.GetValue(2);
            bool up = c2 <= m2 && c1 > m1, dn = c2 >= m2 && c1 < m1;
            if (!up && !dn) return;
            foreach (var p in PositionsManager.GetPositions()) PositionsManager.Close(p);
            OrdersManager.Send(new OrderRequest(OrderType.Market, InstrumentsManager.Current, AccountManager.Current, up ? OrderSide.Buy : OrderSide.Sell, Lots, null));
        }
    }

    // 25 子程式測試用（spec.md 13.8）：第 HangAfterBars 根 K 棒開始卡在無窮迴圈
    public class HangStrategy : StrategyBuilder
    {
        public int HangAfterBars = 5;
        private int bars;
        public HangStrategy() { Credentials.ProjectName = "Hang"; }
        public override void Update(TickStatus args)
        {
            if (args == TickStatus.IsBar && ++bars >= HangAfterBars) while (true) { }
        }
    }

    // 25 子程式測試用：每根 K 棒睡 20ms，讓「跑到一半按停止」可以穩定重現
    public class SlowStrategy : StrategyBuilder
    {
        public SlowStrategy() { Credentials.ProjectName = "Slow"; }
        public override void Update(TickStatus args) { if (args == TickStatus.IsBar) System.Threading.Thread.Sleep(20); }
    }

    // 25 子程式測試用：第一根 K 棒就無限遞迴，造成堆疊溢位（整個程式會被系統結束）
    public class OverflowStrategy : StrategyBuilder
    {
        public OverflowStrategy() { Credentials.ProjectName = "Overflow"; }
        public override void Update(TickStatus args) { if (args == TickStatus.IsBar) Recurse(0); }
        private int Recurse(int depth) { return Recurse(depth + 1) + 1; }
    }

    // 24d 用：只有一個 bool 欄位，給 OptRanges.Expand 測 bool 展開
    public class OptFlagHolder
    {
        public bool Flag;
    }

    public static class Program
    {
        static int pass, fail;
        static readonly StringBuilder report = new StringBuilder();

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            string line = (ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : "");
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        static bool Near(double a, double b, double eps = 1e-6) { return Math.Abs(a - b) <= eps; }

        // 合成資料：每根 H1 已知 OHLC
        static string MakeSymbol(string root, string name, List<Bar> h1, List<Bar> m1 = null)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            Func<List<Bar>, string> csv = bars =>
            {
                var sb = new StringBuilder("time_utc,open,high,low,close\r\n");
                foreach (var b in bars) sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm},{1},{2},{3},{4}\r\n", b.Time, b.Open, b.High, b.Low, b.Close);
                return sb.ToString();
            };
            File.WriteAllText(Path.Combine(dir, "H1.csv"), csv(h1));
            if (m1 != null) File.WriteAllText(Path.Combine(dir, "M1.csv"), csv(m1));
            else if (File.Exists(Path.Combine(dir, "M1.csv"))) File.Delete(Path.Combine(dir, "M1.csv"));
            File.WriteAllText(Path.Combine(dir, "symbol_info.txt"), "Symbol=" + name + "\r\nMinimalTickSize=0.01\r\nLotStep=0.01\r\nMinimalLot=0.01\r\nAsk=100.02\r\nBid=100\r\n[SymbolInfo]\r\n合約規格=100\r\n計價幣別=USD\r\n", Encoding.UTF8);
            return dir;
        }

        static Bar B(DateTime t, double o, double h, double l, double c) { return new Bar { Time = t, Open = o, High = h, Low = l, Close = c }; }

        static TesterSettings Settings(SymbolData d, TickMode mode)
        {
            var s = Defaults.For(d, "H1", null);
            s.Mode = mode;
            s.FromUtc = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);
            s.ToUtc = new DateTime(2026, 2, 1);
            return s;
        }

        static void UnitTests(string tmp)
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var bars = new List<Bar>();
            for (int i = 0; i < 10; i++) bars.Add(B(t0.AddHours(i), 100, 100.5, 99.5, 100));
            // 第 3 根（02:00）開 100，收黑：開→高 101→低 98→收 99
            bars[2] = B(t0.AddHours(2), 100, 101, 98, 99);
            var d = SymbolData.Open(MakeSymbol(tmp, "SYN", bars));

            // 1. 市價買在 Ask，停損在 99 → 本週期 OHLC：開 100 → 高 101 → 低 98 觸發停損，成交在 99（不是跳空）
            ProbeStrategy.OnInit = null;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (a == TickStatus.IsBar && st.HistoryDataSeries.GetTimeUtc(0) == t0.AddHours(2))
                {
                    var r = new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null);
                    r.StopLossPrice = 99; r.TakeProfitPrice = 102;
                    var res = st.OrdersManager.Send(r);
                    Check("1a 市價單成交", !res.HasError, res.Message);
                }
            };
            var s = Settings(d, TickMode.BarOhlc);
            var rr = Tester.Run(d, typeof(ProbeStrategy), null, s, null, null);
            Check("1b 有一筆交易", rr.Trades.Count == 1, "筆數 " + rr.Trades.Count + " " + rr.Error);
            if (rr.Trades.Count == 1)
            {
                var t = rr.Trades[0];
                Check("1c 進場價 = 開盤＋點差", Near(t.OpenPrice, 100.02), t.OpenPrice.ToString());
                Check("1d 停損成交在停損價", Near(t.ClosePrice, 99) && t.Reason == "停損", t.ClosePrice + " " + t.Reason);
                Check("1e 損益 = (99-100.02)x1x100", Near(t.Profit, -102), t.Profit.ToString());
                Check("1f 餘額一致", Near(rr.Broker.Balance, 10000 - 102), rr.Broker.Balance.ToString());
            }

            // 2. 跳空：第 4 根開在 95（低於停損 99）→ 停損用 95 成交
            bars[3] = B(t0.AddHours(3), 95, 96, 94, 95.5);
            bars[2] = B(t0.AddHours(2), 100, 100.5, 99.5, 100);
            d = SymbolData.Open(MakeSymbol(tmp, "SYN2", bars));
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("2a 跳空停損用開盤價成交", rr.Trades.Count == 1 && Near(rr.Trades[0].ClosePrice, 95), rr.Trades.Count > 0 ? rr.Trades[0].ClosePrice.ToString() : "沒交易");

            // 3. 索引 0 不偷看：每個報價時 index0 的高低不能超過已發生的報價
            bars[3] = B(t0.AddHours(3), 100, 103, 97, 101);
            d = SymbolData.Open(MakeSymbol(tmp, "SYN3", bars));
            bool leak = false; int quotes = 0; double seenHigh = double.MinValue;
            DateTime lastBar = DateTime.MinValue;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                var h = st.HistoryDataSeries;
                if (h.GetTimeUtc(0) != lastBar) { lastBar = h.GetTimeUtc(0); seenHigh = double.MinValue; }
                double bid = st.InstrumentsManager.Current.DayInfo.Bid;
                seenHigh = Math.Max(seenHigh, bid);
                if (h.GetValue(PriceType.High, 0) > seenHigh + 1e-9) leak = true;
                if (Near(h.GetValue(PriceType.Close, 0), bid) == false) leak = true;
                quotes++;
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("3a 進行中 K 棒不偷看未來", !leak && quotes > 0, "報價次數 " + quotes);
            Check("3b 每根 4 個報價", rr.Ticks == rr.Bars * 4, rr.Ticks + " / " + rr.Bars);

            // 4. 只用開盤價：策略每根只被呼叫一次，但停損仍依高低價觸發
            bars[3] = B(t0.AddHours(3), 100, 100.5, 98, 99.8);
            d = SymbolData.Open(MakeSymbol(tmp, "SYN4", bars));
            int calls = 0;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                calls++;
                if (st.HistoryDataSeries.GetTimeUtc(0) == t0.AddHours(2))
                {
                    var r = new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null);
                    r.StopLossPrice = 98.5;
                    st.OrdersManager.Send(r);
                }
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.OpenOnly), null, null);
            Check("4a 開盤價模式每根呼叫一次", calls == rr.Bars, calls + " / " + rr.Bars);
            Check("4b 開盤價模式停損仍觸發", rr.Trades.Count == 1 && rr.Trades[0].Reason == "停損" && Near(rr.Trades[0].ClosePrice, 98.5), rr.Trades.Count > 0 ? rr.Trades[0].Reason + " " + rr.Trades[0].ClosePrice : "沒交易");

            // 5. 保證金不足拒單、手數不合法拒單、停損在錯誤一邊拒單
            var results = new List<TradeResult>();
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                var inst = st.InstrumentsManager.Current; var acc = st.AccountManager.Current;
                results.Add(st.OrdersManager.Send(new OrderRequest(OrderType.Market, inst, acc, OrderSide.Buy, 5, null)));      // 保證金 500 → OK
                results.Add(st.OrdersManager.Send(new OrderRequest(OrderType.Market, inst, acc, OrderSide.Buy, 1000, null)));   // 保證金 100000 → 超過
                results.Add(st.OrdersManager.Send(new OrderRequest(OrderType.Market, inst, acc, OrderSide.Buy, 0.015, null)));
                var bad = new OrderRequest(OrderType.Market, inst, acc, OrderSide.Sell, 1, null); bad.StopLossPrice = 90;
                results.Add(st.OrdersManager.Send(bad));
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("5a 保證金足夠成交", results.Count == 4 && !results[0].HasError);
            Check("5b 保證金不足拒單", results.Count == 4 && results[1].HasError, results.Count == 4 ? results[1].Message : "");
            Check("5c 手數不是級距倍數拒單", results.Count == 4 && results[2].HasError, results.Count == 4 ? results[2].Message : "");
            Check("5d 空單停損在下方拒單", results.Count == 4 && results[3].HasError, results.Count == 4 ? results[3].Message : "");
            Check("5e 測試結束平倉", rr.Trades.Count == 1 && rr.Trades[0].Reason == "測試結束");

            // 6. 限價單：掛 99.6 買，第 4 根低點 98 → 用 99.6 成交（非跳空）
            Order placed = null;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                var r = new OrderRequest(OrderType.Limit, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, 99.6);
                var res = st.OrdersManager.Send(r);
                placed = st.OrdersManager.GetOrders(false).FirstOrDefault();
                Check("6a 限價單掛單", !res.HasError && placed != null && placed.IsActive);
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("6b 限價單用限價成交", rr.Trades.Count == 1 && Near(rr.Trades[0].OpenPrice, 99.6), rr.Trades.Count > 0 ? rr.Trades[0].OpenPrice.ToString() : "沒交易");

            // 7. M1 模式：用 M1 產生報價；H1 index0 在 M1 報價間逐步長出來
            var m1 = new List<Bar>();
            for (int h = 0; h < 10; h++) for (int m = 0; m < 60; m++) m1.Add(B(t0.AddHours(h).AddMinutes(m), 100 + m * 0.01, 100 + m * 0.01 + 0.005, 100 + m * 0.01 - 0.005, 100 + m * 0.01));
            var hb = new List<Bar>();
            for (int h = 0; h < 10; h++) hb.Add(B(t0.AddHours(h), 100, 100.595, 99.995, 100.59));
            d = SymbolData.Open(MakeSymbol(tmp, "SYN7", hb, m1));
            int maxHighSteps = 0; double lastH = 0;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                double h0 = st.HistoryDataSeries.GetValue(PriceType.High, 0);
                if (a == TickStatus.IsQuote && h0 > lastH + 1e-9) maxHighSteps++;
                lastH = a == TickStatus.IsBar ? 0 : h0;
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.M1Ohlc), null, null);
            Check("7a M1 模式每根用 M1", rr.BarsWithM1 == rr.Bars, rr.BarsWithM1 + " / " + rr.Bars);
            Check("7b M1 報價數 = 60x4", rr.Ticks == rr.Bars * 240, rr.Ticks + " / " + rr.Bars);
            Check("7c 進行中高點逐步上升", maxHighSteps > 50, maxHighSteps.ToString());

            // 8. 多週期：要 H1 以外沒有的週期回傳空；要 H1 不含未來
            bool futureLeak = false; int mtfCount = -1;
            ProbeStrategy.OnInit = null;
            HistoricalData hd = null;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (hd == null)
                {
                    hd = st.HistoricalDataManager.Get(new TimeHistoricalRequest(st.InstrumentsManager.Current, DataType.Bid, Period.Hour, 1), new Interval(t0.AddYears(-1), t0.AddYears(1)));
                    var none = st.HistoricalDataManager.Get(new TimeHistoricalRequest(st.InstrumentsManager.Current, DataType.Bid, Period.Day, 1), new Interval(t0.AddYears(-1), t0.AddYears(1)));
                    mtfCount = none.Count;
                }
                if (hd.GetTimeUtc(0) > st.HistoryDataSeries.GetTimeUtc(0)) futureLeak = true;
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("8a 沒有的週期回傳空資料", mtfCount == 0, mtfCount.ToString());
            Check("8b 上層資料不含未來", !futureLeak && hd != null && hd.Count > 0);

            // 9. 指標：SMA(3) 與 EMA(3) 手算比對
            var cl = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var ib = new List<Bar>();
            for (int i = 0; i < 10; i++) ib.Add(B(t0.AddHours(i), cl[i], cl[i], cl[i], cl[i]));
            d = SymbolData.Open(MakeSymbol(tmp, "SYN9", ib));
            double sma = double.NaN, ema = double.NaN, rsi = double.NaN; BuiltInIndicator smaI = null, emaI = null, rsiI = null;
            ProbeStrategy.OnInit = st => { smaI = st.IndicatorsManager.BuildIn.MA(st.HistoryDataSeries, 3, MAMode.SMA); emaI = st.IndicatorsManager.BuildIn.MA(st.HistoryDataSeries, 3, MAMode.EMA); rsiI = st.IndicatorsManager.BuildIn.RSI(st.HistoryDataSeries, 3); };
            ProbeStrategy.OnUpdate = (st, a) => { sma = smaI.GetValue(1); ema = emaI.Lines[0][1]; rsi = rsiI.GetValue(1); };
            var s9 = Settings(d, TickMode.OpenOnly); s9.FromUtc = t0.AddHours(5);
            rr = Tester.Run(d, typeof(ProbeStrategy), null, s9, null, null);
            // 最後一次 Update 在 09:00，index1 = 08:00 收 9 → SMA = 8；EMA 從 1 起遞迴 a=0.5
            double e = 1; for (int i = 1; i <= 8; i++) e = e + 0.5 * (cl[i] - e);
            Check("9a SMA(3)", Near(sma, 8), sma.ToString());
            Check("9b EMA(3)", Near(ema, e), ema + " vs " + e);
            Check("9c RSI 只漲 = 100", Near(rsi, 100), rsi.ToString());
            ProbeStrategy.OnInit = null; ProbeStrategy.OnUpdate = null;

            // 11. M1 不完整（每小時只有前 3 分鐘）：高低沒涵蓋 H1 → 改用本週期 OHLC
            var m1s = new List<Bar>();
            for (int h = 0; h < 10; h++) for (int m = 0; m < 3; m++) m1s.Add(B(t0.AddHours(h).AddMinutes(m), 100, 100.01, 99.99, 100));
            var h11 = new List<Bar>();
            for (int h = 0; h < 10; h++) h11.Add(B(t0.AddHours(h), 100, 101, 99, 100));
            d = SymbolData.Open(MakeSymbol(tmp, "SYN11", h11, m1s));
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.M1Ohlc), null, null);
            Check("11a M1 不完整時改用本週期", rr.BarsWithM1 == 0 && rr.BarsM1Incomplete == rr.Bars, rr.BarsWithM1 + " / " + rr.BarsM1Incomplete + " / " + rr.Bars);
            Check("11b 報價數 = 每根 4 個", rr.Ticks == rr.Bars * 4, rr.Ticks + " / " + rr.Bars);

            // 12. 停損回呼裡平掉另一筆：不能產生手數 0 的假交易
            var b12 = new List<Bar>(bars);
            b12[3] = B(t0.AddHours(3), 100, 100.5, 98, 99.8);
            d = SymbolData.Open(MakeSymbol(tmp, "SYN12", b12));
            ProbeStrategy.OnInit = st => { st.PositionsManager.OnClose += p => { foreach (var o in st.PositionsManager.GetPositions()) st.PositionsManager.Close(o); }; };
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                for (int k = 0; k < 2; k++)
                {
                    var r = new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null);
                    r.StopLossPrice = 99;
                    st.OrdersManager.Send(r);
                }
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("12a 兩筆部位各平一次", rr.Trades.Count == 2 && rr.Trades.All(x => x.Volume > 0), "筆數 " + rr.Trades.Count);

            // 13. GTD 限價單到期取消：02:30 到期，03:00 才到價 → 不成交
            ProbeStrategy.OnInit = null;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                var r = new OrderRequest(OrderType.Limit, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, 99.0);
                r.TIF = TimeInForce.GTD; r.ExpirationTimeUtc = t0.AddHours(2).AddMinutes(30);
                st.OrdersManager.Send(r);
            };
            rr = Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            Check("13a GTD 到期不成交", rr.Trades.Count == 0, "筆數 " + rr.Trades.Count);
            ProbeStrategy.OnInit = null; ProbeStrategy.OnUpdate = null;
        }

        // 簡單 CSV 拆欄（支援雙引號）
        static List<string> SplitCsv(string line)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
                else if (c == '"') q = true;
                else if (c == ',') { r.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            r.Add(sb.ToString());
            return r;
        }

        // 14. 群益官方規格：用平台匯出的真實 symbol_info（2026-10-09 商品總表）跑 Defaults，
        //     和群益槓桿交易商官網（全商品槓桿、全商品費用，2026-11-09 讀取）逐項比對
        static void SpecTests(string tmp)
        {
            string fx = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Tests", "fixtures", "cta_symbols_20261009.csv"));
            if (!File.Exists(fx)) { Check("14 規格測試資料", false, "找不到 " + fx); return; }
            // 官網：槓桿倍數；跳動點與合約來自平台；手續費率 %
            var expect = new Dictionary<string, double[]>
            {
                // 槓桿, 跳動點, 合約, 手續費率%, 最小手數
                { "EURUSD",  new[] { 30.0, 0.00001, 100000, 0, 0.01 } },
                { "GBPUSD",  new[] { 30.0, 0.00001, 100000, 0, 0.01 } },
                { "USDJPY",  new[] { 30.0, 0.001, 100000, 0, 0.01 } },
                { "AUDUSD",  new[] { 20.0, 0.00001, 100000, 0, 0.01 } },
                { "XAUUSD",  new[] { 20.0, 0.01, 100, 0, 0.01 } },
                { "XAGUSD",  new[] { 10.0, 0.001, 5000, 0, 0.01 } },
                { "XTIUSD",  new[] { 10.0, 0.001, 1000, 0, 0.01 } },
                { "XBRUSD",  new[] { 10.0, 0.001, 1000, 0, 0.01 } },
                { "DJ30",    new[] { 20.0, 0.01, 1, 0.01, 0.1 } },
                { "NDAQ100", new[] { 20.0, 0.01, 1, 0.01, 0.1 } },
                { "SPX500",  new[] { 20.0, 0.01, 1, 0.01, 1 } },
                { "DAX30",   new[] { 20.0, 0.01, 1, 0.01, 0.1 } },
                { "HK50",    new[] { 10.0, 0.01, 1, 0.01, 1 } },
                { "JPN225",  new[] { 20.0, 0.01, 10, 0.01, 1 } },
                { "AAPL.US", new[] { 10.0, 0.001, 1, 0.05, 1 } },
                { "TSLA.US", new[] { 5.0, 0.001, 1, 0.05, 1 } },
                { "NVDA.US", new[] { 6.67, 0.001, 1, 0.05, 1 } },
                { "00700.HK", new[] { 10.0, 0.001, 1, 0.5, 1 } },
            };
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lines = File.ReadAllLines(fx, Encoding.UTF8);
            var hdr = SplitCsv(lines[0]);
            int iSym = hdr.IndexOf("symbol"), iTick = hdr.IndexOf("tick_size"), iStep = hdr.IndexOf("lot_step"), iInfo = hdr.IndexOf("symbol_info");
            int seen = 0;
            var dirs = new Dictionary<string, SymbolData>();
            for (int li = 1; li < lines.Length; li++)
            {
                var f = SplitCsv(lines[li]);
                string sym = f[iSym];
                double[] e;
                if (!expect.TryGetValue(sym, out e)) continue;
                seen++;
                string dir = Path.Combine(tmp, "SPEC", sym);
                Directory.CreateDirectory(dir);
                var bars = new StringBuilder("time_utc,open,high,low,close\r\n");
                for (int i = 0; i < 10; i++) bars.AppendFormat("{0:yyyy-MM-dd HH:mm},100,100.5,99.5,100\r\n", t0.AddHours(i));
                File.WriteAllText(Path.Combine(dir, "H1.csv"), bars.ToString());
                var info = new StringBuilder("Symbol=" + sym + "\r\nMinimalTickSize=" + f[iTick] + "\r\nLotStep=" + f[iStep] + "\r\n[SymbolInfo]\r\n");
                foreach (string kv in f[iInfo].Split('|')) info.Append(kv).Append("\r\n");
                File.WriteAllText(Path.Combine(dir, "symbol_info.txt"), info.ToString(), Encoding.UTF8);
                var d = SymbolData.Open(dir);
                dirs[sym] = d;
                var notes = new List<string>();
                var s = Defaults.For(d, "H1", notes);
                bool mismatch = notes.Any(n => n.Contains("群益官方表是"));
                Check("14 " + sym + " 槓桿／跳動點／合約／費率／最小手數",
                      Near(s.Leverage, e[0], 0.01) && Near(s.TickSize, e[1], 1e-12) && Near(s.ContractSize, e[2]) && Near(s.CommissionPercent, e[3]) && Near(s.MinLot, e[4]) && !mismatch,
                      string.Format(System.Globalization.CultureInfo.InvariantCulture, "1:{0} / {1} / {2} / {3}% / {4}{5}", s.Leverage, s.TickSize, s.ContractSize, s.CommissionPercent, s.MinLot, mismatch ? " 與官方表不一致" : ""));
            }
            Check("14 規格測試資料齊全", seen == expect.Count, seen + " / " + expect.Count);

            // 15. 保證金與手續費實際扣法
            Func<SymbolData, double, TradeRecord> runOne = (d, lots) =>
            {
                ProbeStrategy.OnInit = null;
                ProbeStrategy.OnUpdate = (st, a) =>
                {
                    if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                    st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, lots, null));
                };
                var set = Settings(d, TickMode.BarOhlc);
                set.Spread = 0;
                var r = Tester.Run(d, typeof(ProbeStrategy), null, set, null, null);
                lastMargin = r.Broker == null ? double.NaN : r.Broker.MaxMarginUsed;
                ProbeStrategy.OnUpdate = null;
                return r.Trades.Count == 1 ? r.Trades[0] : null;
            };
            if (dirs.ContainsKey("XAUUSD"))
            {
                var t = runOne(dirs["XAUUSD"], 1);
                Check("15a 黃金 1 手保證金 = 100x100/20", t != null && Near(lastMargin, 500), lastMargin.ToString());
                Check("15b 黃金手續費 0", t != null && Near(t.Commission, 0), t == null ? "沒交易" : t.Commission.ToString());

                // 鎖倉：同一商品多空並存，保證金取多空合計較大的一邊（群益公告）
                ProbeStrategy.OnInit = null;
                ProbeStrategy.OnUpdate = (st, a) =>
                {
                    if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2)) return;
                    st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null));
                    st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Sell, 1, null));
                    st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Sell, 0.5, null));
                };
                var hs = Settings(dirs["XAUUSD"], TickMode.BarOhlc);
                hs.Spread = 0;
                var hr = Tester.Run(dirs["XAUUSD"], typeof(ProbeStrategy), null, hs, null, null);
                ProbeStrategy.OnUpdate = null;
                Check("15g 鎖倉保證金取大值：多 1 手、空 1.5 手 = 750（不是 1250）", hr.Trades.Count == 3 && Near(hr.Broker.MaxMarginUsed, 750), hr.Trades.Count + " 筆 / " + hr.Broker.MaxMarginUsed);
                // 資金 700：多 1 手用掉 500。再加多 1 手要再 500（拒絕）；反向空 1 手不增加保證金（接受）
                int sent = 0;
                bool sameSideRejected = false, hedgeAccepted = false;
                ProbeStrategy.OnUpdate = (st, a) =>
                {
                    if (a != TickStatus.IsBar || st.HistoryDataSeries.GetTimeUtc(0) != t0.AddHours(2) || sent++ > 0) return;
                    st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null));
                    sameSideRejected = st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Buy, 1, null)).HasError;
                    hedgeAccepted = !st.OrdersManager.Send(new OrderRequest(OrderType.Market, st.InstrumentsManager.Current, st.AccountManager.Current, OrderSide.Sell, 1, null)).HasError;
                };
                var ms = Settings(dirs["XAUUSD"], TickMode.BarOhlc);
                ms.Spread = 0;
                ms.InitialDeposit = 700;
                var mr = Tester.Run(dirs["XAUUSD"], typeof(ProbeStrategy), null, ms, null, null);
                ProbeStrategy.OnUpdate = null;
                Check("15i 保證金不足時同向加碼被拒、反向鎖倉可成交", sameSideRejected && hedgeAccepted && Near(mr.Broker.MaxMarginUsed, 500), sameSideRejected + " / " + hedgeAccepted + " / " + mr.Broker.MaxMarginUsed);
                Check("15h 強制平倉水位預設 = 群益規定 50%", Near(hs.StopOutPercent, 50) && Near(OfficialSpecs.StopOutPercent, 50), hs.StopOutPercent.ToString());
            }
            if (dirs.ContainsKey("AAPL.US"))
            {
                var t = runOne(dirs["AAPL.US"], 10);
                Check("15c 美股 10 股保證金 = 10x100x10%", t != null && Near(lastMargin, 100), lastMargin.ToString());
                Check("15d 美股手續費 = 買賣各 1000x0.05%", t != null && Near(t.Commission, 1.0), t == null ? "沒交易" : t.Commission.ToString());
            }
            if (dirs.ContainsKey("00700.HK"))
            {
                var t = runOne(dirs["00700.HK"], 1);
                Check("15e 港股手續費不足 5 美元收 5（買賣各一次）", t != null && Near(t.Commission, 10), t == null ? "沒交易" : t.Commission.ToString());
            }
            if (dirs.ContainsKey("DJ30"))
            {
                var t = runOne(dirs["DJ30"], 1);
                Check("15f 道瓊 1 手保證金 = 100/20、手續費買賣各 0.01%", t != null && Near(lastMargin, 5) && Near(t.Commission, 0.02), lastMargin + " / " + (t == null ? "沒交易" : t.Commission.ToString()));
            }
        }
        static double lastMargin;

        // 17. 自動匯率（spec 6.2）與報告位數（spec 6.3）
        static void FxTests(string tmp)
        {
            string root = Path.Combine(tmp, "FX");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Func<string, string, double, SymbolData> mk = (sym, info, close) =>
            {
                string dir = Path.Combine(root, sym);
                Directory.CreateDirectory(dir);
                var csv = new StringBuilder("time_utc,open,high,low,close\r\n");
                for (int i = 0; i < 5; i++)
                {
                    double c = close + (i - 4);   // 最後一根是 close，前面每天少 1
                    csv.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "2026-11-0{0} 00:00,{1},{1},{1},{1}\r\n", i + 1, c);
                }
                File.WriteAllText(Path.Combine(dir, "D1.csv"), csv.ToString());
                File.WriteAllText(Path.Combine(dir, "H1.csv"), csv.ToString());
                File.WriteAllText(Path.Combine(dir, "symbol_info.txt"), "Symbol=" + sym + "\r\n[SymbolInfo]\r\n" + info, Encoding.UTF8);
                return SymbolData.Open(dir);
            };
            var usdjpy = mk("USDJPY", "計價幣別=JPY\r\n合約規格=100000 USD\r\n", 153.2104);
            var eurusd = mk("EURUSD", "計價幣別=USD\r\n", 10.08341);
            mk("USDHKD", "計價幣別=HKD\r\n", 7.78);
            var gbpjpy = mk("GBPJPY", "計價幣別=JPY\r\n合約規格=100000 GBP\r\n", 211.898);
            var dax = mk("DAX30", "計價幣別=EUR\r\n", 20000);
            var hk = mk("00700.HK", "所需保證金=10 %\r\n", 500);

            var n1 = new List<string>();
            var s1 = Defaults.For(gbpjpy, "H1", n1);
            Check("17a GBPJPY 自動用 USDJPY：匯率 = 1/153.21", s1.FxPair == "USDJPY" && Near(s1.FxPairPrice, 153.21) && Near(s1.FxRate, 1 / 153.21, 1e-12) && !n1.Any(n => n.Contains("找不到")),
                  s1.FxPair + " " + s1.FxPairPrice + " " + s1.FxRate + " " + s1.FxSource);
            var s2 = Defaults.For(dax, "H1", null);
            Check("17b DAX30 用 EURUSD：匯率 = 報價（5 位）", s2.FxPair == "EURUSD" && Near(s2.FxPairPrice, 10.08341) && Near(s2.FxRate, 10.08341), s2.FxPair + " " + s2.FxRate);
            var s3 = Defaults.For(usdjpy, "H1", null);
            Check("17c USDJPY 用自己的資料", s3.FxPair == "USDJPY" && Near(s3.FxRate, 1 / 153.21, 1e-12), s3.FxPair + " " + s3.FxRate);
            var s4 = Defaults.For(hk, "H1", null);
            Check("17d 港股沒有計價幣別：用官方表 HKD，取 USDHKD", s4.QuoteCurrency == "HKD" && s4.FxPair == "USDHKD" && Near(s4.FxRate, 1 / 7.78, 1e-12), s4.QuoteCurrency + " " + s4.FxPair + " " + s4.FxRate);
            var s5 = Defaults.For(eurusd, "H1", null);
            Check("17e 美元報價不用換算", s5.FxPair == null && Near(s5.FxRate, 1), s5.FxPair ?? "null");
            Directory.Delete(Path.Combine(root, "USDJPY"), true);
            var n6 = new List<string>();
            var gbp2 = SymbolData.Open(Path.Combine(root, "GBPJPY"));
            var s6 = Defaults.For(gbp2, "H1", n6);
            Check("17f 找不到貨幣對：匯率 1、指出要匯出 USDJPY", s6.FxPair == "USDJPY" && double.IsNaN(s6.FxPairPrice) && Near(s6.FxRate, 1) && n6.Any(n => n.Contains("用 USDJPY") && n.Contains("還沒有")), string.Join(" ", n6));
            DateTime at;
            double c3 = FxRates.LastClose(gbpjpy, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), out at);
            Check("17g 取指定日期之前最後一根", Near(c3, 211.898 - 3) && at.Day == 2, c3 + " " + at);

            // 交叉外匯保證金用基礎貨幣對美元（使用者 2026-11-10「保證金用 EURUSD 計算」）
            var euraud = mk("EURAUD", "計價幣別=AUD\r\n合約規格=100000 EUR\r\n", 1.75);
            BarSeries ms; bool bf;
            string mp = FxRates.MarginPair(euraud, "AUD", "USD", out ms, out bf);
            Check("17m EURAUD 保證金用 EURUSD 的 K 棒（基礎幣別在前）", mp == "EURUSD" && ms != null && ms.Count == 5 && bf, mp + " " + (ms == null ? "null" : ms.Timeframe));
            string mp2 = FxRates.MarginPair(gbp2, "JPY", "USD", out ms, out bf);
            Check("17n GBPJPY 沒有 GBPUSD 資料：指出 GBPUSD、不給 K 棒", mp2 == "GBPUSD" && ms == null, mp2 + " " + (ms == null ? "null" : "有"));
            Check("17o 非交叉外匯不用（EURUSD、USDJPY、DAX30）",
                  FxRates.MarginPair(eurusd, "USD", "USD", out ms, out bf) == null && FxRates.MarginPair(usdjpy, "JPY", "USD", out ms, out bf) == null
                  && FxRates.MarginPair(dax, "EUR", "USD", out ms, out bf) == null, "");

            // 報告位數：合約 100,000、點差寫跳動點、匯率寫貨幣對報價
            var r = new BacktestResult { Settings = s1, Symbol = "GBPJPY", StrategyName = "T" };
            s1.Spread = 45 * s1.TickSize;
            string html = HtmlReport.Build(r, false);
            Check("17h 報告：合約 100,000 不補零、點差 45 跳動點、匯率 USDJPY 153.21",
                  html.Contains(">100,000<") && !html.Contains("100,000.0000") && html.Contains("45 跳動點") && html.Contains("USDJPY 153.21"), "");
            Check("17j 0 筆交易：報告有重點數字卡與原因說明", html.Contains("class=\"kpi\"") && html.Contains("這次回測沒有任何交易"), "");
            r.Parameters.Add(new KeyValuePair<string, string>("EnableOrders", "False"));
            r.Parameters.Add(new KeyValuePair<string, string>("RiskPercent", "1"));
            string html2 = HtmlReport.Build(r, false);
            Check("17k 0 筆交易且下單開關關閉：報告點名 EnableOrders", html2.Contains("「EnableOrders」沒打勾") && !html2.Contains("RiskPercent」"), "");
            // 有中文說明時改用中文名稱（和參數頁、狀態列一致）
            r.ParamLabels["EnableOrders"] = "允許送單（預設關閉，只記錄訊號）";
            string html3 = HtmlReport.Build(r, false);
            Check("17l 報告用中文參數名稱「允許送單」", html3.Contains("「允許送單」沒打勾"), "");

            // 使用者回測器開著、去交易王補匯出 USDJPY：同一個商品物件再算一次要抓得到（按開始時會這樣重抓）
            mk("USDJPY", "計價幣別=JPY\r\n", 150.5);
            var s7 = Defaults.For(gbp2, "H1", null);
            Check("17i 補匯出貨幣對後不用重開就抓得到", s7.FxPair == "USDJPY" && Near(s7.FxPairPrice, 150.5), s7.FxPairPrice.ToString());

            // 歷史匯率（spec.md 6.2）：USDJPY 每天開盤 146.5～150.5，取當下最近一根的開盤價，不偷看未來、超過 4 天不用
            var pr = FxRates.ProfitRates(gbp2, "USDJPY", "JPY");
            Func<int, int, int, double> rateAt = (mo, d, h) => pr.At(new DateTime(2026, mo, d, h, 0, 0, DateTimeKind.Utc));
            Check("17p 歷史匯率取當下那根開盤價（11/03 05:00 → 1/148.5）", pr != null && Near(rateAt(11, 3, 5), 1 / 148.5, 1e-12), pr == null ? "null" : rateAt(11, 3, 5).ToString());
            Check("17q 資料開始前、或最後一根超過 4 天，回傳 NaN", pr != null && double.IsNaN(rateAt(10, 30, 0)) && double.IsNaN(rateAt(11, 20, 0)) && Near(rateAt(11, 8, 0), 1 / 150.5, 1e-12), "");
            Check("17r 商品本身就是換算貨幣對（USDJPY）用自己的資料", FxRates.ProfitRates(SymbolData.Open(Path.Combine(root, "USDJPY")), "USDJPY", "JPY") != null
                  && FxRates.ProfitRates(gbp2, "USDXXX", "JPY") == null, "");

            // 18. 圖表刻度：1、2、5 × 10 的次方，範圍包住資料
            double lo = 9308, hi = 10023; int dec;
            var tk = ChartAxis.NiceTicks(ref lo, ref hi, 5, out dec);
            Check("18a 刻度 9,200～10,200 每 200", Near(lo, 9200) && Near(hi, 10200) && tk.Count == 6 && dec == 0, string.Join(",", tk));
            lo = 9999.6; hi = 10000.4;
            tk = ChartAxis.NiceTicks(ref lo, ref hi, 5, out dec);
            Check("18b 範圍很小時刻度不重複、帶小數", tk.Distinct().Count() == tk.Count && dec >= 1 && lo <= 9999.6 && hi >= 10000.4, string.Join(",", tk) + " dec=" + dec);
            lo = hi = 10000;
            tk = ChartAxis.NiceTicks(ref lo, ref hi, 5, out dec);
            Check("18c 平線（0 筆交易）也有刻度", tk.Count >= 2 && lo < 10000 && hi > 10000, string.Join(",", tk));
            // 下單開關的認定：做多做空的選擇（TradeLong/TradeShort）不算
            Check("18d 下單開關名稱判斷", HtmlReport.IsOrderSwitchName("EnableOrders") && HtmlReport.IsOrderSwitchName("EnableLiveOrders")
                && !HtmlReport.IsOrderSwitchName("TradeShort") && !HtmlReport.IsOrderSwitchName("TradeLong"));

            // 21. 資料缺口：週末（約 2 天）不算，超過 5 天才算；只看指定期間
            var gb = new List<Bar>();
            DateTime g0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Action<DateTime, DateTime> fill = (a, z) => { for (DateTime t = a; t < z; t = t.AddHours(1)) if (t.DayOfWeek != DayOfWeek.Saturday && t.DayOfWeek != DayOfWeek.Sunday) gb.Add(new Bar { Time = t, Open = 1, High = 1.1, Low = 0.9, Close = 1 }); };
            fill(g0, g0.AddDays(30));
            fill(g0.AddDays(60), g0.AddDays(90));   // 中間缺 30 天
            var gs = new BarSeries("H1", gb);
            var gaps = DataQuality.FindGaps(gs, g0, g0.AddDays(90));
            Check("21a 缺口：週末不算、30 天缺口抓到", gaps.Count == 1 && gaps[0].Days > 29 && gaps[0].Days < 33, gaps.Count + " 段 " + (gaps.Count > 0 ? gaps[0].Days.ToString("F1") : ""));
            Check("21b 缺口：期間不含缺口就不報", DataQuality.FindGaps(gs, g0.AddDays(61), g0.AddDays(90)).Count == 0 && DataQuality.FindGaps(gs, g0, g0.AddDays(20)).Count == 0, "");
        }

        // 16. 官方槓桿表：解析官網頁面、拒絕不完整頁面、更新差異、找不到商品時提醒
        static void LeverageTests(string tmp)
        {
            Check("16 預設官方槓桿表已載入（data\\official_leverage.csv）", LeverageTable.Count >= 600, LeverageTable.Count + " 項，" + LeverageTable.FilePath);
            string fx = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Tests", "fixtures", "cfmt_lever_20261109.html"));
            if (!File.Exists(fx)) { Check("16 官網頁面測試資料", false, "找不到 " + fx); return; }
            string html = File.ReadAllText(fx, Encoding.UTF8);

            var list = LeverageTable.Parse(html);
            var by = list.ToDictionary(e => e.Symbol, StringComparer.OrdinalIgnoreCase);
            Func<string, double> lev = s => by.ContainsKey(s) ? by[s].Leverage : double.NaN;
            Check("16a 解析 702 項、通過完整性檢查", list.Count == 702 && LeverageTable.Validate(list) == null, list.Count.ToString());
            Check("16b 外匯去斜線、30 倍不因 3.33% 變 30.03", by.ContainsKey("AUDCAD") && Near(lev("EURUSD"), 30) && Near(lev("USDJPY"), 30) && Near(lev("AUDUSD"), 20),
                  lev("EURUSD") + " / " + lev("USDJPY") + " / " + lev("AUDUSD"));
            Check("16c 商品、指數槓桿", Near(lev("XAUUSD"), 20) && Near(lev("XAGUSD"), 10) && Near(lev("XTIUSD"), 10) && Near(lev("XBRUSD"), 10) && Near(lev("DJ30"), 20) && Near(lev("HK50"), 10));
            Check("16d 個股槓桿與保證金", Near(lev("AAPL.US"), 10) && Near(lev("TSLA.US"), 5) && Near(lev("NVDA.US"), 6.67) && Near(by["NVDA.US"].MarginPercent, 15) && Near(lev("00700.HK"), 10));
            Check("16e 類別", by["EURUSD"].Group == "外匯" && by["XAUUSD"].Group == "商品" && by["DJ30"].Group == "指數" && by["AAPL.US"].Group == "美股" && by["00700.HK"].Group == "港股");

            string file = Path.Combine(tmp, "LEV", "official_leverage.csv");
            if (Directory.Exists(Path.GetDirectoryName(file))) Directory.Delete(Path.GetDirectoryName(file), true);
            try
            {
                LeverageTable.FilePath = file;
                var r1 = LeverageTable.Update(html);
                Check("16f 第一次更新：全部算新增", r1.Ok && r1.Total == 702 && r1.Added.Count == 702 && LeverageTable.Count == 702, r1.Error ?? r1.Total.ToString());
                string before = File.ReadAllText(file);

                var bad = LeverageTable.Update(html.Substring(0, 20000));
                Check("16g 頁面不完整時拒絕且不覆蓋原檔", !bad.Ok && File.ReadAllText(file) == before && LeverageTable.Count == 702, bad.Error);

                // 模擬官網變動：AAPL 改 5 倍、刪掉 TSLA、新增一檔
                string changed = System.Text.RegularExpressions.Regex.Replace(html, "<td>AAPL\\.US</td>\\s*<td>蘋果</td>\\s*<td>10倍</td>\\s*<td>10%</td>",
                                                "<td>AAPL.US</td><td>蘋果</td><td>5倍</td><td>20%</td>");
                changed = System.Text.RegularExpressions.Regex.Replace(changed, "<tr>\\s*<td>TSLA\\.US</td>.*?</tr>", "<tr><td>ZZNEW.US</td><td>測試新股</td><td>4倍</td><td>25%</td><td>盤中</td></tr>", System.Text.RegularExpressions.RegexOptions.Singleline);
                var r2 = LeverageTable.Update(changed);
                Check("16h 第二次更新：調整、新增、下架各 1", r2.Ok && r2.Changed.Count == 1 && r2.Added.Count == 1 && r2.Removed.Count == 1
                      && r2.Changed[0].StartsWith("AAPL.US") && r2.Added[0].StartsWith("ZZNEW.US") && r2.Removed[0].StartsWith("TSLA.US"),
                      r2.Error ?? string.Join(" | ", r2.Changed.Concat(r2.Added).Concat(r2.Removed)));
                var a = OfficialSpecs.Find("AAPL.US"); var z = OfficialSpecs.Find("ZZNEW.US");
                Check("16i 更新後立即生效（AAPL 5 倍、新股 4 倍、手續費規則不變）",
                      a != null && Near(a.Leverage, 5) && Near(a.FeePercent, 0.05) && z != null && Near(z.Leverage, 4) && OfficialSpecs.Find("TSLA.US").MarginPercent.Equals(double.NaN),
                      a == null ? "無" : a.Leverage + " / " + (z == null ? "無" : z.Leverage.ToString()));

                // 槓桿欄格式變了（例「1:30」）：整次拒絕，不改用四捨五入的保證金欄、也不把它當下架
                string before2 = File.ReadAllText(file);
                var r3 = LeverageTable.Update(System.Text.RegularExpressions.Regex.Replace(changed, "<td>30倍</td>", "<td>1:30</td>"));
                Check("16l 槓桿欄看不懂時拒絕更新且不覆蓋", !r3.Ok && r3.Error.Contains("看不懂") && File.ReadAllText(file) == before2, r3.Error);
                // 抓到的表比原表少一成以上：拒絕（只留外匯、商品、指數、港股各類，美股刪掉大半）
                string shrunk = System.Text.RegularExpressions.Regex.Replace(changed, "<tr>\\s*<td>[A-M][A-Z0-9\\-]*(?:-1x)?\\.US</td>.*?</tr>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                var r4 = LeverageTable.Update(shrunk);
                Check("16m 新表比原表少一成以上時拒絕", !r4.Ok && r4.Error.Contains("少一成") && File.ReadAllText(file) == before2, r4.Error);
                // 匯出檔 3.33333 % 與官方 100/30 視為相同，不出「不一致」提醒
                string dir2 = Path.Combine(tmp, "LEV", "EURUSD");
                Directory.CreateDirectory(dir2);
                File.WriteAllText(Path.Combine(dir2, "H1.csv"), "time_utc,open,high,low,close\r\n2026-01-01 00:00,1,1,1,1\r\n2026-01-01 01:00,1,1,1,1\r\n");
                File.WriteAllText(Path.Combine(dir2, "symbol_info.txt"), "Symbol=EURUSD\r\nMinimalTickSize=0.00001\r\n[SymbolInfo]\r\n所需保證金=3.33333 %\r\n", Encoding.UTF8);
                var n2 = new List<string>();
                var s2 = Defaults.For(SymbolData.Open(dir2), "H1", n2);
                Check("16n 3.33333% 與官方 30 倍不誤報不一致", Near(s2.Leverage, 30) && !n2.Any(n => n.Contains("群益官方表是")), string.Join(" ", n2));

                // 表上沒有的商品：用匯出檔的值並提醒
                string dir = Path.Combine(tmp, "LEV", "QQQQ.US");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "H1.csv"), "time_utc,open,high,low,close\r\n2026-01-01 00:00,1,1,1,1\r\n2026-01-01 01:00,1,1,1,1\r\n");
                File.WriteAllText(Path.Combine(dir, "symbol_info.txt"), "Symbol=QQQQ.US\r\nMinimalTickSize=0.001\r\n[SymbolInfo]\r\n所需保證金=25 %\r\n", Encoding.UTF8);
                var notes = new List<string>();
                var s = Defaults.For(SymbolData.Open(dir), "H1", notes);
                Check("16j 表上沒有的商品：用匯出檔 25% = 4 倍並提醒更新", Near(s.Leverage, 4) && notes.Any(n => n.Contains("官方槓桿表裡沒有")) && s.SpecMissing == null, s.Leverage + " / " + string.Join(" ", notes));

                // 官方表與匯出檔都沒有合約大小、槓桿：標記缺規格（畫面據此擋下回測，不讓使用者自己填）
                string dir3 = Path.Combine(tmp, "LEV", "ZZZZZZ");
                Directory.CreateDirectory(dir3);
                File.WriteAllText(Path.Combine(dir3, "H1.csv"), "time_utc,open,high,low,close\r\n2026-01-01 00:00,1,1,1,1\r\n2026-01-01 01:00,1,1,1,1\r\n");
                File.WriteAllText(Path.Combine(dir3, "symbol_info.txt"), "Symbol=ZZZZZZ\r\nMinimalTickSize=0.001\r\n[SymbolInfo]\r\n", Encoding.UTF8);
                var s3 = Defaults.For(SymbolData.Open(dir3), "H1", null);
                Check("16o 缺合約大小與槓桿的商品：標記缺規格", s3.SpecMissing != null && s3.SpecMissing.Contains("合約大小") && s3.SpecMissing.Contains("槓桿") && !s3.SpecMissing.Contains("跳動點"), s3.SpecMissing ?? "null");
            }
            finally { LeverageTable.FilePath = null; }
            FxTests(tmp);
            Check("16k 測試結束後恢復預設表", LeverageTable.Count >= 600, LeverageTable.FilePath);
        }

        static void StatsTests()
        {
            var r = new BacktestResult { Settings = new TesterSettings { InitialDeposit = 1000 } };
            r.Trades.Add(new TradeRecord { Profit = 100, IsLong = true, BalanceAfter = 1100, Reason = "停利" });
            r.Trades.Add(new TradeRecord { Profit = -50, IsLong = false, BalanceAfter = 1050, Reason = "停損" });
            r.Trades.Add(new TradeRecord { Profit = -30, IsLong = true, BalanceAfter = 1020, Reason = "停損" });
            var st = Stats.From(r);
            Check("10a 淨利", Near(st.NetProfit, 20));
            Check("10b 獲利因子 100/80", Near(st.ProfitFactor, 1.25));
            Check("10c 最大連敗 2", st.MaxConsecLosses == 2 && Near(st.MaxConsecLossMoney, -80));
            Check("10d 多單勝率", st.Longs == 2 && st.LongWins == 1);
            r.Trades.Add(new TradeRecord { Profit = 0, IsLong = true, BalanceAfter = 1020, Reason = "策略平倉" });
            st = Stats.From(r);
            Check("10e 損益 0 不算虧損", st.Losses == 2 && st.Wins == 1 && st.MaxConsecLosses == 2, st.Wins + " 勝 " + st.Losses + " 敗");
        }

        // 用真實策略檔：編譯（不改原檔）→ 回測 → 報告
        // files = null：編譯並跑資料夾裡所有 *.cs（預設是 samples\strategies）；給清單則只跑清單（--real 的私人策略）
        static void RealStrategies(string dataRoot, string scriptsDir, string outDir, string[] files = null)
        {
            if (files == null)
                files = Directory.GetFiles(scriptsDir, "*.cs").Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            Check("R 找到範例策略檔 " + scriptsDir, files.Length > 0, files.Length + " 支");
            foreach (string sym in new[] { "EURUSD", "XAUUSD" })
            {
                string folder = Path.Combine(dataRoot, sym);
                if (!Directory.Exists(folder)) { Check("R 資料夾 " + sym, false, "找不到"); continue; }
                var d = SymbolData.Open(folder);
                // MtfProbe 會寫記錄到 文件\群益交易王CTA\Export\RoyAuto；那是給交易王實跑用的，測完刪掉自己產生的
                string probeLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export", "RoyAuto", "mtf_probe_" + sym + ".txt");
                bool probeExisted = File.Exists(probeLog);
                string probeBackup = probeExisted ? File.ReadAllText(probeLog) : null;
                foreach (string file in files)
                {
                    string path = Path.Combine(scriptsDir, file);
                    var cr = StrategyLoader.Compile(path);
                    Check("R 編譯 " + file, cr.Ok, cr.Ok ? "" : cr.Output.Split('\n')[0]);
                    if (!cr.Ok) continue;
                    foreach (var info in cr.Strategies)
                    {
                        var notes = new List<string>();
                        var s = Defaults.For(d, "H1", notes);
                        var p = new Dictionary<string, object>();
                        if (info.Params.Any(x => x.Name == "EnableOrders")) p["EnableOrders"] = true;   // RoyAuto 預設不送單
                        // Part7Pullback 預設會寫 文件\群益交易王CTA\Export\Part7Pullback\trades_*.csv；自檢不可動真實 Export
                        if (info.Params.Any(x => x.Name == "WriteCsv")) p["WriteCsv"] = false;
                        // 範例資料沒有 H4／W1：多週期過濾開著會整段不下單（另有 R-MTF 檢查），這裡只測下單流程
                        if (info.Params.Any(x => x.Name == "MtfEnable"))
                        {
                            var pm = new Dictionary<string, object>(p);
                            var rm = Tester.Run(d, info.Type, pm, Defaults.For(d, "H1", null), null, null);
                            bool missing = !d.HasTimeframe("H4");
                            Check("R-MTF RoyAuto 缺上層資料時不下單、日誌寫出原因 " + sym,
                                  !missing || (rm.Trades.Count == 0 && rm.Log.Any(x => x.Contains("H4 資料還沒到"))),
                                  missing ? "缺 H4，交易 " + rm.Trades.Count : "有 H4，不適用");
                            p["MtfEnable"] = false;
                        }
                        var r = Tester.Run(d, info.Type, p, s, null, null);
                        string tag = sym + " " + info.ProjectName;
                        Check("R 跑完 " + tag, r.Error == null && r.Bars > 0, string.Format("K 棒 {0}，報價 {1}，交易 {2}，策略錯誤 {3}，耗時 {4:F1}s {5}", r.Bars, r.Ticks, r.Trades.Count, r.StrategyErrors, r.Elapsed.TotalSeconds, r.Error));
                        string html = HtmlReport.Build(r, true);
                        File.WriteAllText(Path.Combine(outDir, "report_" + sym + "_" + info.Type.Name + ".html"), html, new UTF8Encoding(true));
                        // 帳務一致：初始資金＋所有交易損益 = 最後餘額
                        double sum = r.Trades.Sum(t => t.Profit);
                        Check("R 帳務一致 " + tag, Math.Abs(s.InitialDeposit + sum - r.Broker.Balance) < 1e-6, (s.InitialDeposit + sum) + " vs " + r.Broker.Balance);
                        // 19. K 線圖：每筆進出場標記落在對應時間那根 K 棒，價格在那根高低範圍內
                        if (r.Trades.Count > 0)
                        {
                            var cd = ChartData.Build(d.Load("H1"), r);
                            int bad = 0; string firstBad = "";
                            foreach (var m in cd.Marks)
                            {
                                var t = m.Trade;
                                bool ok = m.EntryIndex >= 0 && m.ExitIndex >= m.EntryIndex
                                    && cd.Bars[m.EntryIndex].Time <= t.OpenTime && (m.EntryIndex + 1 >= cd.Bars.Count || cd.Bars[m.EntryIndex + 1].Time > t.OpenTime)
                                    && cd.Bars[m.ExitIndex].Time <= t.CloseTime && (m.ExitIndex + 1 >= cd.Bars.Count || cd.Bars[m.ExitIndex + 1].Time > t.CloseTime)
                                    && cd.PriceInBar(m.EntryIndex, t.OpenPrice, s.TickSize) && cd.PriceInBar(m.ExitIndex, t.ClosePrice, s.TickSize);
                                if (!ok) { bad++; if (firstBad.Length == 0) firstBad = "#" + t.No + " " + t.OpenTime + " " + t.OpenPrice + " → " + t.CloseTime + " " + t.ClosePrice; }
                            }
                            Check("19 K 線圖進出場標記位置正確 " + tag, cd.Marks.Count == r.Trades.Count && bad == 0, cd.Marks.Count + " 筆，錯 " + bad + " " + firstBad);
                        }
                        // 20. 均線疊加：從參數自動帶出週期與通道，SMA 值與手算一致（含回測開始前的暖機段）
                        if (info.Type.Name == "MaCrossStrategy" || info.Type.Name == "SampleMaCrossStrategy")
                        {
                            var periods = new List<int>();
                            double band;
                            bool got = ChartData.SuggestSma(r, periods, out band);
                            Check("20a 均線自動帶入 " + sym, got && periods.SequenceEqual(new[] { 120, 12 }) && Math.Abs(band - 2) < 1e-9,
                                string.Join(",", periods) + " ±" + band);
                            var series = d.Load("H1");
                            var cd2 = ChartData.Build(series, r);
                            cd2.SetSma(periods, band);
                            double maxErr = 0; int nan = 0;
                            var v120 = cd2.Lines[0].Values;
                            for (int i = 0; i < cd2.Bars.Count; i++)
                            {
                                int j = cd2.Offset + i;
                                if (j < 119) { if (!double.IsNaN(v120[i])) nan++; continue; }
                                double acc = 0;
                                for (int q = 0; q < 120; q++) acc += series.Bars[j - q].Close;
                                if (double.IsNaN(v120[i])) nan++;
                                else maxErr = Math.Max(maxErr, Math.Abs(v120[i] - acc / 120));
                            }
                            bool bandOk = cd2.Lines.Count == 4 && cd2.Lines[1].Dashed && Math.Abs(cd2.Lines[1].Values[cd2.Bars.Count - 1] - v120[cd2.Bars.Count - 1] * 1.02) < 1e-9;
                            Check("20b SMA 與手算一致、通道 = 均線 ±2% " + sym, maxErr < 1e-9 && nan == 0 && bandOk, "最大誤差 " + maxErr + "，不該是 NaN 的 " + nan + "，線數 " + cd2.Lines.Count);
                        }
                        if (info.Type.Name == "RoyAutoStrategy")
                        {
                            bool sltp = r.Trades.All(t => !double.IsNaN(t.StopLoss) && !double.IsNaN(t.TakeProfit));
                            Check("R RoyAuto 有交易且每筆有停損停利 " + sym, r.Trades.Count > 0 && sltp, "交易 " + r.Trades.Count);
                        }
                    }
                }
                try
                {
                    if (probeExisted) File.WriteAllText(probeLog, probeBackup, new UTF8Encoding(true));
                    else if (File.Exists(probeLog)) File.Delete(probeLog);
                }
                catch { }
            }
        }

        // 22. 資料橋不重複抓（spec 3.5）：沒資料要全部、有資料只要尾段、剛抓過自動觸發時略過、過期或手動仍抓
        // 23. 模擬時鐘（spec 4.1）
        static void SimClockTests(string tmp)
        {
            string src = "var a = DateTime.UtcNow; var b = System.DateTime.Now.Ticks; var c = global::System.DateTime.UtcNow; var d = MyDateTime.Now; var e = x.DateTime.Now; var f = DateTime.Today;";
            string got = StrategyLoader.UseSimClock(src);
            string want = "var a = (global::TradeApi.CtaBacktestInfo.UtcNow); var b = (global::TradeApi.CtaBacktestInfo.Now).Ticks; var c = (global::TradeApi.CtaBacktestInfo.UtcNow); var d = MyDateTime.Now; var e = x.DateTime.Now; var f = DateTime.Today;";
            Check("23a 只換 DateTime.UtcNow／Now", got == want, got);

            Check("23b 不在回測中 = 真實時間", Math.Abs((TradeApi.CtaBacktestInfo.UtcNow - DateTime.UtcNow).TotalSeconds) < 5);

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var bars = new List<Bar>();
            for (int i = 0; i < 8; i++) bars.Add(B(t0.AddHours(i), 100, 100.5, 99.5, 100));
            var d = SymbolData.Open(MakeSymbol(tmp, "CLK", bars));
            bool ok = true; int n = 0; string bad = "";
            ProbeStrategy.OnInit = null;
            ProbeStrategy.OnUpdate = (st, a) =>
            {
                DateTime now = TradeApi.CtaBacktestInfo.UtcNow;
                DateTime bar = st.HistoryDataSeries.GetTimeUtc(0);
                n++;
                if (now.Kind != DateTimeKind.Utc || now < bar || now >= bar.AddHours(1)) { ok = false; bad = bar + " / " + now; }
            };
            Tester.Run(d, typeof(ProbeStrategy), null, Settings(d, TickMode.BarOhlc), null, null);
            ProbeStrategy.OnUpdate = null;
            Check("23c 回測中 UtcNow 落在目前 K 棒內", ok && n > 0, "呼叫 " + n + " " + bad);
        }

        static void BridgePlanTests(string tmp)
        {
            string root = Path.Combine(tmp, "bridge_plan");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string folder = Path.Combine(root, "EURUSD");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "H1.csv"), "time_utc,open,high,low,close\r\n2026-11-01 10:00,1,1,1,1\r\n2026-11-09 23:00,1.1,1.2,1.0,1.15\r\n", new UTF8Encoding(false));
            DateTime now = new DateTime(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc);
            var tfs = new List<string> { "H1", "M1" };

            DateTime? last = Bridge.LastBarTime(Path.Combine(folder, "H1.csv"));
            Check("22a 讀 CSV 最後一根", last == new DateTime(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc), last.ToString());

            var p1 = Bridge.PlanFetch(root, "EURUSD", tfs, true, now, null);
            Check("22b 沒抓過：H1 從最後一根往前 2 天、M1 沒檔要全部",
                p1.Count == 2 && p1[0].Tf == "H1" && p1[0].From == new DateTime(2026, 11, 7) && p1[1].Tf == "M1" && p1[1].From == null,
                string.Join(" ", p1.Select(x => x.Tf + "@" + x.From)));

            Bridge.MarkFetched(folder, new[] { "H1" }, now.AddMinutes(-10));
            var skipped = new List<string>();
            var p2 = Bridge.PlanFetch(root, "EURUSD", tfs, true, now, skipped);
            Check("22c 10 分鐘前抓過 H1：自動觸發略過 H1，M1 照抓", p2.Count == 1 && p2[0].Tf == "M1" && skipped.Count == 1 && skipped[0].StartsWith("H1"),
                string.Join(" ", p2.Select(x => x.Tf)) + " | " + string.Join(" ", skipped));

            var p3 = Bridge.PlanFetch(root, "EURUSD", tfs, false, now, null);
            Check("22d 手動更新不看新鮮度，但仍只抓尾段", p3.Count == 2 && p3[0].From == new DateTime(2026, 11, 7), string.Join(" ", p3.Select(x => x.Tf + "@" + x.From)));

            Bridge.MarkFetched(folder, new[] { "H1" }, now.AddMinutes(-61));
            var p4 = Bridge.PlanFetch(root, "EURUSD", new List<string> { "H1" }, true, now, null);
            Check("22e 超過 60 分鐘就再抓", p4.Count == 1 && p4[0].Tf == "H1", p4.Count.ToString());

            File.WriteAllText(Path.Combine(folder, "D1.csv"), "time_utc,open,high,low,close\r\n2026-11-09 16:00,1,1,1,1\r\n", new UTF8Encoding(false));
            Bridge.MarkFetched(folder, new[] { "D1" }, now.AddHours(-5));
            var p5 = Bridge.PlanFetch(root, "EURUSD", new List<string> { "D1" }, true, now, null);
            Check("22f D1 5 小時前抓過仍算新（門檻 12 小時）", p5.Count == 0, p5.Count.ToString());

            string id = Bridge.Submit(root, "EURUSD", p1);
            string req = File.ReadAllText(Path.Combine(root, "requests", id + ".req"));
            Check("22g 請求單寫各週期起始日", req.Contains("from.H1=2026-11-07") && req.Contains("from.M1=\r\n") && req.Contains("timeframes=H1,M1"), req);
        }

        // 24. 參數最佳化（spec 12）：只用合成資料與測試用策略
        static void OptimizerTests(string tmp)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var infos = StrategyLoader.Discover(typeof(MaCross).Assembly);
            var ma = infos.First(x => x.Type == typeof(MaCross));
            ParamInfo pPeriod = ma.Params.First(x => x.Name == "Period"), pLots = ma.Params.First(x => x.Name == "Lots");
            string err;

            // 24a～24d 候選值展開
            var v = OptRanges.Expand(pPeriod, 10, 5, 20, out err);
            Check("24a 候選值展開（整數）10～20 步 5", err == null && v != null && v.Count == 3 && v.All(x => x is int) && (int)v[0] == 10 && (int)v[1] == 15 && (int)v[2] == 20,
                  err ?? string.Join(",", v));
            v = OptRanges.Expand(pLots, 0.1, 0.05, 0.2, out err);
            Check("24b 候選值展開（浮點容差）0.1～0.2 步 0.05", err == null && v != null && v.Count == 3 && Near((double)v[0], 0.1, 1e-9) && Near((double)v[1], 0.15, 1e-9) && Near((double)v[2], 0.2, 1e-9),
                  err ?? string.Join(",", v.Select(x => Convert.ToString(x, inv))));
            string e1, e2, e3, e4;
            OptRanges.Expand(pPeriod, 10, 0.5, 12, out e1);
            OptRanges.Expand(pPeriod, 10, 0, 12, out e2);
            OptRanges.Expand(pPeriod, 20, 1, 10, out e3);
            OptRanges.Expand(pPeriod, 1, 1, 5, out e4);
            Check("24c 展開錯誤：非整數步、步 0、起大於終、低於最小值都報錯且點名「均線週期」",
                  new[] { e1, e2, e3, e4 }.All(x => x != null && x.Contains("均線週期")), string.Join(" | ", new[] { e1, e2, e3, e4 }.Select(x => x ?? "null")));
            var pFlag = new ParamInfo { Field = typeof(OptFlagHolder).GetField("Flag"), Label = "旗標" };
            v = OptRanges.Expand(pFlag, 0, 0, 0, out err);
            Check("24d bool 展開 = [關閉, 開啟]", err == null && v != null && v.Count == 2 && Equals(v[0], false) && Equals(v[1], true), err ?? "");

            // 24e、24f 組合計畫
            Func<string, int, OptParam> mkp = (name, n) => new OptParam { Name = name, Label = name, ValueType = typeof(int), Values = Enumerable.Range(1, n).Select(x => (object)x).ToList() };
            var three = new List<OptParam> { mkp("A", 3), mkp("B", 2), mkp("C", 4) };
            Func<Dictionary<string, object>, string> key = c => string.Join(",", c.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
            Func<OptSettings, List<Dictionary<string, object>>> plan = o => new Optimizer(null, typeof(MaCross), null, new TesterSettings(), three, o).BuildPlan();
            var full = plan(new OptSettings());
            Check("24e 組合數 3x2x4 = 24、窮舉 24 組不重複、首組全取第一個值、末組全取最後一個值",
                  OptRanges.CountCombinations(three) == 24 && full.Count == 24 && full.Select(key).Distinct().Count() == 24
                  && key(full[0]) == "A=1,B=1,C=1" && key(full[23]) == "A=3,B=2,C=4" && key(full[1]) == "A=1,B=1,C=2",
                  full.Count + " 組，首 " + (full.Count > 0 ? key(full[0]) : "") + "，末 " + (full.Count > 0 ? key(full[full.Count - 1]) : ""));
            var r7 = plan(new OptSettings { Mode = OptMode.Random, RandomCount = 10, Seed = 7 });
            var r7b = plan(new OptSettings { Mode = OptMode.Random, RandomCount = 10, Seed = 7 });
            var r8 = plan(new OptSettings { Mode = OptMode.Random, RandomCount = 10, Seed = 8 });
            var rAll = plan(new OptSettings { Mode = OptMode.Random, RandomCount = 100, Seed = 7 });
            Check("24f 隨機抽樣：10 組不重複、同種子相同、換種子不同、抽樣數超過總數 = 窮舉",
                  r7.Count == 10 && r7.Select(key).Distinct().Count() == 10 && r7.Select(key).SequenceEqual(r7b.Select(key))
                  && !r7.Select(key).SequenceEqual(r8.Select(key)) && rAll.Count == 24 && rAll.Select(key).SequenceEqual(full.Select(key)),
                  r7.Count + " / " + r7.Select(key).Distinct().Count() + " / " + rAll.Count);

            // 合成資料 OPT：300 根 H1，收盤 = 100 + 3 sin(i/8)，SMA 交叉會多次觸發
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var bars = new List<Bar>();
            double prev = 100;
            for (int i = 0; i < 300; i++)
            {
                double c = 100 + 3 * Math.Sin(i / 8.0);
                bars.Add(B(t0.AddHours(i), prev, Math.Max(prev, c) + 0.5, Math.Min(prev, c) - 0.5, c));
                prev = c;
            }
            var d = SymbolData.Open(MakeSymbol(tmp, "OPT", bars));
            var s = Settings(d, TickMode.BarOhlc);
            s.FromUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            s.ToUtc = new DateTime(2026, 1, 13, 12, 0, 0, DateTimeKind.Utc);
            var baseP = new Dictionary<string, object> { { "Period", 20 }, { "Lots", 0.1 } };
            Func<double, double, double, List<OptParam>> periods = (a, st, z) =>
            {
                string er;
                return new List<OptParam> { new OptParam { Name = "Period", Label = pPeriod.Label, ValueType = typeof(int), Values = OptRanges.Expand(pPeriod, a, st, z, out er) } };
            };
            Func<int, Stats> single = per => Stats.From(Tester.Run(d, typeof(MaCross), new Dictionary<string, object> { { "Period", per }, { "Lots", 0.1 } }, s, null, null));

            // 24g 每組結果 = 單獨回測
            var ps3 = periods(10, 5, 20);
            var res3 = new Optimizer(d, typeof(MaCross), baseP, s, ps3, new OptSettings { Threads = 1 }).Run(null, null);
            bool same = res3.Count == 3 && res3.All(r => r.Error == null && r.InSample != null);
            string det = "";
            if (same)
                foreach (var r in res3)
                {
                    var st = single((int)r.Params["Period"]);
                    bool ok = Near(r.InSample.NetProfit, st.NetProfit, 1e-9) && r.InSample.Trades == st.Trades && Near(r.InSample.EquityDDPct, st.EquityDDPct, 1e-9);
                    same &= ok;
                    det += "P" + r.Params["Period"] + " 淨利 " + r.InSample.NetProfit.ToString("F2", inv) + "/" + st.NetProfit.ToString("F2", inv) + " 筆 " + r.InSample.Trades + "/" + st.Trades + "；";
                }
            Check("24g 每組結果 = 單獨 Tester.Run、至少一組有交易", same && res3.Any(r => r.InSample.Trades > 0), det + res3.Count + " 組 " + string.Join(" ", res3.Select(r => r.Error)));

            // 24h 平行 = 單執行緒
            var ps10 = periods(5, 5, 50);
            var one = new Optimizer(d, typeof(MaCross), baseP, s, ps10, new OptSettings { Threads = 1 }).Run(null, null);
            var par = new Optimizer(d, typeof(MaCross), baseP, s, ps10, new OptSettings { Threads = 3 }).Run(null, null);
            bool eq = one.Count == 10 && par.Count == 10;
            for (int i = 0; eq && i < 10; i++)
            {
                Stats a = one[i].InSample, b = par[i].InSample;
                eq = one[i].Pass == par[i].Pass && a != null && b != null && one[i].Error == null && par[i].Error == null
                     && a.NetProfit == b.NetProfit && a.Trades == b.Trades && a.GrossProfit == b.GrossProfit && a.GrossLoss == b.GrossLoss && a.EquityDD == b.EquityDD;
            }
            Check("24h 3 執行緒與單執行緒逐組結果完全相同（10 組）", eq, one.Count + " / " + par.Count + " 組，交易 " + string.Join(",", one.Select(r => r.InSample == null ? -1 : r.InSample.Trades)));

            // 24i 共用變數隔離：兩個回測同時跑，各自看到自己寫的值；主執行緒的不被清掉
            int mismatch = 0;
            TradeApi.ToolBelt.GlobalVariableManager.RemoveAll();
            TradeApi.ToolBelt.GlobalVariableManager.SetValue("main", 1, TradeApi.ToolBelt.VariableLifetime.SaveSession);
            using (var barrier = new System.Threading.Barrier(2))
            {
                ProbeStrategy.OnInit = st => barrier.SignalAndWait(10000);   // 兩個都進到 Init 才開始跑，確保重疊
                ProbeStrategy.OnUpdate = (st, a) =>
                {
                    int me = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (!TradeApi.ToolBelt.GlobalVariableManager.Exists("who")) TradeApi.ToolBelt.GlobalVariableManager.SetValue("who", me, TradeApi.ToolBelt.VariableLifetime.SaveSession);
                    else if (!Equals(TradeApi.ToolBelt.GlobalVariableManager.GetValue("who"), me)) System.Threading.Interlocked.Increment(ref mismatch);
                };
                var ta = System.Threading.Tasks.Task.Run(() => Tester.Run(d, typeof(ProbeStrategy), null, s, null, null));
                var tb = System.Threading.Tasks.Task.Run(() => Tester.Run(d, typeof(ProbeStrategy), null, s, null, null));
                System.Threading.Tasks.Task.WaitAll(ta, tb);
                Check("24i 共用變數每執行緒一份：兩個同時跑的回測互不干擾、主執行緒的不被清掉",
                      mismatch == 0 && ta.Result.Error == null && tb.Result.Error == null && ta.Result.StrategyErrors == 0 && tb.Result.StrategyErrors == 0
                      && Equals(TradeApi.ToolBelt.GlobalVariableManager.GetValue("main"), 1) && TradeApi.ToolBelt.GlobalVariableManager.Count == 1,
                      "不一致 " + mismatch + "，主執行緒變數數 " + TradeApi.ToolBelt.GlobalVariableManager.Count + " " + ta.Result.Error + tb.Result.Error);
            }
            ProbeStrategy.OnInit = null; ProbeStrategy.OnUpdate = null;
            TradeApi.ToolBelt.GlobalVariableManager.RemoveAll();

            // 24j 取消：第 3 組完成後要求停止
            int done = 0;
            List<OptResult> cres = null; Exception cex = null;
            try
            {
                cres = new Optimizer(d, typeof(MaCross), baseP, s, ps10, new OptSettings { Threads = 2 })
                    .Run(r => System.Threading.Interlocked.Increment(ref done), () => System.Threading.Volatile.Read(ref done) >= 3);
            }
            catch (Exception ex) { cex = ex; }
            Check("24j 取消：正常返回、結果少於 10 組、至少 3 組完整",
                  cex == null && cres != null && cres.Count < 10 && cres.Count(r => !r.Cancelled && r.Error == null) >= 3,
                  cex != null ? cex.Message : cres.Count + " 組，完整 " + cres.Count(r => !r.Cancelled && r.Error == null));

            // 24k 排序規則
            Func<int, double, double, double, int, string, OptResult> mk = (pass, np, pf, dd, trades, error) => new OptResult
            {
                Pass = pass, Error = error, Params = new Dictionary<string, object>(),
                InSample = new Stats { NetProfit = np, ProfitFactor = pf, EquityDDPct = dd, Trades = trades, Wins = trades / 2 }
            };
            var A = mk(1, 100, 2, 10, 5, null);
            var Bq = mk(2, -50, 0.5, 20, 4, null);
            var C = mk(3, 0, 0, 0, 0, null);
            var D = mk(4, 500, 9, 1, 9, "x");
            var E = mk(5, 30, double.PositiveInfinity, 5, 2, null);
            var all5 = new List<OptResult> { C, D, Bq, E, A };
            Func<OptSortKey, bool, string> order = (k, desc) =>
            {
                var l = new List<OptResult>(all5);
                l.Sort((x, y) => Optimizer.Compare(x, y, k, desc));
                return string.Join("", l.Select(x => x == A ? "A" : x == Bq ? "B" : x == C ? "C" : x == D ? "D" : "E"));
            };
            string o1 = order(OptSortKey.NetProfit, true), o2 = order(OptSortKey.ProfitFactor, true), o3 = order(OptSortKey.EquityDDPct, false), o4 = order(OptSortKey.NetProfit, false);
            Check("24k 排序：淨利降冪 AEB 後接 CD、獲利因子 ∞ 第一、回撤升冪有值在前、升冪時 0 筆與錯誤仍最後",
                  o1 == "AEBCD" && o2.StartsWith("E") && o2.EndsWith("CD") && o3 == "EABCD" && o4 == "BEACD" && !Optimizer.SmallerIsBetter(OptSortKey.NetProfit) && Optimizer.SmallerIsBetter(OptSortKey.EquityDDPct),
                  o1 + " / " + o2 + " / " + o3 + " / " + o4);

            // 24l 前進測試切點
            var sf = new TesterSettings { FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ToUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
            TesterSettings sIn, sOut, x1, x2; DateTime split, sp2, sp3;
            bool okSplit = Optimizer.SplitForward(sf, 0.25, out sIn, out sOut, out split);
            var s1d = new TesterSettings { FromUtc = sf.FromUtc, ToUtc = sf.FromUtc.AddDays(1) };
            bool bad1 = Optimizer.SplitForward(sf, 0.999, out x1, out x2, out sp2), bad2 = Optimizer.SplitForward(s1d, 0.25, out x1, out x2, out sp3);
            Check("24l 前進切點 = 2026-01-24（31 天 x 0.75 取整 23 天）；比例 0.999 或期間 1 天不允許",
                  okSplit && split == new DateTime(2026, 1, 24, 0, 0, 0, DateTimeKind.Utc) && sIn.ToUtc == split && sOut.FromUtc == split && sIn.FromUtc == sf.FromUtc && sOut.ToUtc == sf.ToUtc
                  && sf.ToUtc == new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) && !bad1 && !bad2,
                  split.ToString("yyyy-MM-dd HH:mm", inv) + " " + bad1 + " " + bad2);

            // 24m 前進測試實跑
            var fres = new Optimizer(d, typeof(MaCross), baseP, s, ps3, new OptSettings { Threads = 2, Forward = true, ForwardPct = 0.4 }).Run(null, null);
            TesterSettings fIn, fOut; DateTime fs;
            bool fOk = Optimizer.SplitForward(s, 0.4, out fIn, out fOut, out fs) && fres.Count == 3;
            string fdet = "切點 " + fs.ToString("yyyy-MM-dd", inv) + "；";
            if (fOk)
                foreach (var r in fres)
                {
                    var prm = new Dictionary<string, object> { { "Period", r.Params["Period"] }, { "Lots", 0.1 } };
                    Stats si = Stats.From(Tester.Run(d, typeof(MaCross), prm, fIn, null, null)), so = Stats.From(Tester.Run(d, typeof(MaCross), prm, fOut, null, null));
                    bool ok = r.Error == null && r.OutSample != null && Near(r.InSample.NetProfit, si.NetProfit, 1e-9) && r.InSample.Trades == si.Trades
                              && Near(r.OutSample.NetProfit, so.NetProfit, 1e-9) && r.OutSample.Trades == so.Trades;
                    fOk &= ok;
                    fdet += "P" + r.Params["Period"] + " 內 " + r.InSample.Trades + "/" + si.Trades + " 外 " + (r.OutSample == null ? -1 : r.OutSample.Trades) + "/" + so.Trades + "；";
                }
            Check("24m 前進測試：樣本內／外各等於單獨跑 [起, 切點)、[切點, 終)", fOk && fres.Any(r => r.OutSample != null && r.OutSample.Trades > 0), fdet);

            // 24n CSV
            string csv = Optimizer.ToCsv(res3, ps3, false);
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            int cols = lines.Length > 0 ? lines[0].Split(',').Length : 0;
            Check("24n CSV：標題 Pass,NetProfit,… 含 Period、行數 = 組數 + 1、各行欄數相同、數字有小數點",
                  lines.Length == res3.Count + 1 && lines[0].StartsWith("Pass,NetProfit,") && lines[0].Contains("Period")
                  && lines.All(l => l.Split(',').Length == cols) && lines.Skip(1).Any(l => l.Contains(".")),
                  lines.Length > 1 ? lines[0] + " | " + lines[1] : csv);

            // 24o KeepLog=false 不留日誌、交易不變
            var sk = s.Clone(); sk.KeepLog = false;
            var withLog = Tester.Run(d, typeof(MaCross), baseP, s, null, null);
            var noLog = Tester.Run(d, typeof(MaCross), baseP, sk, null, null);
            Check("24o KeepLog=false：日誌 0 行、交易筆數相同", noLog.Log.Count == 0 && withLog.Log.Count > 0 && noLog.Trades.Count == withLog.Trades.Count && noLog.Error == null,
                  withLog.Log.Count + " → " + noLog.Log.Count + " 行，交易 " + withLog.Trades.Count + " / " + noLog.Trades.Count);
        }

        // 25. 子程式（spec.md 13.2、13.8）：策略 DLL 就用本測試程式自己（裡面有 MaCross、HangStrategy、OverflowStrategy）
        static void WorkerTests(string tmp)
        {
            if (!File.Exists(WorkerClient.WorkerPath)) { Check("25 找到子程式 BacktestWorker.exe", false, WorkerClient.WorkerPath); return; }
            string self = typeof(MaCross).Assembly.Location;
            string folder = Path.Combine(tmp, "OPT");   // OptimizerTests 建的合成商品（有交易）
            var d = SymbolData.Open(folder);
            var s = Settings(d, TickMode.BarOhlc);
            s.FromUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            s.ToUtc = new DateTime(2026, 1, 13, 12, 0, 0, DateTimeKind.Utc);
            var p = new Dictionary<string, object> { { "Period", 15 }, { "Lots", 0.1 } };
            Func<string, WorkerJob> job = cls => new WorkerJob { DataFolder = folder, DllPath = self, StrategyClass = cls, Parameters = new Dictionary<string, object>(p), Settings = s.Clone() };

            // 25a DLL 載入器：本程式沒有平台參考，不缺成員
            var lr = DllLoader.Load(self);
            Check("25a DLL 載入器讀得到測試策略、不缺平台成員", lr.Ok && lr.Missing.Count == 0 && lr.Strategies.Any(x => x.Type.FullName == typeof(MaCross).FullName), lr.Output);

            // 25b 子程式結果 = 同程式內執行，逐筆相同
            var inProc = Tester.Run(d, typeof(MaCross), p, s.Clone(), null, null);
            var wc = new WorkerClient();
            int progressCalls = 0;
            wc.Progress = x => progressCalls++;
            var viaWorker = wc.RunBacktest(job(typeof(MaCross).FullName), null);
            Func<BacktestResult, string> sig = r => string.Join(";", r.Trades.Select(t => t.OpenTime.ToString("MMddHHmm") + (t.IsLong ? "L" : "S") + t.Profit.ToString("F4")));
            Check("25b 子程式回測 = 同程式內，逐筆相同、有進度回報", viaWorker != null && inProc.Trades.Count > 0 && sig(viaWorker) == sig(inProc) && progressCalls > 0
                  && viaWorker.Broker != null && Near(viaWorker.Broker.Balance, inProc.Broker.Balance, 1e-9),
                  (wc.Failure ?? "") + " 筆 " + inProc.Trades.Count + "/" + (viaWorker == null ? -1 : viaWorker.Trades.Count) + " 進度 " + progressCalls);

            // 25c 策略卡在無窮迴圈：逾時後結束子程式，說明卡在 Update
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hang = new WorkerClient { TimeoutSeconds = 3 };
            var hr = hang.RunBacktest(job(typeof(HangStrategy).FullName), null);
            Check("25c 無窮迴圈：3 秒逾時後停止、說明卡在 Update、20 秒內結束", hr == null && hang.Failure != null && hang.Failure.Contains("無窮迴圈") && hang.Failure.Contains("Update") && sw.Elapsed.TotalSeconds < 20,
                  (hang.Failure ?? "null") + " " + sw.Elapsed.TotalSeconds.ToString("F1") + " 秒");

            // 25d 卡住時按停止：寬限期後直接結束子程式
            sw.Restart();
            var stuck = new WorkerClient { TimeoutSeconds = 0, StopGraceMs = 1000 };
            DateTime stopAt = DateTime.UtcNow.AddSeconds(2);
            var sr = stuck.RunBacktest(job(typeof(HangStrategy).FullName), () => DateTime.UtcNow > stopAt);
            Check("25d 卡住時按停止：寬限 1 秒後強制停止、不算失敗、10 秒內結束", sr == null && stuck.ForcedStop && stuck.Failure == null && sw.Elapsed.TotalSeconds < 10,
                  "ForcedStop=" + stuck.ForcedStop + " " + (stuck.Failure ?? "") + " " + sw.Elapsed.TotalSeconds.ToString("F1") + " 秒");

            // 25e 正常回測按停止：子程式自己在下一根停下，結果標示已中止
            var stopNow = new WorkerClient();
            DateTime stopSlow = DateTime.UtcNow.AddSeconds(1.5);
            var cr = stopNow.RunBacktest(job(typeof(SlowStrategy).FullName), () => DateTime.UtcNow > stopSlow);
            Check("25e 正常回測按停止：子程式自己停、結果標示已中止", cr != null && cr.Cancelled && !stopNow.ForcedStop, (stopNow.Failure ?? "") + " " + (cr == null ? "null" : cr.Cancelled.ToString()));

            // 25f 堆疊溢位：子程式被系統結束，本程式不受影響、說明原因
            var ov = new WorkerClient();
            var orr = ov.RunBacktest(job(typeof(OverflowStrategy).FullName), null);
            Check("25f 堆疊溢位：子程式結束、本程式繼續、說明「堆疊溢位」", orr == null && ov.Failure != null && ov.Failure.Contains("堆疊溢位"), (ov.Failure ?? "null") + " 代碼 " + ov.ExitCode);

            // 25g 最佳化在子程式跑：每組結果 = 同程式內
            string er;
            var ma = StrategyLoader.Discover(typeof(MaCross).Assembly).First(x => x.Type == typeof(MaCross));
            var ops = new List<OptParam> { new OptParam { Name = "Period", Label = "均線週期", ValueType = typeof(int), Values = OptRanges.Expand(ma.Params.First(x => x.Name == "Period"), 10, 5, 20, out er) } };
            var local = new Optimizer(d, typeof(MaCross), p, s, ops, new OptSettings { Threads = 2 }).Run(null, null);
            var oj = job(typeof(MaCross).FullName); oj.OptParams = ops; oj.Opt = new OptSettings { Threads = 2 };
            int rows = 0;
            var owc = new WorkerClient { OptResultReceived = r => System.Threading.Interlocked.Increment(ref rows) };
            var remote = owc.RunOptimize(oj, null);
            bool same = remote.Count == local.Count && remote.Count == 3;
            for (int i = 0; same && i < remote.Count; i++) same = remote[i].Pass == local[i].Pass && Near(remote[i].InSample.NetProfit, local[i].InSample.NetProfit, 1e-9) && remote[i].InSample.Trades == local[i].InSample.Trades;
            Check("25g 最佳化在子程式跑：3 組結果 = 同程式內、每組即時回報", same && rows == 3 && owc.Failure == null, (owc.Failure ?? "") + " " + remote.Count + "/" + local.Count + " 回報 " + rows);

            // 25h 不留資金曲線：統計相同、曲線是空的
            var s2 = s.Clone(); s2.KeepCurve = false;
            var noCurve = Tester.Run(d, typeof(MaCross), p, s2, null, null);
            Stats a = Stats.From(inProc), b = Stats.From(noCurve);
            Check("25h KeepCurve=false：統計（淨利、回撤、夏普）相同、曲線為空", noCurve.Curve.Count == 0 && inProc.Curve.Count > 0 && Near(a.NetProfit, b.NetProfit, 1e-9) && Near(a.EquityDDPct, b.EquityDDPct, 1e-9) && Near(a.Sharpe, b.Sharpe, 1e-12), "");
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // 位置參數：資料夾、策略夾、輸出夾；--real <資料夾> = 另外跑使用者私人策略（選擇性）
            string realDir = null;
            var pos = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--real") { if (i + 1 < args.Length) realDir = args[++i]; }
                else pos.Add(args[i]);
            }
            string dataRoot = pos.Count > 0 ? pos[0] : null;
            string scripts = pos.Count > 1 ? pos[1] : null;
            string outDir = pos.Count > 2 ? pos[2] : Path.Combine(Path.GetTempPath(), "CtaBacktestSelfTest");
            Directory.CreateDirectory(outDir);
            string tmp = Path.Combine(outDir, "synthetic");
            try { UnitTests(tmp); } catch (Exception ex) { Check("單元測試執行", false, ex.ToString()); }
            try { StatsTests(); } catch (Exception ex) { Check("統計測試執行", false, ex.ToString()); }
            try { SpecTests(tmp); } catch (Exception ex) { Check("規格測試執行", false, ex.ToString()); }
            try { LeverageTests(tmp); } catch (Exception ex) { Check("槓桿表測試執行", false, ex.ToString()); }
            try { BridgePlanTests(tmp); } catch (Exception ex) { Check("資料橋偵測測試執行", false, ex.ToString()); }
            try { SimClockTests(tmp); } catch (Exception ex) { Check("模擬時鐘測試執行", false, ex.ToString()); }
            try { OptimizerTests(tmp); } catch (Exception ex) { Check("參數最佳化測試執行", false, ex.ToString()); }
            try { WorkerTests(tmp); } catch (Exception ex) { Check("子程式測試執行", false, ex.ToString()); }
            if (dataRoot != null && scripts != null)
            {
                try { RealStrategies(dataRoot, scripts, outDir); } catch (Exception ex) { Check("範例策略執行", false, ex.ToString()); }
            }
            if (dataRoot != null && realDir != null)
            {
                try { RealStrategies(dataRoot, realDir, outDir, new[] { "RoyAuto.cs", "MtfProbe.cs", "Part7Pullback.cs", "RiskManagement_Trade改.cs", "MaCross.cs" }); }
                catch (Exception ex) { Check("真實策略執行", false, ex.ToString()); }
            }
            string summary = "合計 PASS " + pass + "，FAIL " + fail;
            Console.WriteLine(summary);
            report.AppendLine(summary);
            File.WriteAllText(Path.Combine(outDir, "selftest_result.txt"), report.ToString(), new UTF8Encoding(true));
            return fail == 0 ? 0 : 1;
        }
    }
}
