using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Runtime.Script;
using TradeApi.Trading;

namespace CtaBacktest
{
    // 策略的一個可調參數（照平台：public 欄位＋InputParameter）
    public sealed class ParamInfo
    {
        public FieldInfo Field;
        public string Name { get { return Field.Name; } }
        public string Label;
        public int Order;
        public InputType Kind;
        public object Default;
        public double? Min, Max, Increment;
        public int? Decimals;
        public List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public Type ValueType { get { return Field.FieldType; } }
    }

    public sealed class StrategyInfo
    {
        public Type Type;
        public string ProjectName;
        public List<ParamInfo> Params = new List<ParamInfo>();
        public override string ToString() { return ProjectName == Type.Name ? Type.Name : ProjectName + "（" + Type.Name + "）"; }
    }

    public sealed class CompileResult
    {
        public bool Ok;
        public string Output = "";
        public List<StrategyInfo> Strategies = new List<StrategyInfo>();
        public string DllPath;   // 編出來的 DLL（自檢交給子程式用，spec.md 13.8）
    }

    public static class StrategyLoader
    {
        static StrategyLoader()
        {
            // 策略 DLL 參考本組件（CtaBacktest）：直接回傳已載入的這一份
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                Assembly self = typeof(StrategyLoader).Assembly;
                return name == self.GetName().Name ? self : null;
            };
        }

