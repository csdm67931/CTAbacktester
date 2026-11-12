using System;
using System.Collections.Generic;
using CtaBacktest;
using TradeApi.Instruments;

// 模擬交易王 TradeApi.Trading（列舉值照真平台）。實際撮合在 Engine\Broker.cs。

namespace TradeApi.Trading
{
    public enum OrderType { Limit = 10030, Market = 10031, StopLimit = 10033, TrailingStop = 10034, Manual = 10070, Stop = 10073, OCO = 10076 }
    public enum OrderSide { Buy = 10000, Sell = 10001 }
    public enum PositionSide { Long = 10000, Short = 10001 }
    public enum TimeInForce { GTC = 10008, IOC = 10010, Day = 10011, FOK = 11000, GTD = 11003, GTS = 11009 }
    public enum OrderStatus
    {
        PendingNew = 1, PendingExecution = 2, PendingCancel = 3, PendingReplaceNotActive = 5, New = 10, Accepted = 11,
        PartiallyFilled = 30, Filled = 31, Canceled = 40, Refused = 41, WaitingMarket = 50, WaitingCancellation = 51,
        OffMarket = 60, Unplaced = 70
    }
    public enum ProductType { General = 0, Intraday = 1, Delivery = 2 }
    public enum SLTPPriceType { Absolute = 0, Ticks = 1, Percent = 2 }
    public enum AccountStatus { Active = 0, Closed = 1, Suspend = 2, TradingDisabledByRiskRules = 3, FinraDayTraderPatternStatus = 4, LiquidationOnlyStatus = 5 }

    public sealed class OrderRequest
    {
        public OrderRequest(OrderType orderType, Instrument instrument, Account account, OrderSide side, double quantity,
                            double? price = null, double? stopPrice = null)
        {
            Type = orderType;
            Instrument = instrument;
            Account = account;
            Side = side;
            Quantity = quantity;
            Price = price ?? double.NaN;
            StopPrice = stopPrice ?? double.NaN;
            StopLossPrice = double.NaN;
            TakeProfitPrice = double.NaN;
            StopLossLimitPrice = double.NaN;
            TrailingStopOffset = double.NaN;
            TIF = TimeInForce.GTC;
        }
        public Account Account { get; set; }
        public Order BoundTo { get; set; }
        public string Comment { get; set; }
        public DateTime ExpirationTimeUtc { get; set; }
        public Instrument Instrument { get; set; }
        public short? Leverage { get; set; }
        public string Name { get; set; }
        public double Price { get; set; }
        public ProductType ProductType { get; set; }
        public double Quantity { get; set; }
        public OrderSide Side { get; set; }
        public SLTPPriceType SLTPPriceType { get; set; }
        public double StopLossLimitPrice { get; set; }
        public double StopLossPrice { get; set; }
        public double StopPrice { get; set; }
        public double TakeProfitPrice { get; set; }
        public TimeInForce TIF { get; set; }
        public double TrailingStopOffset { get; set; }
        public OrderType Type { get; set; }
    }

    public sealed class ReplaceOrderRequest
    {
        public ReplaceOrderRequest(Order order) { Order = order; }
        public ReplaceOrderRequest(Order order, double price) { Order = order; Price = price; }
        public ReplaceOrderRequest(Order order, double price, double amount) { Order = order; Price = price; Quantity = amount; }
        public Order BoundTo { get; set; }
        public string Comment { get; set; }
        public DateTime? ExpirationTime { get; set; }
        public Order Order { get; private set; }
        public double? Price { get; set; }
        public double? Quantity { get; set; }
        public SLTPPriceType? SLTPPriceType { get; set; }
        public double? StopLossLimitPrice { get; set; }
        public double? StopLossPrice { get; set; }
        public double? StopPrice { get; set; }
        public double? TakeProfitPrice { get; set; }
        public TimeInForce? TIF { get; set; }
        public double? TrailingStopOffset { get; set; }
    }

    public sealed class TradeResult
    {
        public TradeResult() { }
        public bool HasError { get; internal set; }
        public string Id { get; internal set; }
        public string Message { get; internal set; }
    }

    public sealed class Order
    {
        internal Order() { }
        public Account Account { get; internal set; }
        public double AverageFilledPrice { get; internal set; }
        public Order BoundTo { get; internal set; }
        public string BoundToId { get { return BoundTo == null ? null : BoundTo.ID; } }
        public string Comment { get; internal set; }
        public DateTime ExpirationTimeUtc { get; internal set; }
        public double FilledQuantity { get; internal set; }
        public string ID { get; internal set; }
        public Instrument Instrument { get; internal set; }
        public bool IsActive { get { return Status == OrderStatus.Accepted || Status == OrderStatus.New || Status == OrderStatus.PartiallyFilled; } }
        public bool IsAutoTrade { get { return true; } }
        public bool IsStopLossOrder { get; internal set; }
        public bool IsTakeProfitOrder { get; internal set; }
        public DateTime LastUpdateTimeUtc { get; internal set; }
        public string Name { get; internal set; }
        public DateTime PlacedTimeUtc { get; internal set; }
        public double Price { get; internal set; }
        public ProductType ProductType { get; internal set; }
        public double Quantity { get; internal set; }
        public OrderSide Side { get; internal set; }
        public SLTPPriceType SLTPPriceType { get { return SLTPPriceType.Absolute; } }
        public OrderStatus Status { get; internal set; }
        public double? StopLossLimitPrice { get { return null; } }
        public double? StopLossPrice { get; internal set; }
        public double StopPrice { get; internal set; }
        public double? TakeProfitPrice { get; internal set; }
        public TimeInForce TIF { get; internal set; }
        public double? TrailingStopOffset { get { return null; } }
        public double? TralingStopPrice { get { return null; } }
        public OrderType Type { get; internal set; }

