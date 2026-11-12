using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CtaBacktest
{
    // 載入 EvoCode 編好的策略 DLL 的結果（spec.md 13.1）
    public sealed class DllLoadResult
    {
        public bool Ok;
        public string Output = "";                            // 失敗原因或提醒（中文）
        public List<StrategyInfo> Strategies = new List<StrategyInfo>();
        public List<string> Missing = new List<string>();     // 策略用到、模擬介面沒有的平台成員
        public int ClockSites;                                // 換成模擬時鐘的地方
        public string SourcePath;                             // .asl 記的原始碼路徑（讀不到為 null）
        public bool SourceNewer;                              // 原始碼比 DLL 新：要在 EvoCode 重新編譯
    }

    // 載入 EvoCode 的策略 DLL：參考改指模擬介面、DateTime.UtcNow／Now 換模擬時鐘、檢查缺的平台成員（spec.md 13.1）
    public static class DllLoader
    {
        // 模擬介面上次核對過的交易王 API 版本（TradeApi.dll 的產品版本，2026-11-11 核對）
        public const string CheckedPlatformVersion = "1.0.0+5031ff212824701f329a17f113c347a73210fb5f";

        private static readonly string[] PlatformAssemblies = { "TradeApi", "TradeLogic" };

        public static string PlatformFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "群益交易王CTA"); }
        }

        // 預設策略資料夾：My scripts\bin\Strategies（EvoCode 的輸出）
        public static string DefaultStrategyFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "My scripts", "bin", "Strategies"); }
        }

        // 交易王 TradeApi.dll 的產品版本；沒裝交易王回傳 null
        public static string PlatformVersion()
        {
            string p = Path.Combine(PlatformFolder, "TradeApi.dll");
            if (!File.Exists(p)) return null;
            try { return FileVersionInfo.GetVersionInfo(p).ProductVersion; } catch { return null; }
        }

        // 版本說明（給狀態列、日誌）；和核對過的版本不同時加提醒
        public static string PlatformVersionNote()
        {
            string v = PlatformVersion();
            if (v == null) return "找不到交易王的 TradeApi.dll（" + PlatformFolder + "），無法核對平台版本。";
            if (v == CheckedPlatformVersion) return null;
            return "交易王已更新（目前 " + v + "），回測器核對過的是 " + CheckedPlatformVersion + "；結果可能和交易王不同，請留意。";
        }

        // .asl 第一行是 EvoCode 記的原始碼路徑
        public static string SourceOf(string dllPath)
        {
            string asl = Path.ChangeExtension(dllPath, ".asl");
            try
            {
                if (!File.Exists(asl)) return null;
                string first = File.ReadAllText(asl, Encoding.UTF8).Split('\n')[0].Trim().TrimStart('﻿');
                return first.Length > 0 ? first : null;
            }
            catch { return null; }
        }

        public static DllLoadResult Load(string dllPath)
        {
            var res = new DllLoadResult();
            if (!File.Exists(dllPath)) { res.Output = "找不到策略 DLL：" + dllPath; return res; }
            res.SourcePath = SourceOf(dllPath);
            try { res.SourceNewer = res.SourcePath != null && File.Exists(res.SourcePath) && File.GetLastWriteTimeUtc(res.SourcePath) > File.GetLastWriteTimeUtc(dllPath).AddSeconds(2); }
            catch { }

            byte[] bytes;
            try { bytes = Patch(dllPath, res.Missing, out res.ClockSites); }
            catch (Exception ex)
            {
                res.Output = "讀不懂這個 DLL（不是 EvoCode 編出來的 .NET 策略？）：" + ex.Message;
                return res;
            }
            if (res.Missing.Count > 0)
            {
                res.Output = "這支策略用到回測器還沒支援的平台功能，不能回測：\r\n  " + string.Join("\r\n  ", res.Missing.ToArray());
                return res;
            }
            Assembly asm;
            try { asm = Assembly.Load(bytes); }
            catch (Exception ex) { res.Output = "載入策略 DLL 失敗：" + ex.Message; return res; }
            res.Strategies = StrategyLoader.Discover(asm);
            res.Ok = res.Strategies.Count > 0;
            if (!res.Ok) res.Output = "這個 DLL 裡沒有繼承 StrategyBuilder 的策略類別（指標不能回測）。";
            else if (res.SourceNewer) res.Output = "原始碼 " + Path.GetFileName(res.SourcePath) + " 比 DLL 新，請先在 EvoCode 重新編譯；目前用的是舊的 DLL。";
            return res;
        }

        // 在記憶體裡改寫（原檔不動）：平台組件參考改指 CtaBacktest、時鐘替換；missing 收集模擬介面缺的成員
        public static byte[] Patch(string dllPath, List<string> missing, out int clockSites)
        {
            clockSites = 0;
            ModuleDefinition mod;
            using (var src = new MemoryStream(File.ReadAllBytes(dllPath)))   // 讀進記憶體，不鎖住檔案（EvoCode 要能覆寫）
                mod = ModuleDefinition.ReadModule(src);

            Assembly self = typeof(StrategyLoader).Assembly;
            CheckMembers(mod, self, missing);

            var selfRef = new AssemblyNameReference(self.GetName().Name, self.GetName().Version);
            mod.AssemblyReferences.Add(selfRef);
            foreach (TypeReference tr in mod.GetTypeReferences())
            {
                var an = tr.Scope as AssemblyNameReference;
                if (an != null && PlatformAssemblies.Contains(an.Name)) tr.Scope = selfRef;
            }
            foreach (string name in PlatformAssemblies)
            {
                AssemblyNameReference r = mod.AssemblyReferences.FirstOrDefault(x => x.Name == name);
                if (r != null) mod.AssemblyReferences.Remove(r);
            }

            // DateTime.UtcNow／Now → 模擬時鐘（同 spec 4.1，改在 IL 層）
            Type clock = typeof(TradeApi.CtaBacktestInfo);
            MethodReference getUtc = mod.Import(clock.GetProperty("UtcNow").GetGetMethod());
            MethodReference getNow = mod.Import(clock.GetProperty("Now").GetGetMethod());
            foreach (TypeDefinition t in AllTypes(mod.Types))
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (Instruction ins in m.Body.Instructions)
                    {
                        var mr = ins.Operand as MethodReference;
                        if (mr == null || ins.OpCode != OpCodes.Call || mr.DeclaringType.FullName != "System.DateTime" || mr.Parameters.Count != 0) continue;
                        if (mr.Name == "get_UtcNow") { ins.Operand = getUtc; clockSites++; }
                        else if (mr.Name == "get_Now") { ins.Operand = getNow; clockSites++; }
                    }
                }

            var ms = new MemoryStream();
            mod.Write(ms);
            return ms.ToArray();
        }

        // 策略用到的每個平台型別與成員，在模擬介面裡找同名、同參數個數的（spec.md 13.1 相容性檢查）
        private static void CheckMembers(ModuleDefinition mod, Assembly self, List<string> missing)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var seen = new HashSet<string>();
            foreach (TypeReference tr in mod.GetTypeReferences())
            {
                if (!IsPlatform(tr)) continue;
                if (ShimType(self, tr) == null && seen.Add(tr.FullName)) missing.Add("型別 " + tr.FullName);
            }
            foreach (MemberReference mr in mod.GetMemberReferences())
            {
                TypeReference dt = mr.DeclaringType;
                var git = dt as GenericInstanceType;
                if (git != null) dt = git.ElementType;
                if (!IsPlatform(dt)) continue;
                Type t = ShimType(self, dt);
                if (t == null) continue;   // 型別已列為缺
                bool ok;
                var m = mr as MethodReference;
                if (m != null)
                {
                    IEnumerable<MethodBase> cands = m.Name == ".ctor"
                        ? t.GetConstructors(all).Cast<MethodBase>()
                        : t.GetMethods(all).Where(x => x.Name == m.Name).Cast<MethodBase>();
                    ok = cands.Any(x => x.GetParameters().Length == m.Parameters.Count);
                }
                else ok = t.GetField(mr.Name, all) != null;
                if (!ok && seen.Add(mr.FullName)) missing.Add(Describe(mr));
            }
        }

        private static bool IsPlatform(TypeReference tr)
        {
            while (tr.DeclaringType != null) tr = tr.DeclaringType;
            var an = tr.Scope as AssemblyNameReference;
            return an != null && PlatformAssemblies.Contains(an.Name);
        }

        private static Type ShimType(Assembly self, TypeReference tr)
        {
            return self.GetType(tr.FullName.Replace('/', '+'), false);
        }

        // 例「TradeApi.Trading.Position.ChangeQuantity（2 個參數）」
        private static string Describe(MemberReference mr)
        {
            string owner = mr.DeclaringType.FullName.Replace('/', '.');
            var m = mr as MethodReference;
            if (m == null) return owner + "." + mr.Name;
            string name = m.Name == ".ctor" ? "建構式" : m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ? m.Name.Substring(4) + "（屬性）" : m.Name;
            return owner + "." + name + (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") ? "" : "（" + m.Parameters.Count + " 個參數）");
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition t in types)
            {
                yield return t;
                foreach (TypeDefinition x in AllTypes(t.NestedTypes)) yield return x;
            }
        }
    }
}
