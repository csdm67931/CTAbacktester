using System;
using CtaBacktest;

// 模擬交易王 Runtime.Script 命名空間（成員名稱照 TradeLogic.dll，2026-11-09 反射確認）
// 只實作回測需要的部分；策略用到沒有的成員會在編譯時報錯，不會默默算錯。

namespace Runtime.Script
{
    public enum TickStatus { IsHistory = 0, IsBar = 1, IsQuote = 2 }

    public enum InputType
    {
        Combobox = 0, Checkbox = 1, Numeric = 2, Color = 3, Instrument = 4, Account = 5,
        String = 6, DateTime = 7, TimeSpan = 8, TimeInterval = 9, TimeZoneInfo = 10
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class InputParameterAttribute : Attribute
    {
        public InputParameterAttribute(InputType inputType, string comment) : this(inputType, comment, -1) { }
        public InputParameterAttribute(InputType inputType, string comment, int orderNumber)
        {
            InputType = inputType;
            Comment = comment;
            OrderNumber = orderNumber;
        }
        public string Comment { get; private set; }
        public InputType InputType { get; private set; }
        public int OrderNumber { get; private set; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class SimpleNumericAttribute : Attribute
    {
        public SimpleNumericAttribute(double minimum, double maximum) { Minimum = minimum; Maximum = maximum; }
        public SimpleNumericAttribute(int precision) { DecimalPlaces = precision; }
        public SimpleNumericAttribute(int precision, double increment) { DecimalPlaces = precision; Increment = increment; }
        public SimpleNumericAttribute(double minimum, double maximum, int precision, double increment)
        {
            Minimum = minimum; Maximum = maximum; DecimalPlaces = precision; Increment = increment;
        }
        public int? DecimalPlaces { get; set; }
        public double? Increment { get; set; }
        public double? Maximum { get; set; }
        public double? Minimum { get; set; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
    public sealed class ComboboxItemAttribute : Attribute
    {
        public ComboboxItemAttribute(string name, object value) { Name = name; Value = value; }
        public string Name { get; set; }
        public object Value { get; set; }
    }

    public interface ICredentials
    {
        string Author { get; set; }
        string Company { get; set; }
        string Copyrights { get; set; }
        DateTime DateOfCreation { get; set; }
        string Description { get; set; }
        DateTime ExpirationDate { get; set; }
        string Password { get; set; }
        string ProjectName { get; set; }
        string Version { get; set; }
    }

    public sealed class Credentials : ICredentials
    {
        internal Credentials() { }
        public string Author { get; set; }
        public string Company { get; set; }
        public string Copyrights { get; set; }
        public DateTime DateOfCreation { get; set; }
        public string Description { get; set; }
        public DateTime ExpirationDate { get; set; }
        public string Password { get; set; }
        public string ProjectName { get; set; }
        public string Version { get; set; }
    }

    public class CSharpScript : IDisposable
    {
        private readonly Credentials credentials = new Credentials();

        // 建構當下所在的回測（讀預設參數時為 null，此時呼叫平台功能會丟出例外）
        internal readonly Tester Tester;

        public CSharpScript()
        {
            Tester = Tester.Current;
        }

        public virtual ICredentials Credentials { get { return credentials; } }
        public virtual bool Disposed { get; set; }
        public Guid Id { get; } = Guid.NewGuid();
        public virtual void Dispose() { Disposed = true; }
        public virtual void OnPaintChart(object sender, Runtime.Script.Charts.PaintChartEventArgs args) { }

        internal Tester RequireTester()
        {
            if (Tester == null) throw new InvalidOperationException("不在回測執行中，不能呼叫平台功能");
            return Tester;
        }
    }

    public class CSharpIndicator : CSharpScript
    {
        protected CSharpIndicator() { }
        public virtual void Init() { }
        public virtual void Update(TickStatus args) { }
        public virtual void Complete() { }
        public bool SeparateWindow { get; set; }
    }
}

namespace Runtime.Script.Charts
{
    // 回測沒有圖表，OnPaintChart 不會被呼叫；只為了讓有覆寫它的策略能編譯
    public class PaintChartEventArgs : EventArgs { }
}
