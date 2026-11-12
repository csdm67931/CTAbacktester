using System;
using System.Collections.Generic;
using Runtime.Script;
using TradeApi;
using TradeApi.History;
using TradeApi.Indicators;
using TradeApi.Instruments;
using TradeApi.Trading;

// ============================================================================
// SampleMaCross 範例策略（隨回測器附贈，給自我檢查與入門示範用）
//
// 規則（只看已收盤 K 棒，收盤價 SMA）：
// 1. 方向看長均線：收盤 > 長均線 x (1 + 區間%) 才做多；收盤 < 長均線 x (1 - 區間%) 才做空。
// 2. 進場看短均線：收盤由下往上穿過短均線 -> 買；由上往下穿過 -> 賣。
// 3. 反手：有反向部位先平倉再進場；沒有停損停利，只當流程示範。
// ============================================================================

namespace SampleMaCross
{
    public class SampleMaCrossStrategy : StrategyBuilder
    {
        [InputParameter(InputType.Numeric, "長均線週期（多空方向）LongPeriod", 0)]
        [SimpleNumeric(2D, 1000D, 0, 1)]
        public int LongPeriod = 120;

        [InputParameter(InputType.Numeric, "短均線週期（進場）ShortPeriod", 1)]
        [SimpleNumeric(1D, 500D, 0, 1)]
        public int ShortPeriod = 12;

        [InputParameter(InputType.Numeric, "長均線上下不進場區（%）NoTradeBandPct", 2)]
        [SimpleNumeric(0D, 50D, 2, 0.1)]
        public double NoTradeBandPct = 2.0;

        [InputParameter(InputType.Numeric, "手數 Lots", 3)]
        [SimpleNumeric(0.01D, 100D, 2, 0.01)]
        public double Lots = 0.1;

        private BuiltInIndicator longMa, shortMa;

        public SampleMaCrossStrategy()
        {
            Credentials.ProjectName = "SampleMaCross";
        }

        public override void Init()
        {
            longMa = IndicatorsManager.BuildIn.MA(HistoryDataSeries, LongPeriod, MAMode.SMA);
            shortMa = IndicatorsManager.BuildIn.MA(HistoryDataSeries, ShortPeriod, MAMode.SMA);
        }

        public override void Update(TickStatus args)
        {
            // 每根新 K 棒開出時判斷一次（看的是前一根與前前一根）
            if (args != TickStatus.IsBar || HistoryDataSeries.Count < Math.Max(LongPeriod, ShortPeriod) + 3) return;

            double c1 = HistoryDataSeries.GetValue(PriceType.Close, 1);
            double c2 = HistoryDataSeries.GetValue(PriceType.Close, 2);
            double s1 = shortMa.GetValue(1), s2 = shortMa.GetValue(2);
            double l1 = longMa.GetValue(1);
            double band = l1 * NoTradeBandPct / 100.0;

            bool up = c2 <= s2 && c1 > s1 && c1 > l1 + band;
            bool dn = c2 >= s2 && c1 < s1 && c1 < l1 - band;
            if (!up && !dn) return;

            foreach (var p in PositionsManager.GetPositions()) PositionsManager.Close(p);
            OrdersManager.Send(new OrderRequest(OrderType.Market, InstrumentsManager.Current, AccountManager.Current,
                up ? OrderSide.Buy : OrderSide.Sell, Lots, null));
        }
    }
}
