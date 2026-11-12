using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Runtime.Script;
using TradeApi.History;
using TradeApi.Instruments;
using TradeApi.Trading;

namespace CtaBacktest
{
    public enum TickMode
    {
        M1Ohlc = 0,    // 每根 M1 四個點（預設，最接近真實）
        BarOhlc = 1,   // 本週期每根四個點
        OpenOnly = 2   // 每根只在開盤呼叫策略；停損停利仍依該根高低價檢查
    }

    [Serializable]   // 子程式工作檔要序列化（spec.md 13.2）
    public sealed class TesterSettings
    {
        public string Timeframe = "H1";
        public DateTime FromUtc, ToUtc;          // ToUtc 不含
        public TickMode Mode = TickMode.M1Ohlc;
        public double InitialDeposit = 10000;
        public string AccountCurrency = "USD";
        public double Leverage = 100;
        public double Spread;                    // 價格單位（不是點數）
        public double CommissionPerLot;          // 每手單邊（固定金額）
        public double CommissionPercent;         // 每筆單邊，名目金額的 %（CTA 的「手續費率%」，買賣各收一次）
        public double CommissionMin;             // 每筆單邊最低手續費（帳戶幣別；CTA 個股 5 美元）
        public double ContractSize = 1;
        public double FxRate = 1;                // 報價幣別換算帳戶幣別
        public string QuoteCurrency;             // 報價幣別（null = 不知道）
        public string FxPair;                    // 換算用貨幣對（例 USDJPY）；null = 不用換算或找不到
        public double FxPairPrice = double.NaN;  // 貨幣對報價（USDJPY 153.21）；FxRate 由它算出
        public string FxSource;                  // 報價來源說明（哪個貨幣對、哪一天）
        public double MinLot = 0.01, MaxLot = 0, LotStep = 0.01;
        public double TickSize = 0.00001;
        public int PriceDigits = 5;
        public double StopOutPercent = 50;       // 保證金水位低於此 % 強制平倉（0 = 只在淨值 <= 0 時）
        public bool KeepLog = true;              // false = 不保留日誌（參數最佳化用，省記憶體；spec 12.3）
        public bool KeepCurve = true;            // false = 不保留資金曲線點（參數最佳化用；統計不受影響，spec 13.4）

        // 淺複製（欄位都是值或不可變字串）：最佳化每組各用一份
        public TesterSettings Clone() { return (TesterSettings)MemberwiseClone(); }
    }

    [Serializable]   // 子程式把結果寫檔傳回視窗程式（spec.md 13.2）
    public sealed class BacktestResult
    {
        public TesterSettings Settings;
        public string Symbol, StrategyName, StrategyClass;
        public List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>();
        public Dictionary<string, string> ParamLabels = new Dictionary<string, string>();   // 程式名稱 → 參數說明（中文）
        public List<TradeRecord> Trades = new List<TradeRecord>();
        public List<EquityPoint> Curve = new List<EquityPoint>();
        public List<string> Log = new List<string>();
        public List<string> Notes = new List<string>();   // 報告要提醒的事
        public BrokerStats Broker;                        // 模擬帳戶的統計摘要（可序列化）
        public long Ticks;
        public int Bars, BarsWithM1, BarsM1Incomplete, WarmupBars;
        public DateTime FirstBar, LastBar;
        public int StrategyErrors;
        public bool Cancelled;
        public string Error;
        public TimeSpan Elapsed;
        public string LastComment;
        public string FxHistory;   // 有用歷史匯率時：換算貨幣對各週期的起點（spec.md 6.2）；null = 固定匯率
    }

    public sealed class Tester
    {
        [ThreadStatic] internal static Tester Current;

        internal Instrument Instrument;
        internal Account Account;
        internal Broker Broker;
        internal LocalHistoricalData Chart;
        internal StrategyBuilder Strategy;
        internal DateTime Now;
        internal string LastComment;

        private readonly SymbolData data;
        private readonly TesterSettings s;
        private readonly BacktestResult result;
        private BarSeries chartSeries;
        private readonly List<LocalHistoricalData> views = new List<LocalHistoricalData>();
        private readonly List<KeyValuePair<HistoricalDataManager, HistoricalData>> loadedQueue = new List<KeyValuePair<HistoricalDataManager, HistoricalData>>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private const int MaxLogLines = 200000;

