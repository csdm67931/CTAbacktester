using System;
using System.Collections.Generic;
using System.Globalization;
using TradeApi.Instruments;
using TradeApi.Trading;

namespace CtaBacktest
{
    // 一筆平倉紀錄（部分平倉也算一筆）
    [Serializable]
    public sealed class TradeRecord
    {
        public int No;
        public string PositionId;
        public bool IsLong;
        public double Volume;
        public DateTime OpenTime, CloseTime;
        public double OpenPrice, ClosePrice;
        public double StopLoss, TakeProfit;   // 平倉當下的設定，NaN = 沒設
        public double Commission;
        public double Profit;                 // 已扣手續費
        public double BalanceAfter;
        public string Reason;                 // 停損、停利、策略平倉、測試結束、強制平倉
        public string Comment;
        public double MaxFavorable, MaxAdverse;   // 持倉期間最大浮盈、最大浮虧（價格差）
    }

    [Serializable]
    public sealed class EquityPoint
    {
        public DateTime Time;
        public double Balance, Equity;
    }

    // 回測結束時的帳戶統計摘要：子程式傳回視窗程式用（spec.md 13.2），欄位名稱同 Broker
    [Serializable]
    public sealed class BrokerStats
    {
        public double Balance;
        public double PeakEquity, MaxEquityDrawdown, MaxEquityDrawdownPct, MinEquity;
        public double PeakBalance, MaxBalanceDrawdown, MaxBalanceDrawdownPct, MinBalance;
        public double MaxMarginUsed;
        public int OrdersSent, OrdersRejected, OpenPositionCount;
    }

    // 模擬帳戶與撮合（規格 spec.md 第 6 節）
    public sealed class Broker
    {
        private readonly Tester tester;
        private readonly TesterSettings s;
        internal Account Account;
        private readonly List<Position> positions = new List<Position>();
        private readonly List<Order> pending = new List<Order>();
        private readonly Dictionary<string, Order> allOrders = new Dictionary<string, Order>();
        private int orderSeq, positionSeq, tradeSeq;

        public double Balance { get; private set; }
        public double Bid { get; private set; }
        public double Ask { get; private set; }
        public DateTime Time { get; private set; }
        public bool StoppedOut { get; private set; }
        public readonly List<TradeRecord> Trades = new List<TradeRecord>();

        // 回撤統計（每個報價更新）
        public double PeakEquity, MaxEquityDrawdown, MaxEquityDrawdownPct, MinEquity;
        public double PeakBalance, MaxBalanceDrawdown, MaxBalanceDrawdownPct, MinBalance;
        public double MaxMarginUsed;
        public int OrdersSent, OrdersRejected;

        private double prevBid = double.NaN;
        private int tickNo;

        internal BrokerStats Snapshot()
        {
            return new BrokerStats
            {
                Balance = Balance, PeakEquity = PeakEquity, MaxEquityDrawdown = MaxEquityDrawdown, MaxEquityDrawdownPct = MaxEquityDrawdownPct, MinEquity = MinEquity,
                PeakBalance = PeakBalance, MaxBalanceDrawdown = MaxBalanceDrawdown, MaxBalanceDrawdownPct = MaxBalanceDrawdownPct, MinBalance = MinBalance,
                MaxMarginUsed = MaxMarginUsed, OrdersSent = OrdersSent, OrdersRejected = OrdersRejected, OpenPositionCount = OpenPositionCount
            };
        }

        internal Broker(Tester tester, TesterSettings settings)
        {
            this.tester = tester;
            s = settings;
            Balance = s.InitialDeposit;
            Bid = Ask = double.NaN;
            PeakEquity = PeakBalance = MinEquity = MinBalance = s.InitialDeposit;
            Account = new Account(this, "BACKTEST");
        }

        private Instrument Inst { get { return tester.Instrument; } }

        // ---------------- 價格與換算 ----------------
        private double Money(double priceDiff, double qty) { return priceDiff * qty * s.ContractSize * Fx(); }
        private double Margin(double price, double qty)
        {
            if (!(s.Leverage > 0)) return 0;
            double r = BaseRate();
            if (!double.IsNaN(r)) return qty * s.ContractSize * r / s.Leverage;
            return qty * s.ContractSize * price * Fx() / s.Leverage;
        }

