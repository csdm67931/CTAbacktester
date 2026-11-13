using System;
using System.Collections.Generic;

namespace CtaBacktest
{
    // 群益官方商品規格（CTA 槓桿交易商）
    // 來源：
    // - 槓桿：https://cfmt.capitalfutures.com.tw/zh-TW/Product/Lever （2026-11-09 讀取）；
    //   實際以 data\official_leverage.csv 為準（LeverageTable，可在設定頁更新），下面的槓桿只在表上沒有時使用
    // - 手續費：https://cfmt.capitalfutures.com.tw/zh-TW/Product/Cost （2026-11-09 讀取）
    // - 跳動點、合約規格、最小手數：官網沒列，取自群益交易王平台的商品總表
    //   （VolRank\catalog\instruments.csv，2026-10-09 由平台 GetSymbolInfo 產生）；兩邊重疊的槓桿與合約規格逐項一致
    // 用途：匯出檔缺欄位時補值；匯出檔與官方表不同時在報告提醒
    public static class OfficialSpecs
    {
        public sealed class Spec
        {
            public readonly double TickSize, ContractSize, MarginPercent, FeePercent, MinLot, LotStep;
            public readonly string Currency, Category;
            public Spec(string category, double tick, double contract, double marginPct, double feePct, double minLot, double lotStep, string currency)
            {
                Category = category; TickSize = tick; ContractSize = contract; MarginPercent = marginPct; FeePercent = feePct;
                MinLot = minLot; LotStep = lotStep; Currency = currency;
            }
            public double Leverage { get { return Math.Round(100.0 / MarginPercent, 2); } }
        }

        private const double M30 = 3.33333, M20 = 5, M10 = 10;

        private static readonly Dictionary<string, Spec> table = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase)
        {
            // 外匯：合約 100,000 基準貨幣，手續費 0；涉及美元、歐元、日圓、英鎊、加幣者多為 30 倍，其餘 20 倍（以官網表逐項為準）
            { "AUDCAD", Fx(0.00001, M20, "CAD") }, { "AUDJPY", Fx(0.001, M20, "JPY") }, { "AUDNZD", Fx(0.00001, M20, "NZD") },
            { "AUDUSD", Fx(0.00001, M20, "USD") }, { "CADJPY", Fx(0.001, M30, "JPY") }, { "EURAUD", Fx(0.00001, M20, "AUD") },
            { "EURCAD", Fx(0.00001, M30, "CAD") }, { "EURCHF", Fx(0.00001, M20, "CHF") }, { "EURGBP", Fx(0.00001, M30, "GBP") },
            { "EURJPY", Fx(0.001, M30, "JPY") },   { "EURNZD", Fx(0.00001, M20, "NZD") }, { "EURUSD", Fx(0.00001, M30, "USD") },
            { "EURZAR", Fx(0.00001, M20, "ZAR") }, { "GBPAUD", Fx(0.00001, M20, "AUD") }, { "GBPCAD", Fx(0.00001, M30, "CAD") },
            { "GBPCHF", Fx(0.00001, M20, "CHF") }, { "GBPJPY", Fx(0.001, M30, "JPY") },   { "GBPNZD", Fx(0.00001, M20, "NZD") },
            { "GBPUSD", Fx(0.00001, M30, "USD") }, { "NZDCAD", Fx(0.00001, M20, "CAD") }, { "NZDJPY", Fx(0.001, M20, "JPY") },
            { "NZDUSD", Fx(0.00001, M20, "USD") }, { "USDCAD", Fx(0.00001, M30, "CAD") }, { "USDCHF", Fx(0.00001, M20, "CHF") },
            { "USDCNH", Fx(0.00001, M20, "CNH") }, { "USDJPY", Fx(0.001, M30, "JPY") },
            { "USDMXN", Fx(0.00001, M20, "MXN") }, { "USDSEK", Fx(0.00001, M20, "SEK") }, { "USDSGD", Fx(0.00001, M20, "SGD") },
            { "USDZAR", Fx(0.00001, M20, "ZAR") },

            // 商品：黃金 100 盎司 20 倍；白銀 5,000 盎司、原油 1,000 桶 10 倍；手續費 0
            { "XAUUSD", new Spec("貴金屬", 0.01, 100, M20, 0, 0.01, 0.01, "USD") },
            { "XAGUSD", new Spec("貴金屬", 0.001, 5000, M10, 0, 0.01, 0.01, "USD") },
            { "XTIUSD", new Spec("原油", 0.001, 1000, M10, 0, 0.01, 0.01, "USD") },
            { "XBRUSD", new Spec("原油", 0.001, 1000, M10, 0, 0.01, 0.01, "USD") },

            // 海外指數：手續費 0.01%（買賣分別計算）
            { "DJ30",    new Spec("指數", 0.01, 1, M20, 0.01, 0.1, 0.1, "USD") },
            { "NDAQ100", new Spec("指數", 0.01, 1, M20, 0.01, 0.1, 0.1, "USD") },
            { "SPX500",  new Spec("指數", 0.01, 1, M20, 0.01, 1, 1, "USD") },
            { "DAX30",   new Spec("指數", 0.01, 1, M20, 0.01, 0.1, 0.1, "EUR") },
            { "HK50",    new Spec("指數", 0.01, 1, M10, 0.01, 1, 1, "HKD") },
            { "JPN225",  new Spec("指數", 0.01, 10, M20, 0.01, 1, 1, "JPY") },
        };