        private Tester(SymbolData data, TesterSettings s, BacktestResult result)
        {
            this.data = data;
            this.s = s;
            this.result = result;
        }

        // 跑一次回測。parameters：欄位 → 值（沒給的用策略預設值）
        public static BacktestResult Run(SymbolData data, Type strategyType, IDictionary<string, object> parameters,
                                         TesterSettings settings, Action<double> progress, Func<bool> cancel)
        {
            var result = new BacktestResult { Settings = settings, Symbol = data.Symbol, StrategyClass = strategyType.FullName };
            var t = new Tester(data, settings, result);
            var started = DateTime.Now;
            Tester saved = Current;
            Current = t;
            lock (active) active.Add(t);
            try { t.Execute(strategyType, parameters, progress, cancel); }
            catch (Exception ex) { result.Error = ex.GetType().Name + "：" + ex.Message; t.Log("［錯誤］" + ex); }
            finally { Current = saved; lock (active) active.Remove(t); }
            result.Elapsed = DateTime.Now - started;
            result.Broker = t.Broker == null ? null : t.Broker.Snapshot();
            // 中途出錯也保留已發生的交易，報告才和回撤統計一致
            if (t.Broker != null) result.Trades = t.Broker.Trades;
            if (t.Broker != null && t.Broker.MarginFallbacks > 0)
                result.Notes.Add("有 " + t.Broker.MarginFallbacks + " 次計算保證金時，換算貨幣對在當下之前還沒有資料，改用「價格 x 匯率」估算。");
            if (t.Broker != null && t.Broker.FxFallbacks > 0)
                result.Notes.Add("有 " + t.Broker.FxFallbacks + " 個時間點找不到 " + settings.FxPair + " 的歷史價格（沒有資料或缺口超過 4 天），損益改用固定匯率；第一次 "
                    + t.Broker.FxFallbackFrom.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "，最後一次 " + t.Broker.FxFallbackTo.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "。");
            if (result.Error != null && t.Broker != null && t.Broker.OpenPositionCount > 0)
                result.Notes.Add("回測中途出錯而提前結束，還有 " + t.Broker.OpenPositionCount + " 筆部位沒有結算，統計不完整。");
            result.LastComment = t.LastComment;
            return result;
        }