        // 損益換算匯率（spec.md 6.2）：有歷史匯率用當下的，沒有（或當下找不到）用固定的 s.FxRate
        private PairRates profitRates;
        public DateTime FxFallbackFrom { get; private set; }   // 有歷史匯率但當下找不到、改用固定匯率的第一個與最後一個時間
        public DateTime FxFallbackTo { get; private set; }
        public int FxFallbacks { get; private set; }           // 改用固定匯率的次數（同一時間只算一次）

        internal void SetProfitRates(PairRates rates) { profitRates = rates; }

        internal double Fx()
        {
            if (profitRates == null) return s.FxRate;
            double r = profitRates.At(tester.Now);
            if (r > 0) return r;
            if (FxFallbacks == 0) FxFallbackFrom = tester.Now;
            if (FxFallbacks == 0 || FxFallbackTo != tester.Now) FxFallbacks++;
            FxFallbackTo = tester.Now;
            return s.FxRate;
        }

        // 交叉外匯保證金用的基礎貨幣歷史匯率（spec.md 第 6 節）；沒設定或當下之前沒有資料回傳 NaN
        private BarSeries marginSeries;
        private bool marginBaseFirst;
        public int MarginFallbacks { get; private set; }   // 交叉外匯但當下沒有匯率、退回舊公式的次數

        internal void SetMarginRate(BarSeries series, bool baseFirst) { marginSeries = series; marginBaseFirst = baseFirst; }

        internal double BaseRate()
        {
            if (marginSeries == null) return double.NaN;
            int i = marginSeries.IndexAtOrBefore(tester.Now);
            double p = i >= 0 ? marginSeries.Bars[i].Open : double.NaN;
            if (!(p > 0)) { MarginFallbacks++; return double.NaN; }
            return marginBaseFirst ? p : 1.0 / p;
        }

        // 單邊手續費 = 每手固定 + 名目金額 x 費率，不足最低收費時收最低（CTA 官網：二者取高）
        private double Fee(double price, double qty)
        {
            double fee = s.CommissionPerLot * qty + qty * s.ContractSize * price * Fx() * s.CommissionPercent / 100.0;
            if (s.CommissionMin > 0 && qty > 0) fee = Math.Max(fee, s.CommissionMin);
            return fee;
        }

        public double FloatingPnL(Position p)
        {
            if (!positions.Contains(p) || double.IsNaN(Bid)) return 0;
            return p.Side == PositionSide.Long ? Money(Bid - p.OpenPrice, p.Quantity) : Money(p.OpenPrice - Ask, p.Quantity);
        }

        public double Equity
        {
            get { double e = Balance; foreach (Position p in positions) e += FloatingPnL(p); return e; }
        }

        public double UsedMargin
        {
            get { double m = 0; foreach (Position p in positions) m += Margin(p.OpenPrice, p.Quantity); return m; }
        }

        public int OpenPositionCount { get { return positions.Count; } }

        internal Dictionary<string, string> AccountDetails()
        {
            Func<double, string> f = v => v.ToString("F2", CultureInfo.InvariantCulture) + " " + s.AccountCurrency;
            double eq = Equity, used = UsedMargin, free = eq - used;
            return new Dictionary<string, string>
            {
                { "Balance", f(Balance) }, { "帳號餘額", f(Balance) },
                { "Equity", f(eq) }, { "淨值", f(eq) },
                { "Margin", f(used) }, { "已用保證金", f(used) },
                { "Free Margin", f(free) }, { "可用保證金", f(free) }, { "Available Funds", f(free) },
                { "Currency", s.AccountCurrency }, { "幣別", s.AccountCurrency }
            };
        }

        // ---------------- 下單 ----------------
        private TradeResult Reject(string msg)
        {
            OrdersRejected++;
            tester.Log("［拒單］" + msg);
            return new TradeResult { HasError = true, Message = msg };
        }

