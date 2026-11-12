using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

// 參數最佳化（仿 MT5 Optimization）。規格：spec.md 第 12 節
namespace CtaBacktest
{
    public enum OptMode { Full = 0, Random = 1 }

    // 排序依據；數值順序即畫面下拉選單順序與 last.txt 的 opt.sort
    public enum OptSortKey { NetProfit = 0, ProfitFactor, ExpectedPayoff, EquityDDPct, RecoveryFactor, Sharpe, Trades, WinRate }

    // 一個要掃的參數：已展開成候選值（以下三個類別都要能序列化，子程式用，spec.md 13.2）
    [Serializable]
    public sealed class OptParam
    {
        public string Name;          // 程式名稱（欄位名）
        public string Label;         // 中文說明（結果表標題用）
        public Type ValueType;
        public List<object> Values;  // 已轉成欄位型別的候選值
    }

    [Serializable]
    public sealed class OptSettings
    {
        public OptMode Mode = OptMode.Full;
        public int RandomCount = 500;
        public int Seed = 1;
        public int Threads = 1;
        public bool Forward;
        public double ForwardPct = 0.25;   // 樣本外比例（0.05～0.5）
        public OptSortKey SortKey = OptSortKey.NetProfit;
        public const int MaxCombinations = 100000;
        public const int MaxThreads = 64;
    }

    [Serializable]
    public sealed class OptResult
    {
        public int Pass;                                  // 第幾組（計畫順序，從 1 起）
        public Dictionary<string, object> Params;         // 只含掃描參數
        public Stats InSample, OutSample;                 // OutSample 沒前進測試（或樣本內被中止）時為 null
        public string Error;                              // 兩段任一段的 BacktestResult.Error
        public int StrategyErrors;
        public bool Cancelled;
        public TimeSpan Elapsed;                          // 兩段合計
    }

    public static class OptRanges
    {
        public const int MaxValuesPerParam = 100000;
        private const double Eps = 1e-9;

        private static bool IsIntegerType(Type t)
        {
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte);
        }