        private void Execute(Type strategyType, IDictionary<string, object> parameters, Action<double> progress, Func<bool> cancel)
        {
            if (s.FromUtc != default(DateTime) && s.ToUtc != default(DateTime) && s.FromUtc >= s.ToUtc)
                throw new InvalidOperationException("起始日要早於結束日（目前起始 " + s.FromUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + "、結束 " + s.ToUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）");
            chartSeries = data.Load(s.Timeframe);
            if (chartSeries.Count == 0) throw new InvalidOperationException("找不到 " + s.Timeframe + " 的資料");
            int start = chartSeries.FirstIndexAtOrAfter(s.FromUtc);
            int end = chartSeries.FirstIndexAtOrAfter(s.ToUtc) - 1;
            if (start > end) throw new InvalidOperationException("選的日期區間裡沒有 K 棒");
            result.WarmupBars = start;
            result.FirstBar = chartSeries.Bars[start].Time;
            result.LastBar = chartSeries.Bars[end].Time;
            if (start < 100) result.Notes.Add("起始日之前只有 " + start + " 根 K 棒可當歷史，需要較長暖機的指標開頭幾筆可能不準。");

            BarSeries m1 = s.Mode == TickMode.M1Ohlc ? data.Load("M1") : null;
            if (s.Mode == TickMode.M1Ohlc && (m1 == null || m1.Count == 0))
                result.Notes.Add("這個商品沒有 M1（1 分鐘）資料，整段改用 " + s.Timeframe + " 的開高低收模擬報價：停損停利在同一根 K 棒內的先後順序是推估的，會比實盤粗糙。"
                    + "要補 M1：在交易王對 " + data.Symbol + " 重掛 HistoryExporter，確認 ExportM1 有勾，等到「已完成 9 / 9」。");

            // 損益換算的前提不確定時，提醒寫進報告（不只顯示在設定頁）
            string quote = s.QuoteCurrency ?? data.QuoteCurrency;
            // 歷史匯率（spec.md 6.2）：自動取得的匯率才用；設定頁手動填寫視為要固定匯率
            PairRates profitRates = null;
            if (quote != null && !string.Equals(quote, s.AccountCurrency, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(s.FxPair) && s.FxSource != null && s.FxSource != "手動填寫")
                profitRates = FxRates.ProfitRates(data, s.FxPair, quote);
            if (profitRates != null)
            {
                result.FxHistory = profitRates.Describe();
                result.Notes.Add("損益與手續費用 " + s.FxPair + " 的歷史價格（" + result.FxHistory + "，取當下最近一根的開盤價）換算成 " + s.AccountCurrency
                    + "；找不到的時間改用固定值 " + s.FxPair + " " + s.FxPairPrice.ToString(CultureInfo.InvariantCulture) + "。");
            }
            else if (quote != null && !string.Equals(quote, s.AccountCurrency, StringComparison.OrdinalIgnoreCase) && Math.Abs(s.FxRate - 1) >= 1e-12)
                result.Notes.Add("匯率固定用 1 " + quote + " = " + s.FxRate.ToString("0.########", CultureInfo.InvariantCulture) + " " + s.AccountCurrency
                    + (string.IsNullOrEmpty(s.FxPair) || double.IsNaN(s.FxPairPrice) ? "" : "（" + s.FxPair + " " + s.FxPairPrice.ToString(CultureInfo.InvariantCulture) + (s.FxSource != null && s.FxSource.StartsWith(s.FxPair) ? "，取自 " + s.FxSource : "") + "）")
                    + "，不隨歷史匯率變動。");
            if (quote == null)
                result.Notes.Add("讀不到報價幣別，損益用匯率 " + s.FxRate.ToString(CultureInfo.InvariantCulture) + " 換算成 " + s.AccountCurrency + "，請確認是否正確。");
            else if (!string.Equals(quote, s.AccountCurrency, StringComparison.OrdinalIgnoreCase) && Math.Abs(s.FxRate - 1) < 1e-12)
                result.Notes.Add("這個商品用 " + quote + " 報價，但匯率是 1；損益與保證金會被當成 " + s.AccountCurrency + " 計算，數字可能差很多。請在設定頁填正確匯率。");
            if (double.IsNaN(data.ContractSize))
                result.Notes.Add("symbol_info.txt 讀不到合約大小，目前用 " + s.ContractSize.ToString(CultureInfo.InvariantCulture) + "，請確認。");
            result.Notes.Add("撮合採對沖模式：每筆成交各自成為一個部位，反向單不會抵銷原部位。");

            TradeApi.ToolBelt.GlobalVariableManager.RemoveAll();
            BuildInstrument();
            Broker = new Broker(this, s);
            Account = Broker.Account;
            if (profitRates != null) Broker.SetProfitRates(profitRates);
            BarSeries mSeries; bool mBaseFirst;
            string mPair = FxRates.MarginPair(data, quote, s.AccountCurrency, out mSeries, out mBaseFirst);
            if (mPair != null && mSeries != null)
            {
                Broker.SetMarginRate(mSeries, mBaseFirst);
                result.Notes.Add("保證金用 " + mPair + " 的歷史價格（" + mSeries.Timeframe + " 開盤價，跟著模擬時間走）換算成 " + s.AccountCurrency + "。");
            }
            else if (mPair != null)
                result.Notes.Add("保證金應該用 " + mPair + " 換算，但資料夾裡沒有它的資料，暫用「價格 x 匯率」估算。請在交易王用 HistoryBridge 或 HistoryExporter 抓 " + mPair + "。");
            Chart = new LocalHistoricalData(chartSeries, new TimeHistoricalRequest(Instrument, DataType.Bid, PeriodOf(s.Timeframe), PeriodValue(s.Timeframe)), 0);
            if (start > 0)
            {
                Bar w = chartSeries.Bars[start - 1];
                Chart.SetForming(start - 1, w.High, w.Low, w.Close);
                Now = w.Time;
            }

            // 建構策略、套參數、Init（順序同平台：建構式 → 參數 → Init）
            // 建構式也計時（卡住偵測看得到）；例外照舊往外丟
            callWhere = "建構式";
            Interlocked.Exchange(ref callStartedTicks, DateTime.UtcNow.Ticks);
            try
            {
                Strategy = (StrategyBuilder)Activator.CreateInstance(strategyType);
                result.StrategyName = Strategy.Credentials.ProjectName ?? strategyType.Name;
                ApplyParameters(Strategy, parameters);
            }
            finally { Interlocked.Exchange(ref callStartedTicks, 0); }
            Call(() => Strategy.Init(), "Init");

            int total = end - start + 1;
            var ticks = new List<TickPoint>(256);
            for (int i = start; i <= end; i++)
            {
                if (cancel != null && cancel()) { result.Cancelled = true; Log("使用者中止回測"); break; }
                Bar b = chartSeries.Bars[i];
                double spanMin = Timeframes.Minutes(s.Timeframe);
                if (i + 1 < chartSeries.Count) spanMin = Math.Min(spanMin, (chartSeries.Bars[i + 1].Time - b.Time).TotalMinutes);
                DateTime barEnd = b.Time.AddMinutes(spanMin);

                ticks.Clear();
                bool usedM1 = false;
                if (m1 != null && m1.Count > 0)
                {
                    // M1 的高低要涵蓋這根 K 棒的高低才用，否則會漏掉極值
                    int j0 = m1.FirstIndexAtOrAfter(b.Time), j = j0;
                    double hi = double.MinValue, lo = double.MaxValue;
                    for (; j < m1.Count && m1.Bars[j].Time < barEnd; j++)
                    {
                        hi = Math.Max(hi, m1.Bars[j].High);
                        lo = Math.Min(lo, m1.Bars[j].Low);
                    }
                    double tol = s.TickSize > 0 ? s.TickSize * 0.5 : 1e-9;
                    if (j > j0 && hi >= b.High - tol && lo <= b.Low + tol)
                    {
                        double prevClose = double.NaN;
                        for (int k = j0; k < j; k++)
                        {
                            Bar mb = m1.Bars[k];
                            // 只有這根的第一個報價、或和上一根 M1 收盤不連續時，才算跳空
                            bool gap = k == j0 || Math.Abs(mb.Open - prevClose) > tol;
                            AddPath(ticks, mb, 1.0, gap);
                            prevClose = mb.Close;
                        }
                        usedM1 = true;
                    }
                    else if (j > j0) result.BarsM1Incomplete++;
                }
                if (!usedM1) AddPath(ticks, b, spanMin, true);
                if (usedM1) result.BarsWithM1++;

                for (int k = 0; k < ticks.Count; k++)
                {
                    TickPoint tp = ticks[k];
                    bool callStrategy = s.Mode != TickMode.OpenOnly || k == 0;
                    OnTick(tp, callStrategy);
                    if (Broker.StoppedOut) break;
                }
                result.Bars++;
                if (s.KeepCurve) result.Curve.Add(new EquityPoint { Time = barEnd, Balance = Broker.Balance, Equity = Broker.Equity });
                if (Broker.StoppedOut) { Log("帳戶淨值歸零，回測停止"); result.Notes.Add("帳戶在 " + Fmt(Now) + " 被強制平倉到歸零，回測提前結束。"); break; }
                if (progress != null && (i - start) % 200 == 0) progress((double)(i - start + 1) / total);
            }

            Call(() => Strategy.Complete(), "Complete");
            if (Broker.OpenPositionCount > 0)
            {
                Log("測試結束，還有 " + Broker.OpenPositionCount + " 筆部位，用最後價格平倉");
                Broker.CloseAll("測試結束");
                // 全部平倉後沒有浮動損益：淨值 = 餘額
                if (result.Curve.Count > 0) result.Curve[result.Curve.Count - 1].Balance = result.Curve[result.Curve.Count - 1].Equity = Broker.Balance;
            }
            result.Trades = Broker.Trades;
            if (progress != null) progress(1.0);
        }

        private struct TickPoint
        {
            public DateTime Time;
            public double Price;
            public bool Gap;
        }

        // 一根 K 棒產生四個報價：收紅 開→低→高→收；收黑 開→高→低→收（MT5 做法）
        private static void AddPath(List<TickPoint> list, Bar b, double spanMinutes, bool gapAtOpen)
        {
            double sec = Math.Max(4, spanMinutes * 60);
            bool up = b.Close >= b.Open;
            list.Add(new TickPoint { Time = b.Time, Price = b.Open, Gap = gapAtOpen });
            list.Add(new TickPoint { Time = b.Time.AddSeconds(sec / 4), Price = up ? b.Low : b.High });
            list.Add(new TickPoint { Time = b.Time.AddSeconds(sec / 2), Price = up ? b.High : b.Low });
            list.Add(new TickPoint { Time = b.Time.AddSeconds(sec - 1), Price = b.Close });
        }

        private void OnTick(TickPoint tp, bool callStrategy)
        {
            Now = tp.Time;
            result.Ticks++;
            bool newBar = Chart.OnTick(tp.Time, tp.Price);
            foreach (LocalHistoricalData v in views) v.OnTick(tp.Time, tp.Price);
            Instrument.DayInfo.OnTick(tp.Time, tp.Price, tp.Price + s.Spread);
            Broker.OnTick(tp.Time, tp.Price, tp.Gap);
            if (!callStrategy) return;
            if (loadedQueue.Count > 0)
            {
                var q = new List<KeyValuePair<HistoricalDataManager, HistoricalData>>(loadedQueue);
                loadedQueue.Clear();
                foreach (var kv in q)
                {
                    var h = kv.Key.OnLoaded;
                    if (h != null) Call(() => h(kv.Value), "OnLoaded");
                }
            }
            TickStatus st = newBar ? TickStatus.IsBar : TickStatus.IsQuote;
            Call(() => Strategy.Update(st), "Update");
        }

        // 報告頂端的提醒，同一句只記一次
        internal void Note(string text)
        {
            if (!result.Notes.Contains(text)) result.Notes.Add(text);
        }

        // 卡住偵測（spec.md 13.2）：正在執行的策略呼叫從何時開始（UTC Ticks，0 = 沒有在跑），子程式的心跳執行緒讀它
        private long callStartedTicks;
        private string callWhere;
        private static readonly List<Tester> active = new List<Tester>();

        // 目前所有回測中，執行最久的那個策略呼叫：秒數、在哪個函式、模擬時間；沒有回傳 0
        public static double LongestStrategyCall(out string where, out DateTime simTime)
        {
            where = null; simTime = default(DateTime);
            double longest = 0;
            long now = DateTime.UtcNow.Ticks;
            lock (active)
                foreach (Tester t in active)
                {
                    long st = Interlocked.Read(ref t.callStartedTicks);
                    if (st == 0) continue;
                    double sec = (now - st) / (double)TimeSpan.TicksPerSecond;
                    if (sec > longest) { longest = sec; where = t.callWhere; simTime = t.Now; }
                }
            return longest;
        }

        private void Call(Action a, string where)
        {
            callWhere = where;
            Interlocked.Exchange(ref callStartedTicks, DateTime.UtcNow.Ticks);
            try { a(); }
            catch (Exception ex)
            {
                result.StrategyErrors++;
                if (result.StrategyErrors <= 50)
                {
                    string first = ex.StackTrace == null ? "" : ex.StackTrace.Split('\n')[0].Trim();
                    Log("［策略錯誤］" + where + "：" + ex.GetType().Name + " " + ex.Message + " " + first);
                }
                else if (result.StrategyErrors == 51) Log("［策略錯誤］超過 50 次，之後不再記錄");
            }
            finally { Interlocked.Exchange(ref callStartedTicks, 0); }
        }

        private void ApplyParameters(object strategy, IDictionary<string, object> parameters)
        {
            var known = new HashSet<string>();
            var bad = new List<string>();
            foreach (FieldInfo f in strategy.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var ip = f.GetCustomAttribute<InputParameterAttribute>();
                if (ip == null) continue;
                known.Add(f.Name);
                if (!string.IsNullOrEmpty(ip.Comment)) result.ParamLabels[f.Name] = ip.Comment;
                object v;
                if (parameters != null && parameters.TryGetValue(f.Name, out v) && v != null)
                {
                    try { f.SetValue(strategy, ConvertValue(v, f.FieldType)); }
                    catch (Exception ex) { Log("參數 " + f.Name + " 設定失敗：" + ex.Message + "，用預設值"); bad.Add(f.Name + "=" + Convert.ToString(v, CultureInfo.InvariantCulture)); }
                }
                object now = f.GetValue(strategy);
                result.Parameters.Add(new KeyValuePair<string, string>(f.Name, Convert.ToString(now, CultureInfo.InvariantCulture)));
            }
            // 傳錯的參數照跑（用預設值），但寫進報告備註，不默默忽略（spec.md 第 4 節）
            if (bad.Count > 0) result.Notes.Add("這些參數的值轉不成參數型別，改用預設值：" + string.Join("、", bad.ToArray()) + "。");
            if (parameters != null)
            {
                var unknown = new List<string>();
                foreach (string key in parameters.Keys) if (!known.Contains(key)) unknown.Add(key);
                if (unknown.Count > 0) result.Notes.Add("這些名稱不是策略的參數，已忽略：" + string.Join("、", unknown.ToArray()) + "。");
            }
        }

        public static object ConvertValue(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum)
            {
                if (v is string) return Enum.Parse(t, (string)v);
                return Enum.ToObject(t, v);
            }
            if (t == typeof(bool) && v is string) return bool.Parse((string)v);
            return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }

        // ---------------- 商品 ----------------
        private void BuildInstrument()
        {
            var info = new List<KeyValuePair<string, string>>(data.SymbolInfo);
            string cs = s.ContractSize.ToString("R", CultureInfo.InvariantCulture);
            string ml = s.MinLot.ToString("R", CultureInfo.InvariantCulture);
            if (!Replace(info, cs, "合約規格", "Lot size")) info.Add(new KeyValuePair<string, string>("合約規格", cs));
            if (!Replace(info, ml, "最小交易量(手)", "Minimal lot")) info.Add(new KeyValuePair<string, string>("最小交易量(手)", ml));
            InstrumentType type = InstrumentType.CFD;
            string ts;
            if (data.Header.TryGetValue("InstrumentType", out ts)) Enum.TryParse(ts, out type);
            Instrument = new Instrument(data.Symbol, type, s.TickSize, s.LotStep, s.MinLot, s.MaxLot > 0 ? s.MaxLot : 1000000, info);
        }

        private static bool Replace(List<KeyValuePair<string, string>> info, string value, params string[] keys)
        {
            bool found = false;
            for (int i = 0; i < info.Count; i++)
                foreach (string k in keys)
                    if (info[i].Key == k) { info[i] = new KeyValuePair<string, string>(k, value); found = true; }
            return found;
        }

        internal List<Instrument> FindInstruments(string symbol)
        {
            var list = new List<Instrument>();
            if (string.IsNullOrEmpty(symbol)) return list;
            string want = symbol.Split(':')[0].Trim().ToUpperInvariant();
            if (want == data.Symbol.Split(':')[0].Trim().ToUpperInvariant()) { list.Add(Instrument); return list; }
            string quote = s.QuoteCurrency ?? data.QuoteCurrency;   // 港股等匯出檔沒有計價幣別，用官方表的
            string acc = s.AccountCurrency.ToUpperInvariant();
            if (!string.IsNullOrEmpty(quote))
            {
                quote = quote.ToUpperInvariant();
                double rate = double.NaN;
                double now = Broker != null ? Broker.Fx() : s.FxRate;   // 歷史匯率時是當下的值（spec.md 6.2）
                if (want == quote + acc) rate = now;
                else if (want == acc + quote && now > 0) rate = 1.0 / now;
                if (!double.IsNaN(rate))
                {
                    var fx = new Instrument(want, InstrumentType.Forex, 0.00001, 0.01, 0.01, 1000000, null);
                    fx.DayInfo.OnTick(Now, rate, rate);
                    list.Add(fx);
                    return list;
                }
            }
            if (warned.Add("inst:" + want)) Log("回測只有 " + data.Symbol + " 的資料，GetInstruments(\"" + symbol + "\") 回傳空清單");
            return list;
        }

        // ---------------- 多週期 ----------------
        internal HistoricalData GetHistory(HistoricalDataManager mgr, HistoricalRequest request, Interval interval)
        {
            var tr = request as TimeHistoricalRequest;
            string tf = tr == null ? null : Timeframes.FromPeriod(tr.Period, tr.Value);
            BarSeries series = null;
            if (tf == null) Warn("hist:" + (tr == null ? "?" : tr.Period + "x" + tr.Value), "沒有對應的週期資料（" + (tr == null ? "非時間週期請求" : tr.Period + " x" + tr.Value) + "），回傳空資料");
            else if (request.Instrument != null && FindInstruments(request.Instrument.Symbol).Count == 0) Warn("histsym:" + request.Instrument.Symbol, "沒有 " + request.Instrument.Symbol + " 的資料，回傳空資料");
            else if (!data.HasTimeframe(tf)) Warn("histtf:" + tf, "資料夾裡沒有 " + tf + ".csv，回傳空資料；請用 HistoryExporter 匯出");
            else series = data.Load(tf);

            if (series == null) series = new BarSeries(tf ?? "?", new List<Bar>());
            int baseIndex = interval == null ? 0 : series.FirstIndexAtOrAfter(interval.FromUtc);
            var view = new LocalHistoricalData(series, request, baseIndex);
            int idx = series.IndexAtOrBefore(Now);
            if (idx >= 0)
            {
                double h, l, c;
                AggregateSince(series.Bars[idx].Time, out h, out l, out c);
                view.SetForming(idx, h, l, c);
            }
            views.Add(view);
            loadedQueue.Add(new KeyValuePair<HistoricalDataManager, HistoricalData>(mgr, view));
            return view;
        }

        internal void RemoveHistory(HistoricalData d)
        {
            var v = d as LocalHistoricalData;
            if (v != null) views.Remove(v);
        }

        // 從 from 起到目前為止的高低收（用本圖 K 棒與目前報價），給中途要求的上層資料當進行中那根
        private void AggregateSince(DateTime from, out double high, out double low, out double close)
        {
            high = double.NegativeInfinity; low = double.PositiveInfinity; close = double.NaN;
            if (Chart == null || Chart.Forming < 0) return;
            for (int k = chartSeries.FirstIndexAtOrAfter(from); k < Chart.Forming; k++)
            {
                Bar b = chartSeries.Bars[k];
                high = Math.Max(high, b.High); low = Math.Min(low, b.Low); close = b.Close;
            }
            if (chartSeries.Bars[Chart.Forming].Time >= from)
            {
                high = Math.Max(high, Chart.GetValue(PriceType.High, 0));
                low = Math.Min(low, Chart.GetValue(PriceType.Low, 0));
                close = Chart.GetValue(PriceType.Close, 0);
            }
            if (!double.IsNaN(Broker.Bid))
            {
                high = Math.Max(high, Broker.Bid); low = Math.Min(low, Broker.Bid); close = Broker.Bid;
            }
        }

        private static Period PeriodOf(string tf)
        {
            if (tf.StartsWith("M") && tf != "MN1") return Period.Minute;
            if (tf.StartsWith("H")) return Period.Hour;
            if (tf == "D1") return Period.Day;
            if (tf == "W1") return Period.Week;
            return Period.Month;
        }

        private static int PeriodValue(string tf)
        {
            if (tf == "MN1") return 1;
            int v;
            return int.TryParse(tf.Substring(1), out v) ? v : 1;
        }

        // ---------------- 日誌 ----------------
        private void Warn(string key, string msg)
        {
            if (warned.Add(key)) Log(msg);
        }

        internal void Log(string message)
        {
            if (!s.KeepLog) return;
            if (result.Log.Count >= MaxLogLines)
            {
                if (result.Log.Count == MaxLogLines) result.Log.Add("（日誌超過 " + MaxLogLines + " 行，之後不再記錄）");
                return;
            }
            result.Log.Add(Fmt(Now) + "  " + message);
        }

        private static string Fmt(DateTime t)
        {
            return t == DateTime.MinValue ? "----------------" : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