        // 回測內部用
        internal string PositionId;   // 停損停利單所屬部位
        internal OrderRequest Request;

        // 真平台有這兩個運算子，EvoCode 編好的 DLL 會直接呼叫（spec.md 13.1）；一張單只有一個物件，比較是否同一個
        public static bool operator ==(Order a, Order b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Order a, Order b) { return !ReferenceEquals(a, b); }
        public override bool Equals(object obj) { return ReferenceEquals(this, obj); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
    }

    public sealed class Position
    {
        internal Position() { }
        public Account Account { get; internal set; }
        public string ID { get; internal set; }
        public Instrument Instrument { get; internal set; }
        public string Name { get; internal set; }
        public string OpenOrderID { get; internal set; }
        public double OpenPrice { get; internal set; }
        public DateTime OpenTimeUtc { get; internal set; }
        public ProductType ProductType { get; internal set; }
        public double Quantity { get; internal set; }
        public PositionSide Side { get; internal set; }
        public string SuperPositionId { get { return null; } }

        // 浮動損益（帳戶幣別）；回測沒有分毛利淨利，兩個相同（手續費在平倉時扣）
        public double GrossPnL { get { return Broker == null ? 0 : Broker.FloatingPnL(this); } }
        public double NetPnL { get { return GrossPnL; } }

        public List<Order> SLTPOrders { get { return Broker == null ? new List<Order>() : Broker.SltpOrders(this); } }

        public void ChangeProductType(ProductType productType, double amount) { ProductType = productType; }

        // 回測內部用
        internal Broker Broker;
        internal double StopLoss = double.NaN;
        internal double TakeProfit = double.NaN;
        internal string Comment;
        internal double MaxFavorable, MaxAdverse;   // 持倉期間最大浮盈、最大浮虧（價格差）
        internal int OpenTick;
        internal double OpenCommission;   // 開倉手續費，平倉時依手數比例算進該筆

        // 同 Order（spec.md 13.1）
        public static bool operator ==(Position a, Position b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Position a, Position b) { return !ReferenceEquals(a, b); }
        public override bool Equals(object obj) { return ReferenceEquals(this, obj); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
    }

    public sealed class Account
    {
        private readonly Broker broker;
        internal Account(Broker broker, string id) { this.broker = broker; Id = id; Name = id; }

        // 同 Order（spec.md 13.1）
        public static bool operator ==(Account a, Account b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Account a, Account b) { return !ReferenceEquals(a, b); }
        public override bool Equals(object obj) { return ReferenceEquals(this, obj); }
        public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this); }
        public string Id { get; private set; }
        public string Name { get; private set; }
        public AccountStatus Status { get { return AccountStatus.Active; } }

        // 欄位名稱照交易王（中文與英文都有），風控大師的讀法可以直接用
        public Dictionary<string, string> GetAccountDetails(string assetName = null)
        {
            return broker.AccountDetails();
        }

        public override string ToString() { return Name; }
    }

    public sealed class AccountManager
    {
        private readonly Runtime.Script.CSharpScript script;
        internal AccountManager(Runtime.Script.CSharpScript script) { this.script = script; }
        public Account Current { get { return script.RequireTester().Account; } }
        public List<Account> GetAccounts() { return new List<Account> { Current }; }
        public List<Account> GetAccounts(Predicate<Account> predicate)
        {
            var all = GetAccounts();
            return predicate == null ? all : all.FindAll(predicate);
        }
    }

    public sealed class OrdersManager
    {
        private readonly Runtime.Script.CSharpScript script;
        internal OrdersManager(Runtime.Script.CSharpScript script) { this.script = script; }
        private Broker B { get { return script.RequireTester().Broker; } }

        public event Action<Order> OnCancel;
        public event Action<Order> OnFill;
        public event Action<Order> OnPlace;
        public event Action<Order> OnUpdate;

        internal void RaiseCancel(Order o) { var h = OnCancel; if (h != null) h(o); }
        internal void RaiseFill(Order o) { var h = OnFill; if (h != null) h(o); }
        internal void RaisePlace(Order o) { var h = OnPlace; if (h != null) h(o); }
        internal void RaiseUpdate(Order o) { var h = OnUpdate; if (h != null) h(o); }

        public TradeResult Send(OrderRequest request) { return B.Send(request); }