        private static bool IsNumericType(Type t)
        {
            return IsIntegerType(t) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);
        }

        // 數值型或 bool，且不是下拉選項（ComboboxItem）也不是 enum
        public static bool IsOptimizable(ParamInfo p)
        {
            if (p == null || p.Items.Count > 0 || p.ValueType.IsEnum) return false;
            return p.ValueType == typeof(bool) || IsNumericType(p.ValueType);
        }

        // 候選值；error != null 表示輸入不合法（訊息已含參數中文名）。bool 回 [false, true]。
        public static List<object> Expand(ParamInfo p, double start, double step, double stop, out string error)
        {
            error = null;
            string name = "參數「" + (string.IsNullOrEmpty(p.Label) ? p.Name : p.Label) + "」：";
            if (!IsOptimizable(p)) { error = name + "這種型別不能最佳化"; return null; }
            if (p.ValueType == typeof(bool)) return new List<object> { false, true };
            if (double.IsNaN(start) || double.IsNaN(step) || double.IsNaN(stop) || double.IsInfinity(start) || double.IsInfinity(step) || double.IsInfinity(stop))
            { error = name + "起、步、終必須是數字"; return null; }
            if (!(step > 0)) { error = name + "步必須大於 0"; return null; }
            if (start > stop) { error = name + "起不能大於終"; return null; }
            if (IsIntegerType(p.ValueType) && (!IsWhole(start) || !IsWhole(step) || !IsWhole(stop)))
            { error = name + "整數參數的起、步、終必須是整數"; return null; }
            if (p.Min.HasValue && (start < p.Min.Value - Eps || stop < p.Min.Value - Eps))
            { error = name + "起、終不能小於最小值 " + p.Min.Value.ToString(CultureInfo.InvariantCulture); return null; }
            if (p.Max.HasValue && (start > p.Max.Value + Eps || stop > p.Max.Value + Eps))
            { error = name + "起、終不能大於最大值 " + p.Max.Value.ToString(CultureInfo.InvariantCulture); return null; }
            double n = Math.Floor((stop - start) / step + Eps) + 1;
            if (n > MaxValuesPerParam) { error = name + "候選值有 " + n.ToString("N0", CultureInfo.InvariantCulture) + " 個，超過上限 " + MaxValuesPerParam.ToString("N0", CultureInfo.InvariantCulture) + "，請加大步距"; return null; }

            var list = new List<object>();
            try
            {
                // 用「起＋k×步」而不是累加，避免浮點累積誤差；四捨五入到 10 位，免得顯示 0.30000000000000004
                for (int k = 0; ; k++)
                {
                    double v = start + k * step;
                    if (v > stop + Eps) break;
                    v = IsIntegerType(p.ValueType) ? Math.Round(v) : Math.Round(v, 10);
                    list.Add(Tester.ConvertValue(v, p.ValueType));
                }
            }
            catch (OverflowException) { error = name + "數值超出這個型別的範圍"; return null; }
            return list;
        }

        private static bool IsWhole(double x) { return Math.Abs(x - Math.Round(x)) < Eps; }

        // 各參數候選數的乘積；溢位時回 long.MaxValue
        public static long CountCombinations(IList<OptParam> ps)
        {
            long total = 1;
            foreach (OptParam p in ps)
            {
                long c = p.Values == null ? 0 : p.Values.Count;
                if (c == 0) return 0;
                if (total > long.MaxValue / c) return long.MaxValue;
                total *= c;
            }
            return total;
        }
    }

    public sealed class Optimizer
    {
        private readonly SymbolData data;
        private readonly Type strategyType;
        private readonly Dictionary<string, object> baseParams;
        private readonly TesterSettings settings;
        private readonly List<OptParam> optParams;
        private readonly OptSettings opt;

        public Optimizer(SymbolData data, Type strategyType, IDictionary<string, object> baseParams,
                         TesterSettings settings, IList<OptParam> optParams, OptSettings opt)
        {
            this.data = data;
            this.strategyType = strategyType;
            this.baseParams = baseParams == null ? new Dictionary<string, object>() : new Dictionary<string, object>(baseParams);
            this.settings = settings.Clone();
            this.optParams = new List<OptParam>(optParams);
            this.opt = opt;
        }

        // 實際要跑幾組：窮舉 = 組合數；隨機 = min(抽樣組數, 組合數)
        public static long PlannedCount(long total, OptSettings opt)
        {
            if (opt.Mode == OptMode.Random) return Math.Min(total, Math.Max(1, opt.RandomCount));
            return total;
        }

        // 產生要跑的組合（窮舉或隨機，順序固定）；每組是「掃描參數 → 值」
        public List<Dictionary<string, object>> BuildPlan()
        {
            long total = OptRanges.CountCombinations(optParams);
            var plan = new List<Dictionary<string, object>>();
            if (opt.Mode == OptMode.Random && opt.RandomCount < 1)
                throw new InvalidOperationException("抽樣組數至少要 1 組");
            if (total <= 0) return plan;
            int k = optParams.Count;
            if (opt.Mode == OptMode.Full || opt.RandomCount >= total)
            {
                if (total > OptSettings.MaxCombinations)
                    throw new InvalidOperationException("組合數 " + total.ToString("N0", CultureInfo.InvariantCulture) + " 超過上限 " + OptSettings.MaxCombinations.ToString("N0", CultureInfo.InvariantCulture) + "，請縮小範圍或改用隨機抽樣");
                // 混合進位：最後一個參數變最快、第一個變最慢
                var idx = new int[k];
                for (long n = 0; n < total; n++)
                {
                    plan.Add(Combo(idx));
                    for (int j = k - 1; j >= 0; j--)
                    {
                        if (++idx[j] < optParams[j].Values.Count) break;
                        idx[j] = 0;
                    }
                }
                return plan;
            }
            // 隨機抽樣（蒙地卡羅）：同種子同結果；索引串成字串去重
            int want = Math.Max(1, Math.Min(opt.RandomCount, OptSettings.MaxCombinations));
            var rng = new Random(opt.Seed);
            var seen = new HashSet<string>();
            long misses = 0, maxMisses = 20L * want;
            var pick = new int[k];
            var sb = new StringBuilder();
            while (plan.Count < want && misses < maxMisses)
            {
                sb.Clear();
                for (int j = 0; j < k; j++)
                {
                    pick[j] = rng.Next(optParams[j].Values.Count);
                    sb.Append(pick[j]).Append(',');
                }
                if (!seen.Add(sb.ToString())) { misses++; continue; }
                plan.Add(Combo(pick));
            }
            return plan;
        }

        private Dictionary<string, object> Combo(int[] idx)
        {
            var d = new Dictionary<string, object>();
            for (int j = 0; j < idx.Length; j++) d[optParams[j].Name] = optParams[j].Values[idx[j]];
            return d;
        }

        // 前進測試切分：切點 = 起始日 + floor(總天數 x (1 - 比例)) 天的 00:00 UTC；切點無效回 false
        public static bool SplitForward(TesterSettings s, double pct, out TesterSettings inSample, out TesterSettings outSample, out DateTime splitUtc)
        {
            inSample = outSample = null;
            splitUtc = DateTime.MinValue;
            if (s == null || !(pct > 0) || !(pct < 1)) return false;
            double totalDays = (s.ToUtc - s.FromUtc).TotalDays;
            if (!(totalDays > 0)) return false;
            int k = (int)Math.Floor(totalDays * (1 - pct) + 1e-9);
            DateTime split = DateTime.SpecifyKind(s.FromUtc.Date.AddDays(k), DateTimeKind.Utc);
            if (split <= s.FromUtc || split >= s.ToUtc) return false;
            inSample = s.Clone(); inSample.ToUtc = split;
            outSample = s.Clone(); outSample.FromUtc = split;
            splitUtc = split;
            return true;
        }

        // 阻塞執行（在背景執行緒呼叫）。每組完成呼叫 onResult（在工作執行緒上，呼叫端自己負責同步）。
        // 回傳依 Pass 排序的全部結果（取消時只含已開始的組）。
        public List<OptResult> Run(Action<OptResult> onResult, Func<bool> cancel)
        {
            Func<bool> stop = () => cancel != null && cancel();
            // 先把資料載好（SymbolData.Load 有 lock，之後各執行緒共用同一份 K 棒）
            data.Load(settings.Timeframe);
            if (settings.Mode == TickMode.M1Ohlc && data.HasTimeframe("M1")) data.Load("M1");

            TesterSettings sIn, sOut = null;
            if (opt.Forward)
            {
                DateTime split;
                if (!SplitForward(settings, opt.ForwardPct, out sIn, out sOut, out split))
                    throw new InvalidOperationException("回測期間太短，無法切出樣本外");
                sOut.KeepLog = false;
                sOut.KeepCurve = false;
            }
            else sIn = settings.Clone();
            sIn.KeepLog = false;
            sIn.KeepCurve = false;   // 每組不留資金曲線，省記憶體（spec 13.4）

            List<Dictionary<string, object>> plan = BuildPlan();
            var results = new List<OptResult>(plan.Count);
            int next = -1;
            int n = Math.Max(1, Math.Min(Math.Min(opt.Threads, OptSettings.MaxThreads), Math.Max(1, plan.Count)));
            ThreadStart worker = () =>
            {
                while (true)
                {
                    if (stop()) break;
                    int i = Interlocked.Increment(ref next);
                    if (i >= plan.Count) break;
                    OptResult res = RunOne(i, plan[i], sIn, sOut, stop);
                    lock (results) results.Add(res);
                    if (onResult != null)
                    {
                        try { onResult(res); } catch { }   // 回呼的例外不可弄死工作執行緒
                    }
                }
            };
            // 不用 ThreadPool：避免和畫面的 Task.Run、資料橋搶執行緒
            var threads = new List<Thread>();
            for (int t = 0; t < n; t++)
            {
                var th = new Thread(worker) { IsBackground = true, Name = "Optimizer-" + (t + 1) };
                threads.Add(th);
                th.Start();
            }
            foreach (Thread th in threads) th.Join();
            lock (results) return results.OrderBy(x => x.Pass).ToList();
        }

        private OptResult RunOne(int i, Dictionary<string, object> combo, TesterSettings sIn, TesterSettings sOut, Func<bool> stop)
        {
            var res = new OptResult { Pass = i + 1, Params = combo };
            DateTime started = DateTime.Now;
            try
            {
                var merged = new Dictionary<string, object>(baseParams);
                foreach (var kv in combo) merged[kv.Key] = kv.Value;
                BacktestResult r1 = Tester.Run(data, strategyType, merged, sIn.Clone(), null, stop);
                res.InSample = Stats.From(r1);
                res.Error = r1.Error;
                res.StrategyErrors = r1.StrategyErrors;
                res.Cancelled = r1.Cancelled;
                if (sOut != null && !r1.Cancelled)
                {
                    BacktestResult r2 = Tester.Run(data, strategyType, merged, sOut.Clone(), null, stop);
                    res.OutSample = Stats.From(r2);
                    if (res.Error == null && r2.Error != null) res.Error = "樣本外：" + r2.Error;
                    res.StrategyErrors += r2.StrategyErrors;
                    res.Cancelled |= r2.Cancelled;
                }
            }
            catch (Exception ex)
            {
                res.Error = ex.GetType().Name + "：" + ex.Message;
            }
            res.Elapsed = DateTime.Now - started;
            return res;
        }

        // ---------------- 排序 ----------------
        // 一份統計的指標值；0 筆交易時除交易筆數外都是「沒有值」（NaN）
        public static double StatValue(Stats st, OptSortKey key)
        {
            if (st == null) return double.NaN;
            if (st.Trades == 0 && key != OptSortKey.Trades) return double.NaN;
            switch (key)
            {
                case OptSortKey.NetProfit: return st.NetProfit;
                case OptSortKey.ProfitFactor: return st.ProfitFactor;
                case OptSortKey.ExpectedPayoff: return st.ExpectedPayoff;
                case OptSortKey.EquityDDPct: return st.EquityDDPct;
                case OptSortKey.RecoveryFactor: return st.RecoveryFactor;
                case OptSortKey.Sharpe: return st.Sharpe;
                case OptSortKey.Trades: return st.Trades;
                case OptSortKey.WinRate: return 100.0 * st.Wins / st.Trades;
            }
            return double.NaN;
        }

        // 排序用（樣本內）：沒有值回 NaN（0 筆交易、錯誤、已中止）
        public static double Metric(OptResult r, OptSortKey key)
        {
            if (r == null || r.Error != null || r.Cancelled) return double.NaN;
            return StatValue(r.InSample, key);
        }

        // 樣本外的指標（前進測試四欄）；錯誤、中止或沒跑樣本外時 NaN
        public static double OutMetric(OptResult r, OptSortKey key)
        {
            if (r == null || r.Error != null || r.Cancelled) return double.NaN;
            return StatValue(r.OutSample, key);
        }

        public static bool SmallerIsBetter(OptSortKey key) { return key == OptSortKey.EquityDDPct; }

        // descending = 數值由大到小。NaN 不論方向永遠最後；+∞ 降冪時最前；同值依 Pass
        // 「好的在前」：Compare(a, b, key, !SmallerIsBetter(key))
        public static int Compare(OptResult a, OptResult b, OptSortKey key, bool descending)
        {
            double x = Metric(a, key), y = Metric(b, key);
            bool nx = double.IsNaN(x), ny = double.IsNaN(y);
            if (nx != ny) return nx ? 1 : -1;
            if (!nx)
            {
                int c = x.CompareTo(y);
                if (c != 0) return descending ? -c : c;
            }
            return a.Pass.CompareTo(b.Pass);
        }

        // 備註欄：回測錯誤、策略錯誤次數、已中止
        public static string NoteOf(OptResult r)
        {
            var parts = new List<string>();
            if (r.Cancelled) parts.Add("已中止");
            if (r.Error != null) parts.Add("回測錯誤：" + r.Error);
            if (r.StrategyErrors > 0) parts.Add("策略錯誤 " + r.StrategyErrors + " 次");
            if (r.Error == null && !r.Cancelled && r.InSample != null && r.InSample.Trades == 0) parts.Add("沒有交易");
            return string.Join("；", parts);
        }

        // ---------------- 匯出 ----------------
        private static readonly OptSortKey[] MainCols = { OptSortKey.NetProfit, OptSortKey.ProfitFactor, OptSortKey.ExpectedPayoff, OptSortKey.EquityDDPct, OptSortKey.Trades, OptSortKey.WinRate, OptSortKey.RecoveryFactor, OptSortKey.Sharpe };
        private static readonly OptSortKey[] OutCols = { OptSortKey.NetProfit, OptSortKey.ProfitFactor, OptSortKey.EquityDDPct, OptSortKey.Trades };

        // UTF-8 由呼叫端決定（建議含 BOM）；數字 InvariantCulture、無限大寫 inf、沒有值寫空白
        public static string ToCsv(IList<OptResult> results, IList<OptParam> ps, bool forward)
        {
            var sb = new StringBuilder();
            var head = new List<string> { "Pass" };
            foreach (OptSortKey k in MainCols) head.Add(k.ToString());
            if (forward) foreach (OptSortKey k in OutCols) head.Add("Oos" + k);
            foreach (OptParam p in ps) head.Add(CsvField(p.Name));
            head.Add("Note");
            sb.Append(string.Join(",", head)).Append("\r\n");
            foreach (OptResult r in results)
            {
                var f = new List<string> { r.Pass.ToString(CultureInfo.InvariantCulture) };
                foreach (OptSortKey k in MainCols) f.Add(Num(Metric(r, k)));
                if (forward) foreach (OptSortKey k in OutCols) f.Add(Num(OutMetric(r, k)));
                foreach (OptParam p in ps)
                {
                    object v;
                    f.Add(r.Params != null && r.Params.TryGetValue(p.Name, out v) ? CsvField(Convert.ToString(v, CultureInfo.InvariantCulture)) : "");
                }
                f.Add(CsvField(NoteOf(r)));
                sb.Append(string.Join(",", f)).Append("\r\n");
            }
            return sb.ToString();
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v)) return "";
            if (double.IsPositiveInfinity(v)) return "inf";
            if (double.IsNegativeInfinity(v)) return "-inf";
            return v.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        private static string CsvField(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