        private static Spec Fx(double tick, double marginPct, string quote)
        {
            return new Spec("外匯", tick, 100000, marginPct, 0, 0.01, 0.01, quote);
        }

        // 個股的槓桿依個股不同，由官網槓桿表（LeverageTable）提供；
        // 這裡只給共同規則：跳動點 0.001、合約 1 股、最小 1 手、手續費率與最低收費
        public static Spec Stock(string symbol)
        {
            string mkt = symbol.Substring(symbol.LastIndexOf('.') + 1).ToUpperInvariant();
            // 美股：官方 0.10% 的 5 折 = 0.05%，免最低收費（使用者 2026-11-09 確認 2027 年延續）；港股 0.50%；歐股 0.25%；最低單筆 5 美元
            double fee = mkt == "US" ? 0.05 : mkt == "HK" ? 0.5 : 0.25;
            string cur = mkt == "US" ? "USD" : mkt == "HK" ? "HKD" : null;
            return new Spec("個股", 0.001, 1, double.NaN, fee, 1, 1, cur);
        }

        public const double StockMinFeeUsd = 5;

        // 強制沖銷：盤中保證金維持率（權益數 ÷ 保證金）低於 50% 時公司有權不經通知平倉。
        // 來源：官網「交易須知」https://cfmt.capitalfutures.com.tw/zh-TW/Client/Instruction （2026-11-13 讀取）。
        // 這是群益的規定，不是使用者可以調整的設定。
        public const double StopOutPercent = 50;

        // 交易王裡有報價、但不能交易的商品（使用者 2026-11-09 說明）
        public static readonly Dictionary<string, string> NonTradable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "USDHKD", "交易王用來換算港股損益的港幣匯率" },
        };

        // 槓桿以官網全商品槓桿表（LeverageTable）為準；表上沒有的才用這裡的內建值
        public static Spec Find(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;
            Spec s;
            if (!table.TryGetValue(symbol, out s) && Defaults.IsStock(symbol)) s = Stock(symbol);
            LeverageTable.Entry lv = LeverageTable.Find(symbol);
            if (lv == null) return s;
            if (s == null) return new Spec(lv.Group, double.NaN, double.NaN, lv.MarginPercent, double.NaN, double.NaN, double.NaN, null);
            return new Spec(s.Category, s.TickSize, s.ContractSize, lv.MarginPercent, s.FeePercent, s.MinLot, s.LotStep, s.Currency);
        }

        // 這個商品的槓桿是否來自官網槓桿表
        public static bool LeverageFromTable(string symbol) { return LeverageTable.Find(symbol) != null; }

        public static IEnumerable<KeyValuePair<string, Spec>> All { get { return table; } }
    }
}
