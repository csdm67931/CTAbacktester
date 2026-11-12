using System;
using Runtime.Script;
using TradeApi.History;
using TradeApi.Indicators;
using TradeApi.Instruments;
using TradeApi.Trading;

namespace TradeApi.ToolBelt
{
    public enum VariableLifetime { SaveSession = 0, SaveFile = 1 }

    // 跨程式共用變數：回測時只存在記憶體，每次回測開始清空（不會讀寫交易王的共用變數檔）
    // 每個執行緒一份（spec 12.3）：參數最佳化平行跑時，各組回測的共用變數互不干擾
    public sealed class GlobalVariableManager
    {
        // [ThreadStatic] 欄位不能用初始器（只有第一個執行緒會初始化），一律經 Vars 惰性建立
        [ThreadStatic] private static System.Collections.Generic.Dictionary<string, object> vars;
        private static System.Collections.Generic.Dictionary<string, object> Vars { get { return vars ?? (vars = new System.Collections.Generic.Dictionary<string, object>()); } }
        private static readonly object sync = new object();
        public GlobalVariableManager() { }
        public static bool Exists(string name) { lock (sync) return name != null && Vars.ContainsKey(name); }
        public static void Flush() { }
        public static System.Collections.Generic.List<string> GetGlobalVariableList() { lock (sync) return new System.Collections.Generic.List<string>(Vars.Keys); }
        public static object GetValue(string name) { lock (sync) { object v; return name != null && Vars.TryGetValue(name, out v) ? v : null; } }
        public static bool Remove(string name) { lock (sync) return name != null && Vars.Remove(name); }
        public static void RemoveAll() { lock (sync) Vars.Clear(); }
        public static void SetValue(string name, object value, VariableLifetime lifetime) { lock (sync) Vars[name] = value; }
        public static bool TryGetValue(string name, out object obj) { lock (sync) { obj = null; return name != null && Vars.TryGetValue(name, out obj); } }
        public static int Count { get { lock (sync) return Vars.Count; } }
    }

    public sealed class Notification
    {
        private readonly CSharpScript script;
        internal Notification(CSharpScript script) { this.script = script; }

        public void Print(string message) { script.RequireTester().Log(message); }
        public void Alert(string message) { script.RequireTester().Log("［提醒］" + message); }
        public void Comment(string message) { script.RequireTester().LastComment = message; }
        public void PlaySound(string filePath) { }
    }

    // 平台的 BaseScript（指標、策略共用）：Init → Update×N → Complete
    public abstract class BaseScript : CSharpIndicator
    {
        internal readonly OrdersManager ordersManager;
        internal readonly PositionsManager positionsManager;
        private readonly AccountManager accountManager;
        private readonly HistoricalDataManager historicalDataManager;
        private readonly IndicatorsManager indicatorsManager;
        private readonly InstrumentsManager instrumentManager;
        private readonly Notification notification;

        internal BaseScript()
        {
            ordersManager = new OrdersManager(this);
            positionsManager = new PositionsManager(this);
            accountManager = new AccountManager(this);
            historicalDataManager = new HistoricalDataManager(this);
            indicatorsManager = new IndicatorsManager(this);
            instrumentManager = new InstrumentsManager(this);
            notification = new Notification(this);
        }

        public AccountManager AccountManager { get { return accountManager; } }
        public HistoricalDataManager HistoricalDataManager { get { return historicalDataManager; } }
        public HistoricalData HistoryDataSeries { get { return RequireTester().Chart; } }
        public IndicatorsManager IndicatorsManager { get { return indicatorsManager; } }
        public InstrumentsManager InstrumentsManager { get { return instrumentManager; } }
        public Notification Notification { get { return notification; } }
    }
}