        private bool ValidLot(double qty, out string why)
        {
            why = null;
            if (!(qty > 0)) { why = "手數必須大於 0"; return false; }
            if (qty < s.MinLot - 1e-9) { why = "手數 " + qty + " 小於最小手數 " + s.MinLot; return false; }
            if (s.MaxLot > 0 && qty > s.MaxLot + 1e-9) { why = "手數 " + qty + " 大於最大手數 " + s.MaxLot; return false; }
            if (s.LotStep > 0)
            {
                double steps = (qty - s.MinLot) / s.LotStep;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-6) { why = "手數 " + qty + " 不是手數級距 " + s.LotStep + " 的倍數"; return false; }
            }
            return true;
        }

        // 委託上的停損停利轉成絕對價格（Ticks：以成交價加減跳動點；Percent 不支援）
        private bool ResolveSlTp(SLTPPriceType kind, bool isLong, double basePrice, double sl, double tp, out double asl, out double atp, out string why)
        {
            asl = double.NaN; atp = double.NaN; why = null;
            bool hasSl = !double.IsNaN(sl) && sl != 0, hasTp = !double.IsNaN(tp) && tp != 0;
            if (kind == SLTPPriceType.Percent && (hasSl || hasTp)) { why = "停損停利的 Percent 模式在回測 v0.1 不支援"; return false; }
            if (kind == SLTPPriceType.Ticks)
            {
                double t = Inst.MinimalTickSize;
                if (hasSl) asl = isLong ? basePrice - sl * t : basePrice + sl * t;
                if (hasTp) atp = isLong ? basePrice + tp * t : basePrice - tp * t;
            }
            else
            {
                if (hasSl) asl = sl;
                if (hasTp) atp = tp;
            }
            return true;
        }

        // 停損停利是否在正確的一邊：多單看 Bid、空單看 Ask
        private string CheckSides(bool isLong, double sl, double tp)
        {
            double mark = isLong ? Bid : Ask;
            if (!double.IsNaN(sl) && (isLong ? sl >= mark : sl <= mark)) return "停損價 " + Fmt(sl) + " 在目前價格 " + Fmt(mark) + " 的錯誤一邊";
            if (!double.IsNaN(tp) && (isLong ? tp <= mark : tp >= mark)) return "停利價 " + Fmt(tp) + " 在目前價格 " + Fmt(mark) + " 的錯誤一邊";
            return null;
        }

        internal TradeResult Send(OrderRequest r)
        {
            OrdersSent++;
            if (r == null) return Reject("委託是 null");
            if (StoppedOut) return Reject("帳戶已強制平倉，回測停止交易");
            if (double.IsNaN(Bid)) return Reject("還沒有報價");
            string why;
            if (!ValidLot(r.Quantity, out why)) return Reject(why);

            bool isBuy = r.Side == OrderSide.Buy;
            switch (r.Type)
            {
                case OrderType.Market:
                    return OpenMarket(r, isBuy ? Ask : Bid);
                case OrderType.Limit:
                case OrderType.Stop:
                {
                    double level = r.Type == OrderType.Limit ? r.Price : (double.IsNaN(r.StopPrice) ? r.Price : r.StopPrice);
                    if (double.IsNaN(level) || level <= 0) return Reject("限價／停損單沒有價格");
                    double mark = isBuy ? Ask : Bid;
                    bool marketable = r.Type == OrderType.Limit ? (isBuy ? mark <= level : mark >= level) : (isBuy ? mark >= level : mark <= level);
                    if (marketable) return OpenMarket(r, mark);   // 已經到價：當市價單立即成交
                    var o = NewOrder(r, level);
                    o.Status = OrderStatus.Accepted;
                    pending.Add(o);
                    if (o.TIF == TimeInForce.Day) tester.Note("策略送了日內（Day）掛單；回測不會在收盤自動取消，會一直掛到成交或策略取消。");
                    tester.Strategy.ordersManager.RaisePlace(o);
                    tester.Log(string.Format(CultureInfo.InvariantCulture, "［委託］{0} {1} {2} 手 @ {3}（{4}）", o.ID, isBuy ? "買" : "賣", r.Quantity, Fmt(level), r.Type));
                    return new TradeResult { Id = o.ID, Message = "已掛單" };
                }
                default:
                    return Reject("委託類型 " + r.Type + " 在回測 v0.1 不支援（只支援 Market、Limit、Stop）");
            }
        }