        public static string CompilerPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "群益交易王CTA", "roslyn", "csc.exe");
            }
        }

        // 用交易王附的 csc 編譯策略原始檔，參考本回測器的模擬介面
        public static CompileResult Compile(string sourcePath)
        {
            var res = new CompileResult();
            if (!File.Exists(CompilerPath)) { res.Output = "找不到編譯器：" + CompilerPath + "（需要安裝群益交易王 CTA）"; return res; }
            if (!File.Exists(sourcePath)) { res.Output = "找不到策略檔：" + sourcePath; return res; }
            // 模擬介面元件不見時（例如回測器開著時重新建置失敗），編譯會報一堆找不到 Runtime、TradeApi，先講清楚
            if (!File.Exists(typeof(StrategyLoader).Assembly.Location))
            {
                res.Output = "找不到回測器元件 " + typeof(StrategyLoader).Assembly.Location + "。\r\n請關閉回測器，執行 Backtester\\build.cmd 重新建置後再開。";
                return res;
            }

            string outDir = Path.Combine(Path.GetTempPath(), "CtaBacktest");
            Directory.CreateDirectory(outDir);
            foreach (string old in Directory.GetFiles(outDir, "*.dll"))
            {
                try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old); } catch { }
            }
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(sourcePath) + "_" + DateTime.Now.Ticks + ".dll");
            string fw = Path.GetDirectoryName(typeof(object).Assembly.Location);
            string self = typeof(StrategyLoader).Assembly.Location;
            var refs = new[] { self, Path.Combine(fw, "System.dll"), Path.Combine(fw, "System.Core.dll"), Path.Combine(fw, "System.Drawing.dll"),
                               Path.Combine(fw, "System.Windows.Forms.dll"), Path.Combine(fw, "System.Xml.dll"), Path.Combine(fw, "System.Xml.Linq.dll"),
                               Path.Combine(fw, "Microsoft.CSharp.dll"), Path.Combine(fw, "System.Data.dll") };
            var args = new StringBuilder("-nologo -utf8output -noconfig -t:library -optimize+ -nowarn:CS0436,CS1702,CS0649,CS0169,CS0414,CS0168,CS0219,CS0162");
            args.Append(" -out:\"").Append(outPath).Append('"');
            foreach (string r in refs) if (File.Exists(r)) args.Append(" -r:\"").Append(r).Append('"');
            // 換成模擬時鐘後另存（檔名不變，錯誤訊息照原檔名），原檔不動
            string srcDir = Path.Combine(outDir, "src_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(srcDir);
            string compiled = Path.Combine(srcDir, Path.GetFileName(sourcePath));
            File.WriteAllText(compiled, UseSimClock(ReadSource(sourcePath)), new UTF8Encoding(true));
            args.Append(" \"").Append(compiled).Append('"');

            var psi = new ProcessStartInfo(CompilerPath, args.ToString())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                try { Directory.Delete(srcDir, true); } catch { }
                res.Output = o.Trim();
                if (p.ExitCode != 0 || !File.Exists(outPath))
                {
                    res.Output = "編譯失敗（策略用到回測器還沒支援的平台功能，或程式本身有錯）：\r\n" + Explain(res.Output);
                    return res;
                }
            }
            Assembly asm = Assembly.Load(File.ReadAllBytes(outPath));
            res.DllPath = outPath;
            res.Strategies = Discover(asm);
            res.Ok = res.Strategies.Count > 0;
            if (!res.Ok) res.Output = "編譯成功，但檔案裡沒有繼承 StrategyBuilder 的策略類別（指標不能回測）。";
            return res;
        }

        private static readonly System.Text.RegularExpressions.Regex SimClockRx = new System.Text.RegularExpressions.Regex(
            @"(?<![\w.])(?:global::)?(?:System\s*\.\s*)?DateTime\s*\.\s*(UtcNow|Now)\b");

        // 和 csc 一樣：有 BOM 照 BOM，否則合法 UTF-8 就用 UTF-8，不然用系統預設編碼（Big5）
        private static string ReadSource(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return File.ReadAllText(path);
            try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(b); }
        }

        // 把策略讀「現在時間」的地方換成回測模擬時間（spec 4.1）
        public static string UseSimClock(string source)
        {
            // 加括號：插值字串 {DateTime.Now:格式} 裡的 global:: 會被當成格式冒號
            return SimClockRx.Replace(source, m => "(global::TradeApi.CtaBacktestInfo." + m.Groups[1].Value + ")");
        }

        // 把錯誤訊息裡的路徑縮短，並加上中文說明
        private static string Explain(string output)
        {
            var sb = new StringBuilder();
            foreach (string line in output.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0) continue;
                int k = l.IndexOf(".cs(", StringComparison.OrdinalIgnoreCase);
                if (k > 0) { int slash = l.LastIndexOfAny(new[] { '\\', '/' }, k); if (slash >= 0) l = l.Substring(slash + 1); }
                sb.Append(l);
                if (l.Contains("CS0117") || l.Contains("CS1061") || l.Contains("CS0246") || l.Contains("CS0234"))
                    sb.Append("　← 可能是回測器 v0.1 還沒模擬的平台功能");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        public static List<StrategyInfo> Discover(Assembly asm)
        {
            var list = new List<StrategyInfo>();
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (Type t in types.Where(x => typeof(StrategyBuilder).IsAssignableFrom(x) && !x.IsAbstract && x.GetConstructor(Type.EmptyTypes) != null))
            {
                var info = new StrategyInfo { Type = t, ProjectName = t.Name };
                object inst = null;
                try { inst = Activator.CreateInstance(t); } catch { }
                if (inst != null)
                {
                    try { string pn = ((StrategyBuilder)inst).Credentials.ProjectName; if (!string.IsNullOrEmpty(pn)) info.ProjectName = pn; } catch { }
                }
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ip = f.GetCustomAttribute<InputParameterAttribute>();
                    if (ip == null) continue;
                    var pi = new ParamInfo { Field = f, Label = ip.Comment ?? f.Name, Order = ip.OrderNumber, Kind = ip.InputType };
                    try { pi.Default = inst != null ? f.GetValue(inst) : (f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null); } catch { }
                    var sn = f.GetCustomAttribute<SimpleNumericAttribute>();
                    if (sn != null) { pi.Min = sn.Minimum; pi.Max = sn.Maximum; pi.Increment = sn.Increment; pi.Decimals = sn.DecimalPlaces; }
                    foreach (var ci in f.GetCustomAttributes<ComboboxItemAttribute>()) pi.Items.Add(new KeyValuePair<string, object>(ci.Name, ci.Value));
                    info.Params.Add(pi);
                }
                info.Params = info.Params.OrderBy(p => p.Order < 0 ? int.MaxValue : p.Order).ToList();
                list.Add(info);
            }
            return list;
        }
    }
}
