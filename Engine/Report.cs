using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace CtaBacktest
{
    // 回測統計（仿 MT5 報告欄位）
    [Serializable]
    public sealed class Stats
    {
        public double InitialDeposit, NetProfit, GrossProfit, GrossLoss, ProfitFactor, ExpectedPayoff, RecoveryFactor, Sharpe;
        public double BalanceDD, BalanceDDPct, EquityDD, EquityDDPct, AbsoluteDD;
        public int Trades, Wins, Losses, Longs, LongWins, Shorts, ShortWins;
        public double LargestWin, LargestLoss, AvgWin, AvgLoss;
        public int MaxConsecWins, MaxConsecLosses;
        public double MaxConsecWinMoney, MaxConsecLossMoney;
        public double TotalCommission;
        public Dictionary<string, int> ByReason = new Dictionary<string, int>();

        public static Stats From(BacktestResult r)
        {
            var st = new Stats { InitialDeposit = r.Settings.InitialDeposit };
            var b = r.Broker;
            var t = r.Trades;
            st.Trades = t.Count;
            foreach (var x in t)
            {
                if (x.Profit > 0) { st.Wins++; st.GrossProfit += x.Profit; st.LargestWin = Math.Max(st.LargestWin, x.Profit); }
                else if (x.Profit < 0) { st.Losses++; st.GrossLoss += x.Profit; st.LargestLoss = Math.Min(st.LargestLoss, x.Profit); }
                if (x.IsLong) { st.Longs++; if (x.Profit > 0) st.LongWins++; }
                else { st.Shorts++; if (x.Profit > 0) st.ShortWins++; }
                st.TotalCommission += x.Commission;
                int n; st.ByReason.TryGetValue(x.Reason, out n); st.ByReason[x.Reason] = n + 1;
            }
            st.NetProfit = st.GrossProfit + st.GrossLoss;
            st.ProfitFactor = st.GrossLoss < 0 ? st.GrossProfit / -st.GrossLoss : (st.GrossProfit > 0 ? double.PositiveInfinity : 0);
            st.ExpectedPayoff = st.Trades > 0 ? st.NetProfit / st.Trades : 0;
            st.AvgWin = st.Wins > 0 ? st.GrossProfit / st.Wins : 0;
            st.AvgLoss = st.Losses > 0 ? st.GrossLoss / st.Losses : 0;

            int cw = 0, cl = 0; double mw = 0, ml = 0;
            foreach (var x in t)
            {
                if (x.Profit > 0) { cw++; mw += x.Profit; cl = 0; ml = 0; }
                else if (x.Profit < 0) { cl++; ml += x.Profit; cw = 0; mw = 0; }   // 損益為 0 的不計入連勝連敗
                if (cw > st.MaxConsecWins || (cw == st.MaxConsecWins && mw > st.MaxConsecWinMoney)) { st.MaxConsecWins = cw; st.MaxConsecWinMoney = mw; }
                if (cl > st.MaxConsecLosses || (cl == st.MaxConsecLosses && ml < st.MaxConsecLossMoney)) { st.MaxConsecLosses = cl; st.MaxConsecLossMoney = ml; }
            }

            if (b != null)
            {
                st.BalanceDD = b.MaxBalanceDrawdown; st.BalanceDDPct = b.MaxBalanceDrawdownPct * 100;
                st.EquityDD = b.MaxEquityDrawdown; st.EquityDDPct = b.MaxEquityDrawdownPct * 100;
                st.AbsoluteDD = Math.Max(0, st.InitialDeposit - Math.Min(b.MinBalance, b.MinEquity));
            }
            st.RecoveryFactor = st.EquityDD > 0 ? st.NetProfit / st.EquityDD : 0;

            // 夏普比率：每筆交易報酬率（損益 ÷ 交易前餘額）的平均 ÷ 標準差（MT5 報告同樣以交易為單位，未年化）
            if (t.Count >= 2)
            {
                var rets = t.Select(x => { double before = x.BalanceAfter - x.Profit; return before > 0 ? x.Profit / before : 0; }).ToList();
                double mean = rets.Average();
                double sd = Math.Sqrt(rets.Sum(v => (v - mean) * (v - mean)) / (rets.Count - 1));
                st.Sharpe = sd > 0 ? mean / sd : 0;
            }
            return st;
        }
    }

    // 圖表刻度：步距取 1、2、5 × 10 的次方，範圍擴到刻度上
    public static class ChartAxis
    {
        public static List<double> NiceTicks(ref double min, ref double max, int count, out int decimals)
        {
            if (double.IsNaN(min) || double.IsNaN(max)) { min = 0; max = 1; }
            if (max - min < 1e-9) { double c = min; min = c - 1; max = c + 1; }
            double raw = (max - min) / Math.Max(1, count);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            double step = (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
            min = Math.Floor(min / step + 1e-9) * step;
            max = Math.Ceiling(max / step - 1e-9) * step;
            decimals = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
            var list = new List<double>();
            for (double v = min; v <= max + step * 0.5; v += step) list.Add(Math.Round(v, decimals + 2));
            return list;
        }
    }

    public static class HtmlReport
    {
        private static string N(double v, int d = 2)
        {
            if (double.IsPositiveInfinity(v)) return "∞";
            if (double.IsNaN(v)) return "—";
            return v.ToString("N" + d, CultureInfo.InvariantCulture);
        }
        private static string P(double v) { return N(v, 2) + "%"; }
        // 設定值：最多 d 位小數、不補零、有千分位（spec 6.3）
        private static string Tn(double v, int d = 2)
        {
            if (double.IsNaN(v)) return "—";
            return v.ToString("#,0" + (d > 0 ? "." + new string('#', d) : ""), CultureInfo.InvariantCulture);
        }
        private static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }
        private static string T(DateTime t) { return t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        private static string Pct(int a, int b) { return b > 0 ? " (" + P(100.0 * a / b) + ")" : ""; }

        public static string ModeText(TickMode m)
        {
            switch (m)
            {
                case TickMode.M1Ohlc: return "M1 OHLC 模擬報價";
                case TickMode.BarOhlc: return "本週期 OHLC 模擬報價";
                default: return "只用開盤價";
            }
        }

        public static string Build(BacktestResult r, bool includeLog)
        {
            var st = Stats.From(r);
            var s = r.Settings;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
            sb.Append("<title>回測報告 ").Append(H(r.StrategyName)).Append(' ').Append(H(r.Symbol)).Append("</title><style>");
            sb.Append("body{font-family:'Microsoft JhengHei','微軟正黑體',sans-serif;font-size:16px;margin:16px;color:#1f2933;background:#fff}");
            sb.Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:19px;margin:24px 0 8px;border-bottom:2px solid #d0d7de;padding-bottom:4px}");
            sb.Append("table{border-collapse:collapse;margin-bottom:8px}td,th{padding:5px 12px;border-bottom:1px solid #e5e7eb;text-align:left;white-space:nowrap}");
            sb.Append("th{background:#f3f4f6}.k{color:#57606a}.v{color:#1f2328}.pos{color:#1a7f37;font-weight:bold}.neg{color:#cf222e;font-weight:bold}");
            sb.Append(".note{background:#fff8c5;border:1px solid #d4a72c;padding:8px 12px;margin:6px 0}.err{background:#ffebe9;border:1px solid #cf222e;padding:8px 12px}");
            sb.Append(".num{text-align:right}.sub{color:#57606a}pre{font-size:14px;background:#f6f8fa;padding:8px}");
            sb.Append("tr:nth-child(even) td{background:#f6f8fa}th{font-weight:600;color:#57606a}td.wrap{white-space:normal;word-break:break-all;max-width:220px}table.full{width:100%;table-layout:fixed}table.full td{white-space:normal}");
            // 重點數字卡片（IE 引擎不用 flex，改用 inline-block）
            sb.Append(".kpi{display:inline-block;vertical-align:top;box-sizing:border-box;width:calc(20% - 10px);min-width:150px;margin:0 10px 10px 0;padding:10px 14px;border:1px solid #d0d7de;border-radius:6px;background:#fff}");
            sb.Append(".kpi .t{font-size:14px;color:#57606a}.kpi .n{font-size:26px;font-weight:bold;margin-top:2px}");
            // 免責說明（spec.md 13.5）：頁首小字灰框
            sb.Append(".disclaimer{font-size:13px;color:#57606a;background:#f6f8fa;border:1px solid #d0d7de;border-radius:6px;padding:6px 14px;margin:0 0 14px 0}.disclaimer p{margin:4px 0}.disclaimer ul{margin:4px 0;padding-left:20px}");
            sb.Append("</style></head><body>");
            sb.Append(Disclaimer.ReportHtml());

            sb.Append("<h1>").Append(H(r.StrategyName)).Append(" 回測報告</h1><div class=\"sub\">")
              .Append(H(r.Symbol)).Append("　").Append(H(Timeframes.Label(s.Timeframe))).Append("　")
              .Append(T(r.FirstBar)).Append(" 到 ").Append(T(r.LastBar)).Append("　")
              .Append(s.Mode == TickMode.M1Ohlc && r.BarsWithM1 == 0 && r.Bars > 0 ? ModeText(TickMode.BarOhlc) + "（沒有 M1 資料）" : ModeText(s.Mode))
              .Append("　耗時 ").Append(r.Elapsed.TotalSeconds < 0.1 ? "不到 0.1" : N(r.Elapsed.TotalSeconds, 1)).Append(" 秒</div>");

            if (!string.IsNullOrEmpty(r.Error)) sb.Append("<div class=\"err\">回測錯誤：").Append(H(r.Error)).Append("</div>");
            if (r.Cancelled) sb.Append("<div class=\"note\">回測被中止，以下只是中止前的結果。</div>");
            if (r.StrategyErrors > 0) sb.Append("<div class=\"note\">策略執行時發生 ").Append(r.StrategyErrors).Append(" 次錯誤，詳見日誌。</div>");
            foreach (string n in r.Notes) sb.Append("<div class=\"note\">").Append(H(n)).Append("</div>");
            if (st.Trades == 0 && string.IsNullOrEmpty(r.Error))
                sb.Append("<div class=\"note\">這次回測沒有任何交易。常見原因：策略條件在這段期間沒觸發、算出的手數小於最小手數、")
                  .Append("匯率或合約大小不對、策略參數關閉了下單。請看「日誌」分頁的策略訊息。")
                  .Append(OrderSwitchHint(r)).Append("</div>");

            // 重點數字
            sb.Append("<div style=\"margin-top:14px\">");
            Kpi(sb, "淨利（" + H(s.AccountCurrency) + "）", Money(st.NetProfit));
            Kpi(sb, "獲利因子", st.Trades == 0 ? "—" : "<span class=\"" + (st.ProfitFactor < 1 ? "neg" : st.ProfitFactor >= 1.5 ? "pos" : "") + "\">" + N(st.ProfitFactor) + "</span>");
            Kpi(sb, "淨值最大回撤", P(st.EquityDDPct));
            Kpi(sb, "交易筆數", st.Trades.ToString(CultureInfo.InvariantCulture));
            Kpi(sb, "勝率", st.Trades == 0 ? "—" : P(100.0 * st.Wins / st.Trades));
            sb.Append("</div>");

            // 摘要
            sb.Append("<h2>摘要</h2><table class=\"full\">").Append(FullCols);
            Row4(sb, "初始資金", N(st.InitialDeposit) + " " + H(s.AccountCurrency), "淨利", Money(st.NetProfit));
            Row4(sb, "總獲利", Money(st.GrossProfit), "總虧損", Money(st.GrossLoss));
            Row4(sb, "獲利因子", N(st.ProfitFactor), "期望收益（每筆）", Money(st.ExpectedPayoff));
            Row4(sb, "恢復因子", N(st.RecoveryFactor), "夏普比率（每筆）", N(st.Sharpe, 3));
            // 金額與百分比各自取最大值，可能來自不同時期，所以分開寫
            Row4(sb, "餘額最大回撤（金額）", N(st.BalanceDD), "淨值最大回撤（金額）", N(st.EquityDD));
            Row4(sb, "餘額最大回撤（相對 %）", P(st.BalanceDDPct), "淨值最大回撤（相對 %）", P(st.EquityDDPct));
            Row4(sb, "絕對回撤", N(st.AbsoluteDD), "手續費合計", N(st.TotalCommission));
            Row4(sb, "交易筆數", st.Trades.ToString(), "獲利筆數", st.Wins + Pct(st.Wins, st.Trades));
            Row4(sb, "多單（勝率）", st.Longs + Pct(st.LongWins, st.Longs), "空單（勝率）", st.Shorts + Pct(st.ShortWins, st.Shorts));
            Row4(sb, "最大單筆獲利", Money(st.LargestWin), "最大單筆虧損", Money(st.LargestLoss));
            Row4(sb, "平均獲利", Money(st.AvgWin), "平均虧損", Money(st.AvgLoss));
            Row4(sb, "最大連勝（金額）", st.MaxConsecWins + "（" + N(st.MaxConsecWinMoney) + "）", "最大連敗（金額）", st.MaxConsecLosses + "（" + N(st.MaxConsecLossMoney) + "）");
            string reasons = string.Join("、", st.ByReason.Select(kv => kv.Key + " " + kv.Value));
            Row4(sb, "出場原因", reasons.Length == 0 ? "—" : H(reasons), "拒單／送單", (r.Broker == null ? 0 : r.Broker.OrdersRejected) + " / " + (r.Broker == null ? 0 : r.Broker.OrdersSent));
            Row4(sb, "K 棒數（暖機）", r.Bars + "（" + r.WarmupBars + "）", "模擬報價數", r.Ticks.ToString("N0", CultureInfo.InvariantCulture));
            Row4(sb, "M1 資料覆蓋", s.Mode == TickMode.M1Ohlc && r.BarsWithM1 == 0 ? "<span style=\"color:#9a6700\">無（改用本週期 OHLC）</span>" : s.Mode == TickMode.M1Ohlc ? P(r.Bars > 0 ? 100.0 * r.BarsWithM1 / r.Bars : 0) + (r.BarsM1Incomplete > 0 ? "（另 " + r.BarsM1Incomplete + " 根 M1 不完整，改用本週期）" : "") : "未使用", "最大已用保證金", r.Broker == null ? "—" : N(r.Broker.MaxMarginUsed));
            sb.Append("</table>");

            // 資金曲線
            sb.Append("<h2>資金曲線</h2>").Append(Svg(r.Curve, s.InitialDeposit));

            // 設定與參數
            sb.Append("<h2>回測設定</h2><table class=\"full\">").Append(FullCols);
            string fx = string.IsNullOrEmpty(s.FxPair) || double.IsNaN(s.FxPairPrice)
                ? (s.QuoteCurrency == null || s.QuoteCurrency == s.AccountCurrency ? "不用換算" : Tn(s.FxRate, 6))
                : r.FxHistory != null
                ? s.FxPair + " 歷史價格（" + H(r.FxHistory) + "）；找不到時用 " + Tn(s.FxPairPrice, FxRates.PairDigits(s.FxPair))
                : s.FxPair + " " + Tn(s.FxPairPrice, FxRates.PairDigits(s.FxPair)) + "（" + H(s.FxSource ?? "手動填寫") + "）";
            Row4(sb, "點差", Tn(s.TickSize > 0 ? Math.Round(s.Spread / s.TickSize) : 0, 0) + " 跳動點（" + Tn(s.Spread, s.PriceDigits) + "）", "槓桿", "1:" + Tn(s.Leverage) + "（保證金 " + Tn(s.Leverage > 0 ? 100.0 / s.Leverage : 0) + "%）");
            Row4(sb, "合約大小", Tn(s.ContractSize, 0), "匯率", fx);
            Row4(sb, "最小手數／級距", Tn(s.MinLot) + " / " + Tn(s.LotStep), "單邊手續費", Tn(s.CommissionPerLot) + "／手＋" + Tn(s.CommissionPercent) + "%（最低 " + Tn(s.CommissionMin) + "）");
            Row4(sb, "最小跳動點", s.TickSize.ToString("0.##########", CultureInfo.InvariantCulture), "強制平倉水位", Tn(s.StopOutPercent, 0) + "%");
            Row4(sb, "隔夜利息", "不計（群益每日公告）", "", "");
            // 策略參數每列 3 組，縮短報告長度
            sb.Append("</table><h2>策略參數</h2><table class=\"full\">");
            var ps = r.Parameters.ToList();
            for (int i = 0; i < ps.Count; i += 3)
            {
                sb.Append("<tr>");
                for (int j = i; j < i + 3; j++)
                {
                    // 顯示中文名稱，程式名稱放滑鼠提示（和參數頁一致）
                    if (j < ps.Count) sb.Append("<td class=\"k\" title=\"").Append(H(ps[j].Key)).Append("\">").Append(H(ParamShortLabel(r, ps[j].Key))).Append("</td><td class=\"v\">").Append(H(OnOff(ps[j].Value))).Append("</td>");
                    else sb.Append("<td></td><td></td>");
                }
                sb.Append("</tr>");
            }
            if (ps.Count == 0) sb.Append("<tr><td>（沒有參數）</td></tr>");
            sb.Append("</table>");

            // 交易明細
            sb.Append("<h2>交易明細（").Append(r.Trades.Count).Append(" 筆）</h2><table><tr><th>#</th><th>部位</th><th>方向</th><th class=\"num\">手數</th><th>進場時間（UTC）</th><th class=\"num\">進場價</th><th>出場時間（UTC）</th><th class=\"num\">出場價</th><th class=\"num\" title=\"平倉當下的停損；有移動停損時可能和進場時不同\">停損（平倉時）</th><th class=\"num\" title=\"平倉當下的停利\">停利（平倉時）</th><th title=\"策略平倉 = 策略程式自己下的平倉（不是被停損停利打到）\">原因</th><th class=\"num\">損益</th><th class=\"num\">餘額</th><th>備註</th></tr>");
            int dg = s.PriceDigits;
            foreach (var x in r.Trades)
            {
                sb.Append("<tr><td>").Append(x.No).Append("</td><td>").Append(H(x.PositionId)).Append("</td><td>").Append(x.IsLong ? "買" : "賣")
                  .Append("</td><td class=\"num\">").Append(N(x.Volume, 2)).Append("</td><td>").Append(T(x.OpenTime))
                  .Append("</td><td class=\"num\">").Append(N(x.OpenPrice, dg)).Append("</td><td>").Append(T(x.CloseTime))
                  .Append("</td><td class=\"num\">").Append(N(x.ClosePrice, dg)).Append("</td><td class=\"num\">").Append(N(x.StopLoss, dg))
                  .Append("</td><td class=\"num\">").Append(N(x.TakeProfit, dg)).Append("</td><td>").Append(H(x.Reason))
                  .Append("</td><td class=\"num\">").Append(Money(x.Profit)).Append("</td><td class=\"num\">").Append(N(x.BalanceAfter))
                  .Append("</td><td title=\"").Append(H(x.Comment)).Append("\">").Append(H(Short(x.Comment, 16))).Append("</td></tr>");
            }
            sb.Append("</table>");

            if (includeLog)
            {
                sb.Append("<h2>日誌</h2><pre>");
                foreach (string l in r.Log.Take(20000)) sb.Append(H(l)).Append('\n');
                sb.Append("</pre>");
            }
            sb.Append("<div class=\"sub\">自製回測器 v").Append(TradeApi.CtaBacktestInfo.Version).Append("。成交、點差、手續費為模擬，實盤前請在模擬帳號確認。</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string Money(double v)
        {
            string cls = v > 0 ? "pos" : v < 0 ? "neg" : "";
            return "<span class=\"" + cls + "\">" + N(v) + "</span>";
        }

        // 0 筆交易時，指出哪個「下單開關」參數是關的（例 EnableLiveOrders、EnableOrders）
        private static string OrderSwitchHint(BacktestResult r)
        {
            var off = OrderSwitchesOff(r).Select(n => "「" + ParamShortLabel(r, n) + "」").ToList();
            return off.Count == 0 ? "" : "<br><b>這次 " + H(string.Join("、", off)) + "沒打勾，策略可能因此不送單：到「策略參數」頁打勾後再跑一次。</b>";
        }

        // 參數的中文名稱：說明去掉程式名稱與括號補充；沒有說明就用程式名稱
        public static string ParamShortLabel(BacktestResult r, string name)
        {
            string lab;
            if (r == null || !r.ParamLabels.TryGetValue(name, out lab) || string.IsNullOrEmpty(lab)) return name;
            lab = lab.Replace("（" + name + "）", "").Replace("(" + name + ")", "").Replace(name, "").Trim();
            int cut = lab.IndexOfAny(new[] { '（', '(' });
            lab = cut > 0 ? lab.Substring(0, cut).Trim() : lab;
            return lab.Length > 0 ? lab : name;
        }

        // 名稱像「下單開關」（含 Order、Live）且是關閉的參數；狀態列、K 線圖也用這個說明 0 筆交易的原因
        public static bool IsOrderSwitchName(string name)
        {
            // 不認 Trade：TradeLong／TradeShort 是做多做空的選擇，不是下單開關
            return name.IndexOf("Order", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Live", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static List<string> OrderSwitchesOff(BacktestResult r)
        {
            return r.Parameters.Where(kv => string.Equals(kv.Value, "False", StringComparison.OrdinalIgnoreCase) && IsOrderSwitchName(kv.Key))
                .Select(kv => kv.Key).ToList();
        }

        // 太長的備註截短，完整內容放在滑鼠提示
        private static string Short(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n) + "…"; }

        private const string FullCols = "<col style=\"width:20%\"><col style=\"width:30%\"><col style=\"width:20%\"><col style=\"width:30%\">";

        private static string OnOff(string v)
        {
            if (string.Equals(v, "True", StringComparison.OrdinalIgnoreCase)) return "開啟";
            if (string.Equals(v, "False", StringComparison.OrdinalIgnoreCase)) return "關閉";
            return v;
        }

        private static void Kpi(StringBuilder sb, string title, string valueHtml)
        {
            sb.Append("<div class=\"kpi\"><div class=\"t\">").Append(title).Append("</div><div class=\"n\">").Append(valueHtml).Append("</div></div>");
        }

        private static void Row4(StringBuilder sb, string k1, string v1, string k2, string v2)
        {
            sb.Append("<tr><td class=\"k\">").Append(k1).Append("</td><td class=\"v\">").Append(v1)
              .Append("</td><td class=\"k\">").Append(k2).Append("</td><td class=\"v\">").Append(v2).Append("</td></tr>");
        }

        // 資金曲線（內嵌 SVG，點太多時抽樣）
        private static string Svg(List<EquityPoint> curve, double initial)
        {
            const int W = 1000, Hh = 320, pad = 60;
            if (curve.Count < 2) return "<div class=\"sub\">資料不足，無法畫圖</div>";
            int step = Math.Max(1, curve.Count / 2000);
            var pts = new List<EquityPoint>();
            for (int i = 0; i < curve.Count; i += step) pts.Add(curve[i]);
            if (pts[pts.Count - 1] != curve[curve.Count - 1]) pts.Add(curve[curve.Count - 1]);
            double min = Math.Min(initial, pts.Min(p => Math.Min(p.Balance, p.Equity)));
            double max = Math.Max(initial, pts.Max(p => Math.Max(p.Balance, p.Equity)));
            int dec;
            List<double> ticks = ChartAxis.NiceTicks(ref min, ref max, 5, out dec);
            Func<int, double> X = i => pad + (double)i / (pts.Count - 1) * (W - pad - 10);
            Func<double, double> Y = v => 42 + (max - v) / (max - min) * (Hh - 72);   // 上方留給圖例
            var sb = new StringBuilder();
            sb.Append("<svg width=\"100%\" viewBox=\"0 0 ").Append(W).Append(' ').Append(Hh).Append("\" height=\"").Append(Hh).Append("\" style=\"max-width:").Append(W).Append("px;border:1px solid #d0d7de;background:#fff\">");   // 內嵌瀏覽器（IE 引擎）沒給高度會縮成 150px，所以寬度上限維持 1000
            foreach (double v in ticks)
            {
                double y = Y(v);
                sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0}\" y1=\"{1:F1}\" x2=\"{2}\" y2=\"{1:F1}\" stroke=\"#eee\"/>", pad, y, W - 10);
                sb.AppendFormat(CultureInfo.InvariantCulture, "<text x=\"4\" y=\"{0:F1}\" font-size=\"14\" fill=\"#57606a\">{1}</text>", y + 4, N(v, dec));
            }
            sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0}\" y1=\"{1:F1}\" x2=\"{2}\" y2=\"{1:F1}\" stroke=\"#8c959f\" stroke-width=\"1.5\" stroke-dasharray=\"6,4\"/>", pad, Y(initial), W - 10);
            Line(sb, pts, X, Y, p => p.Equity, "#2da44e");
            Line(sb, pts, X, Y, p => p.Balance, "#0969da");
            sb.AppendFormat(CultureInfo.InvariantCulture, "<text x=\"{0}\" y=\"{1}\" font-size=\"14\" fill=\"#57606a\">{2}</text>", pad, Hh - 8, T(pts[0].Time));
            sb.AppendFormat(CultureInfo.InvariantCulture, "<text x=\"{0}\" y=\"{1}\" font-size=\"14\" fill=\"#57606a\" text-anchor=\"end\">{2}</text>", W - 10, Hh - 8, T(pts[pts.Count - 1].Time));
            // 圖例畫在圖內左上角
            sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0}\" y=\"16\" width=\"12\" height=\"12\" fill=\"#0969da\"/><text x=\"{1}\" y=\"27\" font-size=\"14\" fill=\"#1f2933\">餘額</text>", pad + 10, pad + 28);
            sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0}\" y=\"16\" width=\"12\" height=\"12\" fill=\"#2da44e\"/><text x=\"{1}\" y=\"27\" font-size=\"14\" fill=\"#1f2933\">淨值（含未平倉）</text>", pad + 80, pad + 98);
            sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0}\" y1=\"22\" x2=\"{1}\" y2=\"22\" stroke=\"#8c959f\" stroke-width=\"1.5\" stroke-dasharray=\"6,4\"/><text x=\"{2}\" y=\"27\" font-size=\"14\" fill=\"#1f2933\">初始資金</text>", pad + 230, pad + 256, pad + 262);
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, List<EquityPoint> pts, Func<int, double> X, Func<double, double> Y, Func<EquityPoint, double> f, string color)
        {
            sb.Append("<polyline fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"1.5\" points=\"");
            for (int i = 0; i < pts.Count; i++)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", X(i), Y(f(pts[i])));
            sb.Append("\"/>");
        }
    }
}