        private Order NewOrder(OrderRequest r, double price)
        {
            var o = new Order
            {
                ID = "O" + (++orderSeq).ToString(CultureInfo.InvariantCulture),
                Account = Account,
                Instrument = Inst,
                Comment = r.Comment,
                Name = r.Name,
                Side = r.Side,
                Type = r.Type,
                Quantity = r.Quantity,
                Price = price,
                StopPrice = r.StopPrice,
                TIF = r.TIF,
                ProductType = r.ProductType,
                ExpirationTimeUtc = r.ExpirationTimeUtc,
                PlacedTimeUtc = Time,
                LastUpdateTimeUtc = Time
            };
            o.StopLossPrice = double.IsNaN(r.StopLossPrice) || r.StopLossPrice == 0 ? (double?)null : r.StopLossPrice;
            o.TakeProfitPrice = double.IsNaN(r.TakeProfitPrice) || r.TakeProfitPrice == 0 ? (double?)null : r.TakeProfitPrice;
            o.Request = r;
            allOrders[o.ID] = o;
            return o;
        }

        // 立即以 price 成交開新部位（對沖模式：每筆成交一個部位）
        private TradeResult OpenMarket(OrderRequest r, double price)
        {
            bool isLong = r.Side == OrderSide.Buy;
            double sl, tp;
            string why;
            if (!ResolveSlTp(r.SLTPPriceType, isLong, price, r.StopLossPrice, r.TakeProfitPrice, out sl, out tp, out why)) return Reject(why);
            why = CheckSides(isLong, sl, tp);
            if (why != null) return Reject(why);
            double need = Margin(price, r.Quantity);
            double free = Equity - UsedMargin;
            if (need > free + 1e-9) return Reject(string.Format(CultureInfo.InvariantCulture, "保證金不足：需要 {0:F2}，可用 {1:F2}", need, free));

            var o = NewOrder(r, price);
            o.Status = OrderStatus.Filled;
            o.FilledQuantity = r.Quantity;
            o.AverageFilledPrice = price;
            return Fill(o, price, sl, tp);
        }

        private TradeResult Fill(Order o, double price, double sl, double tp)
        {
            bool isLong = o.Side == OrderSide.Buy;
            double comm = Fee(price, o.Quantity);
            Balance -= comm;
            var p = new Position
            {
                ID = "P" + (++positionSeq).ToString(CultureInfo.InvariantCulture),
                OpenOrderID = o.ID,
                Account = Account,
                Instrument = Inst,
                Name = !string.IsNullOrEmpty(o.Name) ? o.Name : o.Comment,
                Comment = o.Comment,
                Side = isLong ? PositionSide.Long : PositionSide.Short,
                Quantity = o.Quantity,
                OpenPrice = price,
                OpenTimeUtc = Time,
                ProductType = o.ProductType,
                Broker = this,
                StopLoss = sl,
                TakeProfit = tp,
                OpenTick = tickNo
            };
            p.OpenCommission = comm;
            positions.Add(p);
            tester.Log(string.Format(CultureInfo.InvariantCulture, "［成交］{0} {1} {2} 手 @ {3}，停損 {4}，停利 {5}，部位 {6}",
                o.ID, isLong ? "買" : "賣", o.Quantity, Fmt(price), Fmt(sl), Fmt(tp), p.ID));
            UpdateMarginStats();
            tester.Strategy.ordersManager.RaiseFill(o);
            tester.Strategy.positionsManager.RaiseOpen(p);
            return new TradeResult { Id = o.ID, Message = "已成交" };
        }