        public bool SendAsync(OrderRequest request, Action<TradeResult> callback)
        {
            TradeResult r = B.Send(request);
            if (callback != null) callback(r);
            return !r.HasError;
        }

        public TradeResult Update(ReplaceOrderRequest request) { return B.Replace(request); }

        public bool UpdateAsync(ReplaceOrderRequest request, Action<TradeResult> callback)
        {
            TradeResult r = B.Replace(request);
            if (callback != null) callback(r);
            return !r.HasError;
        }

        public bool Cancel(Order order) { return B.Cancel(order); }

        public bool Cancel(List<Order> orders)
        {
            bool ok = true;
            if (orders != null) foreach (Order o in orders) ok &= B.Cancel(o);
            return ok;
        }

        public bool CancelAsync(Order order, Action<bool> callback)
        {
            bool ok = B.Cancel(order);
            if (callback != null) callback(ok);
            return ok;
        }

        public List<Order> GetOrders(bool SLTPinclude) { return B.GetOrders(SLTPinclude, null); }
        public List<Order> GetOrders(bool SLTPinclude, Predicate<Order> predicate) { return B.GetOrders(SLTPinclude, predicate); }

        public int Count { get { return B.GetOrders(false, null).Count; } }
        public double SLTPCount { get { return B.GetOrders(true, null).Count - Count; } }
        public double TotalBuyQuantity { get { return Sum(OrderSide.Buy); } }
        public double TotalSellQuantity { get { return Sum(OrderSide.Sell); } }

        private double Sum(OrderSide side)
        {
            double s = 0;
            foreach (Order o in B.GetOrders(false, null)) if (o.Side == side) s += o.Quantity;
            return s;
        }
    }

    public sealed class PositionsManager
    {
        private readonly Runtime.Script.CSharpScript script;
        internal PositionsManager(Runtime.Script.CSharpScript script) { this.script = script; }
        private Broker B { get { return script.RequireTester().Broker; } }

        public event Action<Position> OnClose;
        public event Action<Position> OnOpen;
        public event Action<Position> OnUpdate;

        internal void RaiseClose(Position p) { var h = OnClose; if (h != null) h(p); }
        internal void RaiseOpen(Position p) { var h = OnOpen; if (h != null) h(p); }
        internal void RaiseUpdate(Position p) { var h = OnUpdate; if (h != null) h(p); }

        public List<Position> GetPositions() { return B.GetPositions(null); }
        public List<Position> GetPositions(Predicate<Position> predicate) { return B.GetPositions(predicate); }
        public int Count { get { return B.GetPositions(null).Count; } }

        public bool Close(Position position, double? quantity = null) { return B.Close(position, quantity, "策略平倉"); }

        public bool CloseAsync(Position position, double? quantity, Action<bool> callback)
        {
            bool ok = B.Close(position, quantity, "策略平倉");
            if (callback != null) callback(ok);
            return ok;
        }

        public bool CloseAll(Account account)
        {
            bool ok = true;
            foreach (Position p in B.GetPositions(null)) ok &= B.Close(p, null, "策略平倉");
            return ok;
        }

        public bool SetStopLoss(Position position, double price) { return B.SetStopLoss(position, price); }
        public bool SetStopLoss(Position position, double limitPrice, double stopPrice) { return B.SetStopLoss(position, stopPrice); }
        public bool SetStopLossAsync(Position position, double price, Action<bool> callback) { return Done(B.SetStopLoss(position, price), callback); }
        public bool SetStopLossAsync(Position position, double limitPrice, double stopPrice, Action<bool> callback) { return Done(B.SetStopLoss(position, stopPrice), callback); }
        public bool SetTakeProfit(Position position, double price) { return B.SetTakeProfit(position, price); }
        public bool SetTakeProfitAsync(Position position, double price, Action<bool> callback) { return Done(B.SetTakeProfit(position, price), callback); }
        public bool RemoveStopLoss(Position position) { return B.SetStopLoss(position, double.NaN); }
        public bool RemoveStopLossAsync(Position position, Action<bool> callback) { return Done(B.SetStopLoss(position, double.NaN), callback); }
        public bool RemoveTakeProfit(Position position) { return B.SetTakeProfit(position, double.NaN); }
        public bool RemoveTakeProfitAsync(Position position, Action<bool> callback) { return Done(B.SetTakeProfit(position, double.NaN), callback); }

        public bool SetTrailingStop(Position position, double price, int offset)
        {
            script.RequireTester().Log("SetTrailingStop（移動停損）在回測 v0.1 不支援，已忽略");
            return false;
        }

        public bool SetTrailingStopAsync(Position position, double price, int offset, Action<bool> callback)
        {
            return Done(SetTrailingStop(position, price, offset), callback);
        }

        private static bool Done(bool ok, Action<bool> callback)
        {
            if (callback != null) callback(ok);
            return ok;
        }
    }

    public class StrategyBuilder : TradeApi.ToolBelt.BaseScript
    {
        public StrategyBuilder() : base() { }
        public string CustomTradingHours { get; set; }
        public OrdersManager OrdersManager { get { return ordersManager; } }
        public PositionsManager PositionsManager { get { return positionsManager; } }
    }
}
