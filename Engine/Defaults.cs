using System;
using System.Collections.Generic;
using System.Globalization;

namespace CtaBacktest
{
    // 依商品資料算出「什麼都不改也合理」的預設回測設定
    public static class Defaults
    {
        public static int DigitsOf(double tick)
        {
            if (!(tick > 0)) return 5;
            string s = tick.ToString("F12", CultureInfo.InvariantCulture).TrimEnd('0');
            int dot = s.IndexOf('.');
            return dot < 0 ? 0 : Math.Min(10, s.Length - dot - 1);
        }

        // 官方值優先；匯出檔有值且不同時提醒（相對差 1e-4 以內視為相同，例：3.33333 與 100/30）
        private static double Pick(double official, double exported, string name, List<string> notes)
        {
            bool hasOff = !double.IsNaN(official), hasExp = !double.IsNaN(exported) && exported > 0;
            if (hasOff && hasExp && Math.Abs(official - exported) > 1e-4 * Math.Abs(official) && notes != null)
                notes.Add(string.Format(CultureInfo.InvariantCulture, "{0}：匯出檔是 {1}，群益官方表是 {2}，採用官方值；若官方已調整請改設定。", name, exported, official));
            return hasOff ? official : (hasExp ? exported : double.NaN);
        }

        // CTA 個股代號：AAPL.US、00700.HK、BMW.DE 這類「代號.市場」
        public static bool IsStock(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            int dot = symbol.LastIndexOf('.');
            return dot > 0 && dot >= symbol.Length - 3 && symbol.Length - dot - 1 == 2;
        }

        // notes 收集需要提醒使用者的事（例如讀不到合約大小）
        public static TesterSettings For(SymbolData d, string timeframe, List<string> notes)
        {
            var s = new TesterSettings { Timeframe = timeframe };
            // 以群益官方表為準（OfficialSpecs）；匯出檔的值不同時提醒，官方表沒有的才用匯出檔
            OfficialSpecs.Spec off = OfficialSpecs.Find(d.Symbol);
            string why;
            if (notes != null && OfficialSpecs.NonTradable.TryGetValue(d.Symbol ?? "", out why))
                notes.Add(d.Symbol + " 是" + why + "，實際不能交易；回測結果沒有參考價值。");
            double v;
            double tick = d.HeaderNumber("MinimalTickSize");
            if (!(tick > 0) && SymbolData.ParseNumber(d.Info("最小價格跳動點", "Tick size"), out v) && v > 0) tick = v;
            tick = Pick(off == null ? double.NaN : off.TickSize, tick, "最小跳動點", notes);
            if (tick > 0) s.TickSize = tick;
            else { s.TickSize = 0.00001; if (notes != null) notes.Add("讀不到最小跳動點，先用 0.00001，請確認。"); }
            s.PriceDigits = DigitsOf(s.TickSize);

            double ask = d.HeaderNumber("Ask"), bid = d.HeaderNumber("Bid");
            if (ask > 0 && bid > 0 && ask >= bid) s.Spread = Math.Round((ask - bid) / s.TickSize) * s.TickSize;
            else { s.Spread = 2 * s.TickSize; if (notes != null) notes.Add("匯出檔沒有買賣價，點差先用 2 個跳動點，請依實際情況調整。"); }

            double cs = Pick(off == null ? double.NaN : off.ContractSize, d.ContractSize, "合約大小", notes);
            if (cs > 0) s.ContractSize = cs;
            else { s.ContractSize = 1; if (notes != null) notes.Add("讀不到合約大小，先用 1，請在設定裡填正確值，否則損益與手數會錯。"); }

            // 槓桿：群益「所需保證金」是名目金額的 %（EURUSD 3.33333 % = 30 倍，XAUUSD 5 % = 20 倍）
            double mg = double.NaN;
            if (SymbolData.ParseNumber(d.Info("所需保證金", "Margin"), out v) && v > 0 && v <= 100) mg = v;
            mg = Pick(off == null ? double.NaN : off.MarginPercent, mg, "保證金比例（%）", notes);
            if (mg > 0) s.Leverage = Math.Round(100.0 / mg, 2);
            else { s.Leverage = 10; if (notes != null) notes.Add("讀不到這個商品的保證金比例，槓桿先用 1:10；請在設定頁按「更新官方槓桿表」，或依官網「全商品槓桿」表修改。"); }
            if (mg > 0 && !OfficialSpecs.LeverageFromTable(d.Symbol) && notes != null)
                notes.Add("官方槓桿表裡沒有 " + d.Symbol + "，槓桿取自內建值或匯出檔；群益新增商品時請按「更新官方槓桿表」。");

            // 手續費：群益「手續費率%」是名目金額的 %，買賣各收一次；個股最低單筆 5 美元（官網全商品費用頁）
            double fee = double.NaN;
            if (SymbolData.ParseNumber(d.Info("手續費率%", "Commission"), out v) && v >= 0) fee = v;
            fee = Pick(off == null ? double.NaN : off.FeePercent, fee, "手續費率（%）", notes);
            s.CommissionPercent = fee >= 0 ? fee : 0;
            if (double.IsNaN(fee) && off != null && off.Category != "外匯" && off.Category != "商品" && notes != null)
                notes.Add("讀不到這個商品的手續費率，先用 0；請依群益官網「全商品費用」頁填寫（指數 0.01%）。");
            if (IsStock(d.Symbol))
            {
                bool us = d.Symbol.EndsWith(".US", StringComparison.OrdinalIgnoreCase);
                s.CommissionMin = us ? 0 : OfficialSpecs.StockMinFeeUsd;
                if (notes != null) notes.Add(us
                    ? "美股依群益：手續費 0.10% 打 5 折（0.05%）、不收最低 5 美元，2027 年延續，已預設。"
                    : "個股依群益官網：最低單筆手續費 5 美元，已預設。");
            }

            double ml = d.HeaderNumber("MinimalLot");
            if (!(ml > 0) && SymbolData.ParseNumber(d.Info("最小交易量(手)", "Minimal lot"), out v) && v > 0) ml = v;
            ml = Pick(off == null ? double.NaN : off.MinLot, ml, "最小手數", notes);
            s.MinLot = ml > 0 ? ml : 0.01;
            double step = d.HeaderNumber("LotStep");
            if (!(step > 0) && SymbolData.ParseNumber(d.Info("最小跳動量(手)", "Lot step"), out v) && v > 0) step = v;
            step = Pick(off == null ? double.NaN : off.LotStep, step, "手數級距", notes);
            s.LotStep = step > 0 ? step : s.MinLot;
            double mx = d.HeaderNumber("MaximalLot");
            s.MaxLot = mx > 0 ? mx : 0;

            BarSeries series = d.Load(timeframe);
            if (series.Count > 0)
            {
                // 預設：留前 20%（最多 500 根）當暖機，其餘全部回測
                int warm = Math.Min(500, series.Count / 5);
                s.FromUtc = series.Bars[warm].Time.Date;
                s.ToUtc = series.Bars[series.Count - 1].Time.Date.AddDays(1);
            }

            // 匯率：從同資料夾的貨幣對匯出資料自動算（spec 6.2）；報價幣別先讀匯出檔，沒有用官方表
            string q = d.QuoteCurrency;
            if (string.IsNullOrEmpty(q) && off != null) q = off.Currency;
            FxRates.Apply(d, s, q, notes);
            return s;
        }
    }
}