        internal TradeResult Replace(ReplaceOrderRequest r)
        {
            if (r == null || r.Order == null) return Reject("改單沒有指定委託");
            Order o = r.Order;
            if (!pending.Contains(o)) return Reject("委託 " + o.ID + " 不在工作中，不能改");
            if (r.Quantity.HasValue)
            {
                string why;
                if (!ValidLot(r.Quantity.Value, out why)) return Reject(why);
                o.Quantity = r.Quantity.Value;
            }
            if (r.Price.HasValue) o.Price = r.Price.Value;
            if (r.StopLossPrice.HasValue) o.StopLossPrice = r.StopLossPrice.Value == 0 ? (double?)null : r.StopLossPrice.Value;
            if (r.TakeProfitPrice.HasValue) o.TakeProfitPrice = r.TakeProfitPrice.Value == 0 ? (double?)null : r.TakeProfitPrice.Value;
            if (r.Comment != null) o.Comment = r.Comment;
            o.LastUpdateTimeUtc = Time;
            tester.Strategy.ordersManager.RaiseUpdate(o);
            return new TradeResult { Id = o.ID, Message = "已改單" };
        }

        internal bool Cancel(Order o)
        {
            if (o == null) return false;
            // 停損停利單：取消等於移除部位上的停損或停利
            if (o.PositionId != null)
            {
                Position p = positions.Find(x => x.ID == o.PositionId);
                if (p == null) return false;
                return o.IsStopLossOrder ? SetStopLoss(p, double.NaN) : SetTakeProfit(p, double.NaN);
            }
            if (!pending.Remove(o)) return false;
            o.Status = OrderStatus.Canceled;
            o.LastUpdateTimeUtc = Time;
            tester.Log("［撤單］" + o.ID);
            tester.Strategy.ordersManager.RaiseCancel(o);
            return true;
        }

        internal List<Order> GetOrders(bool includeSltp, Predicate<Order> predicate)
        {
            var list = new List<Order>(pending);
            if (includeSltp) foreach (Position p in positions) list.AddRange(SltpOrders(p));
            return predicate == null ? list : list.FindAll(predicate);
        }

        internal List<Order> SltpOrders(Position p)
        {
            var list = new List<Order>();
            if (!positions.Contains(p)) return list;
            bool isLong = p.Side == PositionSide.Long;
            if (!double.IsNaN(p.StopLoss)) list.Add(SltpOrder(p, true, isLong));
            if (!double.IsNaN(p.TakeProfit)) list.Add(SltpOrder(p, false, isLong));
            return list;
        }

        private Order SltpOrder(Position p, bool isSl, bool isLong)
        {
            return new Order
            {
                ID = p.ID + (isSl ? "-SL" : "-TP"),
                Account = Account,
                Instrument = Inst,
                Side = isLong ? OrderSide.Sell : OrderSide.Buy,
                Type = isSl ? OrderType.Stop : OrderType.Limit,
                Quantity = p.Quantity,
                Price = isSl ? p.StopLoss : p.TakeProfit,
                Status = OrderStatus.Accepted,
                IsStopLossOrder = isSl,
                IsTakeProfitOrder = !isSl,
                PlacedTimeUtc = p.OpenTimeUtc,
                LastUpdateTimeUtc = Time,
                PositionId = p.ID,
                Comment = p.Comment,
                Name = p.Name
            };
        }

        internal List<Position> GetPositions(Predicate<Position> predicate)
        {
            return predicate == null ? new List<Position>(positions) : positions.FindAll(predicate);
        }

        internal bool SetStopLoss(Position p, double price)
        {
            if (!positions.Contains(p)) return false;
            if (price == 0) price = double.NaN;
            string why = CheckSides(p.Side == PositionSide.Long, price, double.NaN);
            if (why != null) { tester.Log("［改停損失敗］" + why); return false; }
            p.StopLoss = price;
            tester.Strategy.positionsManager.RaiseUpdate(p);
            return true;
        }

        internal bool SetTakeProfit(Position p, double price)
        {
            if (!positions.Contains(p)) return false;
            if (price == 0) price = double.NaN;
            string why = CheckSides(p.Side == PositionSide.Long, double.NaN, price);
            if (why != null) { tester.Log("［改停利失敗］" + why); return false; }
            p.TakeProfit = price;
            tester.Strategy.positionsManager.RaiseUpdate(p);
            return true;
        }

        // 用目前報價平倉（多單用 Bid、空單用 Ask）
        internal bool Close(Position p, double? quantity, string reason)
        {
            if (p == null || !positions.Contains(p)) return false;
            double px = p.Side == PositionSide.Long ? Bid : Ask;
            return CloseAt(p, quantity, px, reason);
        }

        private bool CloseAt(Position p, double? quantity, double price, string reason)
        {
            // 同一報價內，策略可能在回呼裡已平掉這個部位
            if (!positions.Contains(p) || p.Quantity <= 1e-9) return false;
            double qty = quantity.HasValue && quantity.Value > 0 ? Math.Min(quantity.Value, p.Quantity) : p.Quantity;
            bool isLong = p.Side == PositionSide.Long;
            double gross = isLong ? Money(price - p.OpenPrice, qty) : Money(p.OpenPrice - price, qty);
            double comm = Fee(price, qty);
            double openCommShare = p.Quantity > 0 ? p.OpenCommission * qty / p.Quantity : 0;
            Balance += gross - comm;
            var t = new TradeRecord
            {
                No = ++tradeSeq,
                PositionId = p.ID,
                IsLong = isLong,
                Volume = qty,
                OpenTime = p.OpenTimeUtc,
                OpenPrice = p.OpenPrice,
                CloseTime = Time,
                ClosePrice = price,
                StopLoss = p.StopLoss,
                TakeProfit = p.TakeProfit,
                Commission = comm + openCommShare,
                Profit = gross - comm - openCommShare,
                BalanceAfter = Balance,
                Reason = reason,
                Comment = p.Comment,
                MaxFavorable = p.MaxFavorable,
                MaxAdverse = p.MaxAdverse
            };
            Trades.Add(t);
            p.OpenCommission -= openCommShare;
            p.Quantity -= qty;
            bool full = p.Quantity <= 1e-9;
            if (full) positions.Remove(p);
            tester.Log(string.Format(CultureInfo.InvariantCulture, "［平倉］{0} {1} 手 @ {2}，{3}，損益 {4:F2}，餘額 {5:F2}",
                p.ID, qty, Fmt(price), reason, t.Profit, Balance));
            UpdateBalanceStats();
            if (full) tester.Strategy.positionsManager.RaiseClose(p);
            else tester.Strategy.positionsManager.RaiseUpdate(p);
            return true;
        }

        internal void CloseAll(string reason)
        {
            foreach (Position p in new List<Position>(positions)) Close(p, null, reason);
        }

        // ---------------- 報價驅動 ----------------
        // gap = 這個報價和上一個之間是跳空（新 K 棒的第一個報價）：到價的單用報價成交，不用委託價
        internal void OnTick(DateTime time, double bid, bool gap)
        {
            tickNo++;
            Time = time;
            double pb = prevBid;
            Bid = bid;
            Ask = bid + s.Spread;
            prevBid = bid;
            bool jump = gap || double.IsNaN(pb);

            // 1. 掛單到價
            if (pending.Count > 0)
            {
                foreach (Order o in new List<Order>(pending))
                {
                    // GTD 掛單到期就取消
                    if (o.TIF == TimeInForce.GTD && o.ExpirationTimeUtc > DateTime.MinValue && Time >= o.ExpirationTimeUtc)
                    {
                        pending.Remove(o);
                        o.Status = OrderStatus.Canceled;
                        o.LastUpdateTimeUtc = Time;
                        tester.Log("［掛單到期取消］" + o.ID);
                        tester.Strategy.ordersManager.RaiseCancel(o);
                        continue;
                    }
                    bool isBuy = o.Side == OrderSide.Buy;
                    double mark = isBuy ? Ask : Bid;
                    bool hit = o.Type == OrderType.Limit ? (isBuy ? mark <= o.Price : mark >= o.Price)
                                                         : (isBuy ? mark >= o.Price : mark <= o.Price);
                    if (!hit) continue;
                    double fillPrice = jump ? mark : o.Price;
                    pending.Remove(o);
                    var r = o.Request;
                    double sl, tp;
                    string why;
                    bool ok = ResolveSlTp(r.SLTPPriceType, isBuy, fillPrice, o.StopLossPrice ?? double.NaN, o.TakeProfitPrice ?? double.NaN, out sl, out tp, out why);
                    if (ok) why = CheckSides(isBuy, sl, tp);
                    double need = Margin(fillPrice, o.Quantity);
                    if (ok && why == null && need > Equity - UsedMargin + 1e-9) why = "保證金不足";
                    if (!ok || why != null)
                    {
                        o.Status = OrderStatus.Refused;
                        OrdersRejected++;
                        tester.Log("［掛單到價但拒絕］" + o.ID + "：" + why);
                        tester.Strategy.ordersManager.RaiseCancel(o);
                        continue;
                    }
                    o.Status = OrderStatus.Filled;
                    o.FilledQuantity = o.Quantity;
                    o.AverageFilledPrice = fillPrice;
                    o.LastUpdateTimeUtc = Time;
                    Fill(o, fillPrice, sl, tp);
                }
            }

            // 2. 部位停損停利（同一個報價兩個都到時算停損）
            foreach (Position p in new List<Position>(positions))
            {
                bool isLong = p.Side == PositionSide.Long;
                double mark = isLong ? Bid : Ask;
                double move = isLong ? Bid - p.OpenPrice : p.OpenPrice - Ask;
                if (move > p.MaxFavorable) p.MaxFavorable = move;
                if (-move > p.MaxAdverse) p.MaxAdverse = -move;
                if (p.OpenTick == tickNo) continue;   // 這個報價才成交的部位，下一個報價才檢查
                bool slHit = !double.IsNaN(p.StopLoss) && (isLong ? mark <= p.StopLoss : mark >= p.StopLoss);
                bool tpHit = !double.IsNaN(p.TakeProfit) && (isLong ? mark >= p.TakeProfit : mark <= p.TakeProfit);
                if (slHit) CloseAt(p, null, jump ? mark : p.StopLoss, "停損");
                else if (tpHit) CloseAt(p, null, jump ? mark : p.TakeProfit, "停利");
            }

            // 3. 淨值、回撤、強制平倉
            UpdateEquityStats();
        }

        private void UpdateEquityStats()
        {
            double eq = Equity;
            if (eq > PeakEquity) PeakEquity = eq;
            if (eq < MinEquity) MinEquity = eq;
            double dd = PeakEquity - eq;
            if (dd > MaxEquityDrawdown) MaxEquityDrawdown = dd;
            if (PeakEquity > 0 && dd / PeakEquity > MaxEquityDrawdownPct) MaxEquityDrawdownPct = dd / PeakEquity;
            UpdateMarginStats();

            double used = UsedMargin;
            if (positions.Count > 0 && (eq <= 0 || (s.StopOutPercent > 0 && used > 0 && eq / used * 100 <= s.StopOutPercent)))
            {
                tester.Log(string.Format(CultureInfo.InvariantCulture, "［強制平倉］淨值 {0:F2}，已用保證金 {1:F2}，保證金水位低於 {2}%", eq, used, s.StopOutPercent));
                CloseAll("強制平倉");
                if (Balance <= 0) StoppedOut = true;
            }
        }

        private void UpdateBalanceStats()
        {
            if (Balance > PeakBalance) PeakBalance = Balance;
            if (Balance < MinBalance) MinBalance = Balance;
            double dd = PeakBalance - Balance;
            if (dd > MaxBalanceDrawdown) MaxBalanceDrawdown = dd;
            if (PeakBalance > 0 && dd / PeakBalance > MaxBalanceDrawdownPct) MaxBalanceDrawdownPct = dd / PeakBalance;
        }

        private void UpdateMarginStats()
        {
            double m = UsedMargin;
            if (m > MaxMarginUsed) MaxMarginUsed = m;
        }

        private string Fmt(double v)
        {
            if (double.IsNaN(v)) return "無";
            return v.ToString("F" + s.PriceDigits, CultureInfo.InvariantCulture);
        }
    }
}
