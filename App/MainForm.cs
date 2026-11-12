using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CtaBacktest;
using Runtime.Script;

// 自製回測器視窗（仿 MT5 策略測試器）。規格：Backtester\spec.md
namespace BacktesterApp
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 日誌與未處理例外（spec.md 13.6）：要在建立任何視窗之前
            AppLog.Init(TradeApi.CtaBacktestInfo.Version);
            MainForm form = null;
            AppLog.InstallGlobalHandlers(() => form);
            form = new MainForm();
            HangWatch.Start(form);   // 卡住偵測（spec 6.5）
            Application.Run(form);
            AppLog.MarkCleanExit();
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Font BaseFont = new Font("Microsoft JhengHei", 12f);
        private static readonly Font BoldFont = new Font("Microsoft JhengHei", 12f, FontStyle.Bold);
        private static readonly Font HelpFont = new Font("Microsoft JhengHei", 10.5f);

        // 設定頁
        private readonly ComboBox cbFile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox cbStrategy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox tbData = new TextBox();
        // 可輸入：還沒有資料夾的新代號也能打，再按「從交易王更新」
        private readonly ComboBox cbSymbol = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        // 資料橋（spec.md 3.5）
        private readonly Label lbBridge = new Label { AutoSize = true, Font = new Font("Microsoft JhengHei", 10.5f), Margin = new Padding(0, 7, 0, 0), MaximumSize = new Size(640, 0) };
        private readonly Button btBridge = new Button { Text = "從交易王更新", AutoSize = true, Height = 32, BackColor = Color.FromArgb(246, 248, 250) };
        private readonly CheckBox ckAutoBridge = new CheckBox { Text = "回測前自動更新", AutoSize = true, Margin = new Padding(14, 6, 0, 0) };
        private readonly System.Windows.Forms.Timer bridgeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private bool bridgeBusy;
        private readonly ComboBox cbTf = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly DateTimePicker dtFrom = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
        private readonly DateTimePicker dtTo = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
        private readonly Label lbRange = new Label { AutoSize = true, ForeColor = Color.FromArgb(87, 96, 106), Font = new Font("Microsoft JhengHei", 10.5f) };
        private readonly ComboBox cbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        // 位數規則見 spec.md 6.3：整數欄 0 位，其他最多 2 位，不補零；匯率欄是貨幣對報價，位數依貨幣對
        private readonly NumericUpDown nuDeposit = Num(100, 100000000, 0, 1000);
        private readonly NumericUpDown nuLeverage = Num(1, 3000, 2, 1);
        private readonly NumericUpDown nuCommPct = Num(0, 10, 2, 0.01m);
        private readonly NumericUpDown nuCommMin = Num(0, 1000, 2, 1);
        private readonly NumericUpDown nuSpread = Num(0, 100000, 0, 1);
        private readonly NumericUpDown nuComm = Num(0, 10000, 2, 0.5m);
        private readonly NumericUpDown nuContract = Num(1, 100000000, 0, 1);
        private readonly NumericUpDown nuFx = Num(0, 1000000, 5, 0.001m);
        private readonly NumericUpDown nuMinLot = Num(0.01m, 1000, 2, 0.01m);
        private readonly NumericUpDown nuLotStep = Num(0.01m, 1000, 2, 0.01m);
        private readonly NumericUpDown nuStopOut = Num(0, 1000, 0, 10);
        private readonly Label lbFxInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = HelpFont, Margin = new Padding(6, 8, 0, 0) };
        private string fxPair, fxQuote, fxSource;   // 目前商品的換算貨幣對、報價幣別、自動報價來源
        private decimal fxAuto = -1;                // 自動算出的貨幣對報價；-1 = 沒有
        private readonly Label lbLevInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = HelpFont };
        private readonly NumericUpDown nuTimeout = Num(0, 3600, 0, 10);
        private readonly Label lbDllInfo = new Label { AutoSize = true, ForeColor = Color.FromArgb(154, 103, 0), Font = HelpFont, MaximumSize = new Size(640, 0), Visible = false };
        private string currentDll;   // 目前載入的策略 DLL（交給子程式）
        private volatile WorkerClient worker;   // 執行中的子程式（關閉視窗時結束它）
        private bool busy, closeAfterBusy;      // RunBusy 等待中、等待中按了關閉
        private readonly Label lbNotes = new Label { AutoSize = true, ForeColor = Color.FromArgb(87, 96, 106), Font = HelpFont, MaximumSize = new Size(640, 0) };
        // 設定頁頂端提醒條（官方規格差異、缺匯率、沒有資料等）
        private readonly Label lbBanner = new Label { Dock = DockStyle.Top, AutoSize = false, Visible = false, BackColor = Color.FromArgb(255, 248, 197), ForeColor = Color.FromArgb(31, 41, 51), Padding = new Padding(14, 10, 14, 10), BorderStyle = BorderStyle.FixedSingle };
        private Label lbMode, lbSpreadInfo;          // 報價模式、點差欄下方的動態說明
        private double curTick; private int curDigits; private bool curIsFx;   // 目前商品的跳動點、報價位數、是否外匯

        // 其他頁
        private readonly DataGridView grid = new DataGridView();
        private readonly WebBrowser browser = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = true };
        private readonly CurvePanel curve = new CurvePanel { Dock = DockStyle.Fill };
        private readonly TextBox tbLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Microsoft JhengHei", 11f), BackColor = Color.White };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private TabPage pgSettings, pgParams, pgOpt, pgReport, pgChart, pgCurve, pgLog;
        private readonly TextBox tbLogFilter = new TextBox { Width = 260, Font = new Font("Microsoft JhengHei", 11f) };
        private readonly Label lbLogCount = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 6, 0, 0) };
        private List<string> logLines = new List<string>();

        // 依篩選字顯示日誌（不分大小寫）；最多顯示 50000 行，TextBox 太多行會很慢
        private void ShowLog()
        {
            string f = tbLogFilter.Text.Trim();
            var shown = f.Length == 0 ? logLines : logLines.Where(x => x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            tbLog.Text = string.Join("\r\n", shown.Take(50000)) + (shown.Count > 50000 ? "\r\n（只顯示前 50000 行，完整日誌請另存報告）" : "");
            tbLog.SelectionStart = 0;
            tbLog.SelectionLength = 0;
            lbLogCount.Text = f.Length == 0 ? "共 " + logLines.Count.ToString("N0") + " 行（例：輸入「停損」「錯誤」「開倉」）" : "符合 " + shown.Count.ToString("N0") + " / " + logLines.Count.ToString("N0") + " 行";
        }
        private readonly KChartView kchart = new KChartView { Dock = DockStyle.Fill };

        // 底部
        private readonly Button btStart = new Button { Text = "開始回測", Width = 160, Height = 44, BackColor = Color.FromArgb(46, 164, 79), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button btStop = new Button { Text = "停止", Width = 100, Height = 44, Enabled = false };
        private readonly ProgressBar progress = new ProgressBar { Width = 360, Height = 26, Maximum = 1000 };
        private readonly Label lbStatus = new Label { AutoSize = true, Name = "lbStatus", Text = "請選擇策略與商品，然後按「開始回測」。" };

        private readonly Dictionary<string, string> saved = new Dictionary<string, string>();
        private List<StrategyInfo> strategies = new List<StrategyInfo>();
        private SymbolData symbol;
        private BacktestResult lastResult;
        private volatile bool cancel;
        private bool loading;

        private static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CtaBacktest", "last.txt"); }
        }

        private static string DefaultScripts
        {
            get
            {
                string near = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "My scripts", "Macroses"));
                if (Directory.Exists(near)) return near;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "My scripts", "Macroses");
            }
        }

        private static string DefaultData
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "群益交易王CTA", "Export", "HistoryData"); }
        }

        private static NumericUpDown Num(decimal min, decimal max, int dec, decimal inc)
        {
            return new TrimNumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = dec, Increment = inc, Width = 200, ThousandsSeparator = true, TextAlign = HorizontalAlignment.Right };
        }

        // 顯示時去掉多餘的 0（100,000、20、6.67），DecimalPlaces 仍決定最多幾位
        private sealed class TrimNumericUpDown : NumericUpDown
        {
            protected override void UpdateEditText()
            {
                // 照基底做法：使用者正在打字時先解析，設文字時標記 ChangingText，避免文字回頭改掉 Value
                if (UserEdit) ParseEditText();
                string fmt = "#,0" + (DecimalPlaces > 0 ? "." + new string('#', DecimalPlaces) : "");
                ChangingText = true;
                Text = Math.Round(Value, DecimalPlaces, MidpointRounding.AwayFromZero).ToString(fmt, CultureInfo.CurrentCulture);
            }
        }

        public MainForm()
        {
            Text = "自製回測器 v" + TradeApi.CtaBacktestInfo.Version;
            Font = BaseFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1320, 900);
            MinimumSize = new Size(1000, 700);
            BackColor = Color.White;

            BuildSettingsPage();
            BuildParamsPage();
            BuildOptimizePage();
            pgReport = new TabPage("報告");
            var reportBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(6) };
            var btSave = new Button { Text = "另存報告（HTML）…", AutoSize = true, Height = 38 };
            var btOpen = new Button { Text = "用瀏覽器開啟", AutoSize = true, Height = 38 };
            btSave.Click += (s, e) => SaveReport();
            btOpen.Click += (s, e) => OpenInBrowser();
            reportBar.Controls.AddRange(new Control[] { btSave, btOpen });
            pgReport.Controls.Add(browser);
            pgReport.Controls.Add(reportBar);
            pgChart = new TabPage("K 線圖（進出場）");
            pgChart.Controls.Add(kchart);
            pgCurve = new TabPage("資金曲線");
            pgCurve.Controls.Add(curve);
            pgLog = new TabPage("日誌");
            pgLog.Controls.Add(tbLog);
            // 日誌常有幾千行：上方篩選框，只顯示含關鍵字的行
            var logBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
            logBar.Controls.Add(new Label { Text = "只看含有：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            logBar.Controls.Add(tbLogFilter);
            logBar.Controls.Add(lbLogCount);
            pgLog.Controls.Add(logBar);
            // 停止打字 0.3 秒後才篩選，日誌很長時不會每打一個字就卡一下
            var logTimer = new System.Windows.Forms.Timer { Interval = 300 };
            logTimer.Tick += (s, e) => { logTimer.Stop(); ShowLog(); };
            tbLogFilter.TextChanged += (s, e) => { logTimer.Stop(); logTimer.Start(); };
            ShowLog();
            tabs.TabPages.AddRange(new[] { pgSettings, pgParams, pgOpt, pgReport, pgChart, pgCurve, pgLog });
            tabs.Padding = new Point(18, 6);

            // 不換行：狀態文字太長時寧可被右邊截掉，也不要整行掉到看不見的第二行
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(10, 8, 10, 8), BackColor = Color.FromArgb(246, 248, 250), WrapContents = false };
            progress.Width = 220;
            btStart.Font = BoldFont;
            btStart.FlatAppearance.BorderSize = 0;
            // 停用時改淡綠，看得出不能按
            btStart.EnabledChanged += (s, e) => { btStart.BackColor = btStart.Enabled ? Color.FromArgb(46, 164, 79) : Color.FromArgb(168, 213, 181); };
            btStop.FlatStyle = FlatStyle.Flat;
            btStop.BackColor = Color.FromArgb(246, 248, 250);
            btStop.FlatAppearance.BorderColor = Color.FromArgb(208, 215, 222);
            btStop.Margin = new Padding(8, 3, 3, 3);
            lbStatus.Margin = new Padding(12, 12, 0, 0);
            progress.Margin = new Padding(12, 10, 0, 0);
            bottom.Controls.AddRange(new Control[] { btStart, btStop, progress, lbStatus });
            btStart.Click += (s, e) => StartRun();
            btStop.Click += (s, e) => { cancel = true; lbStatus.Text = "正在停止…"; };

            Controls.Add(tabs);
            Controls.Add(bottom);

            browser.DocumentText = "<html><body style=\"font-family:Microsoft JhengHei;text-align:center;padding-top:160px;color:#1f2933\">"
                + "<div style=\"font-size:22px\">還沒有回測結果</div>"
                + "<div style=\"font-size:16px;color:#57606a;margin-top:10px\">設定好策略與商品後，按左下角綠色的「開始回測」。</div></body></html>";
            Load += (s, e) => Initialize();
            // 關閉視窗：回測、最佳化一律停止，子程式直接結束（spec.md 13.3）
            FormClosing += (s, e) =>
            {
                // 正在背景讀資料、載入策略：先不關，讀完自動關（RunBusy）
                if (busy) { e.Cancel = true; closeAfterBusy = true; return; }
                cancel = true;
                var w = worker;
                if (w != null) { AppLog.Info("關閉視窗，結束執行中的子程式"); w.Kill(); }
                SaveSettings();
            };
        }

        // ---------------------------------------------------------------- 設定頁
        private void BuildSettingsPage()
        {
            pgSettings = new TabPage("設定") { AutoScroll = true, BackColor = Color.White };
            // 說明文字放在輸入框正下方，所以只要兩欄：欄位名稱、輸入框＋說明
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(20, 8, 20, 20), Dock = DockStyle.Top };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            cbFile.Width = 420; cbStrategy.Width = 420; tbData.Width = 520; cbSymbol.Width = 260; cbTf.Width = 260; cbMode.Width = 420;
            dtFrom.Width = 200; dtTo.Width = 200;
            cbMode.Items.AddRange(new object[] { "M1 OHLC 模擬報價（最精確，建議）", "本週期 OHLC 模擬報價", "只用開盤價（最快）" });

            var btBrowse = SmallButton("選檔案…");
            var btRecompile = SmallButton("重新載入");
            var btData = SmallButton("選資料夾…");
            var btDefaults = SmallButton("恢復預設值");
            btBrowse.Click += (s, e) => BrowseStrategy();
            btRecompile.Click += (s, e) => { loading = true; RefreshFiles(); loading = false; LoadSelectedDll(); };
            btData.Click += (s, e) => BrowseData();
            btDefaults.Click += (s, e) => ApplyDefaults(true);

            // 回測器不編譯策略：讀 EvoCode 編好的 DLL（spec.md 13.1）
            Section(t, "策略");
            Row(t, "策略 DLL", Flow(cbFile, btBrowse, btRecompile), "在 EvoCode 編譯後的策略（My scripts\\bin\\Strategies）；改了程式碼要先在 EvoCode 重新編譯，再按「重新載入」", lbDllInfo);
            Row(t, "策略類別", cbStrategy, "一個檔案有多個策略時在這裡選");
            Row(t, "策略逾時（秒）", nuTimeout, "策略的一次呼叫超過這麼久沒回來，就當作卡在無窮迴圈並停止回測；0 = 不限");
            Section(t, "資料");
            Row(t, "歷史資料資料夾", Flow(tbData, btData), "交易王的 HistoryBridge／HistoryExporter 寫入的位置");
            Row(t, "商品", cbSymbol, "可直接輸入代號（例 EURUSD），還沒有資料就按下面的「從交易王更新」");
            Row(t, "週期", cbTf, "只列出有資料的週期");
            btBridge.Margin = new Padding(0, 0, 0, 0);
            Row(t, "交易王連線", Flow(btBridge, ckAutoBridge), "在交易王任一張圖掛「HistoryBridge」指標並保持開著；按「從交易王更新」會要這個商品的所選週期（M1 OHLC 模式另加 M1），只增不減", lbBridge);
            btBridge.Click += (s, e) => UpdateFromBridge(null);
            bridgeTimer.Tick += (s, e) => ShowBridgeState();
            Row(t, "開始日期", dtFrom, "開始日之前的 K 棒當作圖上既有歷史（暖機）");
            Row(t, "結束日期", dtTo, "含這一天", lbRange);
            lbMode = Row(t, "報價模式", cbMode, "");
            Section(t, "帳戶與交易成本");
            Row(t, "初始資金（USD）", nuDeposit, "");
            var btLev = SmallButton("更新官方槓桿表");
            btLev.Click += (s, e) => UpdateLeverageTable(btLev);
            Row(t, "槓桿（1:N）", Flow(nuLeverage, btLev), "預設依群益官網「全商品槓桿」表；群益有新商品時按右邊按鈕更新", lbLevInfo);
            ShowLeverageInfo();
            lbSpreadInfo = Row(t, "點差（跳動點）", nuSpread, "");
            nuSpread.ValueChanged += (s, e) => ShowSpreadInfo();
            Row(t, "手續費率（%）", nuCommPct, "名目金額的 %，買賣各收一次；預設用群益官方費率");
            Row(t, "最低單筆手續費", nuCommMin, "群益個股最低 5 美元；外匯、商品、指數為 0");
            Row(t, "每手固定手續費", nuComm, "群益不用這種收法，通常保持 0");
            Section(t, "商品規格");
            Row(t, "合約大小", nuContract, "1 手等於多少單位（例：外匯 100000、黃金 100）");
            Row(t, "匯率（換算成美元）", nuFx, "非美元報價的商品，自動取資料夾裡的貨幣對報價（例 USDJPY）；可自己改", lbFxInfo);
            nuFx.ValueChanged += (s, e) => ShowFxInfo();
            Row(t, "最小手數", nuMinLot, "");
            Row(t, "手數級距", nuLotStep, "");
            Row(t, "強制平倉水位（%）", nuStopOut, "淨值 ÷ 已用保證金低於此值時全部平倉；0 = 只在淨值歸零時");
            Row(t, "", btDefaults, "依商品資料重新填入上面的預設值", lbNotes);
            pgSettings.Controls.Add(t);
            // 提醒條：最後加入的 Dock=Top 控制項排在最上面
            pgSettings.Controls.Add(lbBanner);
            pgSettings.Resize += (s, e) => LayoutBanner();

            // 改回測日期：重算期間內的資料缺口提醒
            dtFrom.ValueChanged += (s, e) => { if (!loading) RefreshBanner(); };
            dtTo.ValueChanged += (s, e) => { if (!loading) RefreshBanner(); };
            cbFile.SelectedIndexChanged += (s, e) => { if (!loading) LoadSelectedDll(); };
            cbStrategy.SelectedIndexChanged += (s, e) => { if (!loading) FillParams(); };
            cbSymbol.SelectedIndexChanged += (s, e) => { if (!loading) LoadSymbol(); };
            cbSymbol.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PickTypedSymbol(); } };
            cbSymbol.Leave += (s, e) => { if (!loading) PickTypedSymbol(); };
            cbTf.SelectedIndexChanged += (s, e) => { if (!loading) ApplyDefaults(false); };
            tbData.Leave += (s, e) => RefreshSymbols();
        }

        private static Button SmallButton(string text) { return new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(246, 248, 250) }; }

        private static FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (Control c in cs) if (!(c is Button)) c.Margin = new Padding(0);
            f.Controls.AddRange(cs);
            return f;
        }

        private static readonly Font SectionFont = new Font("Microsoft JhengHei", 14f, FontStyle.Bold);
        private static readonly Color MutedColor = Color.FromArgb(87, 96, 106);
        private static readonly Color ErrorColor = Color.FromArgb(207, 34, 46);

        // 區塊標題＋一條分隔線
        private static void Section(TableLayoutPanel t, string title)
        {
            var l = new Label { Text = title, Font = SectionFont, AutoSize = true, ForeColor = Color.FromArgb(9, 105, 218), Margin = new Padding(0, 22, 0, 4) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 2);
            var line = new Panel { Height = 1, BackColor = Color.FromArgb(208, 215, 222), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 8) };
            t.Controls.Add(line);
            t.SetColumnSpan(line, 2);
        }

        // 一列設定：左邊欄位名稱，右邊輸入框，說明與附加資訊疊在輸入框下方。回傳說明 Label（可動態改文字）
        private static Label Row(TableLayoutPanel t, string label, Control c, string help, params Label[] extras)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 9, 8, 4) });
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 4, 0, 8) };
            c.Margin = new Padding(0);
            cell.Controls.Add(c);
            var h = new Label { Text = help, AutoSize = true, MaximumSize = new Size(640, 0), Font = HelpFont, ForeColor = MutedColor, Margin = new Padding(1, 3, 0, 0), Visible = help.Length > 0 };
            cell.Controls.Add(h);
            foreach (Label x in extras)
            {
                x.Margin = new Padding(1, 3, 0, 0);
                if (x.MaximumSize.Width == 0) x.MaximumSize = new Size(640, 0);
                cell.Controls.Add(x);
            }
            t.Controls.Add(cell);
            return h;
        }

        // 設定頁頂端的黃色提醒條；沒有提醒時隱藏
        private void SetBanner(string text)
        {
            lbBanner.Text = text ?? "";
            lbBanner.Visible = lbBanner.Text.Length > 0;
            LayoutBanner();
        }

        private void LayoutBanner()
        {
            if (!lbBanner.Visible) return;
            int w = Math.Max(200, pgSettings.ClientSize.Width - lbBanner.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            Size sz = TextRenderer.MeasureText(lbBanner.Text, lbBanner.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak);
            lbBanner.Height = sz.Height + lbBanner.Padding.Vertical + 2;
        }

        // 點差欄下方：換算成價格與 pips
        private void ShowSpreadInfo()
        {
            if (lbSpreadInfo == null) return;
            if (curTick <= 0) { lbSpreadInfo.Text = "買價 = 賣價 + 點差"; lbSpreadInfo.Visible = true; return; }
            double price = (double)nuSpread.Value * curTick;
            string s = "買價 = 賣價 + 點差 = " + price.ToString("0.##########", CultureInfo.InvariantCulture);
            if (curIsFx && (curDigits == 5 || curDigits == 3)) s += "（" + ((double)nuSpread.Value / 10).ToString("0.#", CultureInfo.InvariantCulture) + " pips）";
            lbSpreadInfo.Text = s + "；預設取匯出當下的實際點差";
            lbSpreadInfo.Visible = true;
        }

        // ---------------------------------------------------------------- 參數頁
        private void BuildParamsPage()
        {
            pgParams = new TabPage("策略參數");
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.ColumnHeadersDefaultCellStyle.Font = BoldFont;
            grid.DefaultCellStyle.Font = BaseFont;
            grid.DefaultCellStyle.Padding = new Padding(4);
            grid.ColumnHeadersHeight = 40;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "參數", ReadOnly = true, FillWeight = 42 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "值（可修改）", FillWeight = 20 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Range", HeaderText = "範圍／預設", ReadOnly = true, FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "程式名稱", ReadOnly = true, FillWeight = 16 });
            grid.Columns["Label"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.Columns["Value"].DefaultCellStyle.BackColor = Color.FromArgb(255, 252, 230);
            grid.Columns["Code"].DefaultCellStyle.Font = new Font("Microsoft JhengHei", 10f);
            grid.Columns["Code"].DefaultCellStyle.ForeColor = Color.FromArgb(140, 149, 159);
            grid.Columns["Range"].DefaultCellStyle.ForeColor = MutedColor;
            // 外觀：淡色表頭、細格線、斑馬紋；選取改淡黃，不用系統深藍
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(243, 244, 246);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(31, 41, 51);
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.GridColor = Color.FromArgb(229, 231, 235);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 245, 177);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(31, 41, 51);
            grid.RowTemplate.MinimumHeight = 36;
            // 可修改的「值」欄固定黃底，不被斑馬紋蓋掉（上方說明寫「黃色欄位可以直接修改」）
            grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == grid.Columns["Value"].Index) { e.CellStyle.BackColor = Color.FromArgb(255, 248, 197); e.CellStyle.SelectionBackColor = Color.FromArgb(255, 236, 153); }
            };
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(8) };
            var btReset = new Button { Text = "恢復策略預設值", AutoSize = true, Height = 38 };
            btReset.Click += (s, e) => FillParams(true);
            bar.Controls.Add(btReset);
            bar.Controls.Add(new Label { Text = "黃色欄位可以直接修改；勾選框打勾＝開啟。這些就是交易王參數面板上的參數。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 10, 0, 0) });
            pgParams.Controls.Add(grid);
            pgParams.Controls.Add(bar);
            // 切到這頁時不要預先選取第一格（深色反白看起來像被鎖住）
            // Enter 只在取得焦點時觸發；切分頁時一定會觸發的是 VisibleChanged
            // 表格顯示後自己會選第一格，所以排到顯示完成之後再清
            pgParams.VisibleChanged += (s, e) =>
            {
                if (pgParams.Visible && !grid.IsCurrentCellInEditMode)
                    BeginInvoke((Action)(() => { if (!grid.IsCurrentCellInEditMode) { grid.ClearSelection(); grid.CurrentCell = null; } }));
            };
            // 策略的下單開關（例 EnableOrders）是關的：頁首黃色提醒，免得跑完 0 筆交易
            lbParamHint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 58, Visible = false, BackColor = Color.FromArgb(255, 248, 197),
                ForeColor = Color.FromArgb(154, 103, 0), Font = new Font("Microsoft JhengHei", 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };
            pgParams.Controls.Add(lbParamHint);
            lbParamHint.Cursor = Cursors.Hand;
            lbParamHint.Click += (s, e) =>
            {
                if (paramHintRow < 0 || paramHintRow >= grid.Rows.Count) return;
                grid.FirstDisplayedScrollingRowIndex = Math.Max(0, paramHintRow - 2);
                grid.CurrentCell = grid.Rows[paramHintRow].Cells["Value"];
                grid.Focus();
            };
            grid.CellValueChanged += (s, e) => UpdateParamHint();
        }

        private StrategyInfo CurrentStrategy { get { return cbStrategy.SelectedItem as StrategyInfo; } }

        private void FillParams(bool resetToDefault = false)
        {
            StoreOptParams();   // 範圍表重建前先記下目前的勾選與範圍（spec 12.6）
            grid.Rows.Clear();
            var info = CurrentStrategy;
            if (info == null) { FillOptParams(); return; }
            foreach (ParamInfo p in info.Params)
            {
                int r = grid.Rows.Add();
                var row = grid.Rows[r];
                row.Tag = p;
                // 說明裡若已含英文名（例「ATR 週期（PbAtrPeriod）」）就拿掉，英文名放在「程式名稱」欄
                string lab = p.Label.Replace("（" + p.Name + "）", "").Replace("(" + p.Name + ")", "").Replace(p.Name, "").Trim();
                row.Cells["Label"].Value = lab.Length > 0 ? lab : p.Name;
                row.Cells["Code"].Value = p.Name;
                object val = p.Default;
                string key = "param." + info.Type.FullName + "." + p.Name;
                string sv;
                if (!resetToDefault && saved.TryGetValue(key, out sv))
                {
                    try { val = ParseValue(p, sv); } catch { val = p.Default; }
                }
                if (p.ValueType == typeof(bool))
                {
                    row.Cells["Value"] = new DataGridViewCheckBoxCell();
                    row.Cells["Value"].Value = val is bool && (bool)val;
                }
                else if (p.Items.Count > 0 || p.ValueType.IsEnum)
                {
                    var cell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                    var names = new List<string>();
                    if (p.Items.Count > 0) names.AddRange(p.Items.Select(x => x.Key));
                    else names.AddRange(Enum.GetNames(p.ValueType));
                    cell.Items.AddRange(names.ToArray());
                    row.Cells["Value"] = cell;
                    row.Cells["Value"].Value = DisplayOf(p, val) ?? names.FirstOrDefault();
                }
                else
                {
                    row.Cells["Value"].Value = Convert.ToString(val, CultureInfo.InvariantCulture);
                }
                string range = "";
                if (p.Min.HasValue || p.Max.HasValue) range = (p.Min.HasValue ? p.Min.Value.ToString(CultureInfo.InvariantCulture) : "") + " ～ " + (p.Max.HasValue ? p.Max.Value.ToString(CultureInfo.InvariantCulture) : "");
                string def = p.Default is bool ? ((bool)p.Default ? "開啟" : "關閉") : DisplayOf(p, p.Default) ?? Convert.ToString(p.Default, CultureInfo.InvariantCulture);
                row.Cells["Range"].Value = (range.Length > 0 ? range + "　" : "") + "預設 " + def;
            }
            pgParams.Text = "策略參數（" + info.Params.Count + "）";
            grid.ClearSelection();
            grid.CurrentCell = null;
            UpdateParamHint();
            FillOptParams();
        }

        private Label lbParamHint;
        private int paramHintRow = -1;

        private void UpdateParamHint()
        {
            if (lbParamHint == null) return;
            var off = new List<string>();
            paramHintRow = -1;
            foreach (DataGridViewRow row in grid.Rows)
            {
                var p = row.Tag as ParamInfo;
                if (p == null || p.ValueType != typeof(bool) || !HtmlReport.IsOrderSwitchName(p.Name)) continue;
                bool isOff = !(row.Cells["Value"].Value is bool && (bool)row.Cells["Value"].Value);
                // 沒打勾的下單開關整列標紅，捲到哪都認得出來
                row.DefaultCellStyle.BackColor = isOff ? Color.FromArgb(255, 235, 233) : Color.Empty;
                row.DefaultCellStyle.ForeColor = isOff ? Color.FromArgb(207, 34, 46) : Color.Empty;
                if (!isOff) continue;
                if (paramHintRow < 0) paramHintRow = row.Index;
                off.Add("「" + ParamDisplayName(p.Name) + "」");
            }
            lbParamHint.Text = off.Count == 0 ? "" : "注意：" + string.Join("、", off) + "沒有打勾，策略只記錄訊號、不會下單，回測會是 0 筆交易。（點這裡跳到那一列）\n在這裡打勾只影響回測，不會改到策略檔或交易王；但交易王實盤掛策略時勾了就會真的送單。";
            lbParamHint.Visible = off.Count > 0;
        }

        private static string DisplayOf(ParamInfo p, object v)
        {
            if (v == null) return null;
            if (p.Items.Count > 0)
            {
                foreach (var kv in p.Items) if (Equals(kv.Value, v) || Convert.ToString(kv.Value) == Convert.ToString(v)) return kv.Key;
                return null;
            }
            if (p.ValueType.IsEnum) return v.ToString();
            return null;
        }

        private static object ParseValue(ParamInfo p, string text)
        {
            Type t = p.ValueType;
            if (p.Items.Count > 0)
            {
                foreach (var kv in p.Items) if (kv.Key == text) return Tester.ConvertValue(kv.Value, t);
                throw new FormatException("選項不存在");
            }
            if (t.IsEnum) return Enum.Parse(t, text);
            if (t == typeof(bool)) return bool.Parse(text);
            if (t == typeof(string)) return text;
            if (t == typeof(Color)) return ColorTranslator.FromHtml(text);
            if (t == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture);
            if (t == typeof(TimeSpan)) return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            double d = double.Parse(text.Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
            return Convert.ChangeType(d, t, CultureInfo.InvariantCulture);
        }

        // 讀參數表；有錯回傳 null 並提示
        private Dictionary<string, object> ReadParams(out List<KeyValuePair<string, string>> raw)
        {
            raw = new List<KeyValuePair<string, string>>();
            grid.EndEdit();
            var result = new Dictionary<string, object>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                var p = row.Tag as ParamInfo;
                if (p == null) continue;
                object cell = row.Cells["Value"].Value;
                string text = cell is bool ? ((bool)cell ? "True" : "False") : Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "";
                object v;
                try { v = ParseValue(p, text); }
                catch
                {
                    tabs.SelectedTab = pgParams;
                    grid.CurrentCell = row.Cells["Value"];
                    MessageBox.Show("參數「" + p.Label + "」的值「" + text + "」格式不對，請修改。", "參數錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (v is IConvertible && !(v is bool) && !(v is string) && !p.ValueType.IsEnum && (p.Min.HasValue || p.Max.HasValue))
                {
                    double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    if ((p.Min.HasValue && d < p.Min.Value) || (p.Max.HasValue && d > p.Max.Value))
                    {
                        tabs.SelectedTab = pgParams;
                        grid.CurrentCell = row.Cells["Value"];
                        MessageBox.Show("參數「" + p.Label + "」超出範圍（" + p.Min + " ～ " + p.Max + "）。", "參數錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return null;
                    }
                }
                result[p.Name] = v;
                raw.Add(new KeyValuePair<string, string>(p.Name, text));
            }
            return result;
        }

        // ---------------------------------------------------------------- 參數最佳化頁（spec 12）
        private static readonly string[] OptSortNames = { "淨利", "獲利因子", "期望收益", "淨值最大回撤 %（小的好）", "恢復因子", "夏普比率", "交易筆數", "勝率" };   // 順序同 OptSortKey
        private static readonly Color OptGreen = Color.FromArgb(46, 164, 79);
        private readonly DataGridView gridOptParams = new DataGridView();
        private readonly DataGridView gridOptResults = new DataGridView();
        private readonly ComboBox cbOptMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        private readonly ComboBox cbOptSort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        private readonly NumericUpDown nuOptCount = Num(1, OptSettings.MaxCombinations, 0, 100);
        private readonly NumericUpDown nuOptSeed = Num(0, 999999, 0, 1);
        private readonly NumericUpDown nuOptThreads = Num(1, OptSettings.MaxThreads, 0, 1);
        private readonly CheckBox ckOptForward = new CheckBox { Text = "前進測試", AutoSize = true };
        private readonly NumericUpDown nuOptForwardPct = Num(5, 50, 0, 5);
        private readonly Button btOptStart = new Button { Text = "開始最佳化", Width = 160, Height = 38, BackColor = OptGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button btOptReset = SmallButton("恢復策略預設範圍");
        private readonly Button btOptRerun = new Button { Text = "用這組跑單次回測", AutoSize = true, Height = 38 };
        private readonly Button btOptExport = new Button { Text = "匯出 CSV…", AutoSize = true, Height = 38 };
        private readonly Label lbOptCount = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 34, Font = BoldFont, Padding = new Padding(10, 6, 10, 0) };
        private readonly Label lbOptSummary = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(16, 10, 0, 0) };
        private readonly System.Windows.Forms.Timer optTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private FlowLayoutPanel optBar;
        private SplitContainer optSplit;
        private bool optSplitSet, loadingOpt;
        private string optGridClass;                       // 範圍表目前是哪個策略類別
        private bool optRunning;
        private List<OptResult> optResults = new List<OptResult>();
        private List<OptParam> optRunParams = new List<OptParam>();   // 這次最佳化掃的參數（結果表的參數欄）
        private bool optRunForward;
        private OptSortKey optRunSort;
        private string optRunClass, optRunName, optRunSymbol, optRunTf;
        private readonly Queue<OptResult> optQueue = new Queue<OptResult>();   // 工作執行緒放、畫面計時器取
        private int optDone;                               // 已完成組數（工作執行緒 Interlocked 加）
        private long optTotal;
        private DateTime optStarted;
        private OptResult optBest;
        private int optSortCol = -1;
        private bool optSortDesc;

        // 結果表一欄的意義（放在 Column.Tag）
        private enum OptColKind { Pass, Metric, OutMetric, Param, Note }
        private sealed class OptCol
        {
            public OptColKind Kind;
            public OptSortKey Key;
            public string Param;
        }

        private static Label OptLabel(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(12, 9, 4, 0) }; }

        // 和策略參數頁同樣外觀：淡色表頭、細格線、斑馬紋、淡黃選取
        private static void StyleGrid(DataGridView g)
        {
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.ColumnHeadersDefaultCellStyle.Font = BoldFont;
            g.DefaultCellStyle.Font = BaseFont;
            g.DefaultCellStyle.Padding = new Padding(2);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(243, 244, 246);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(31, 41, 51);
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            g.GridColor = Color.FromArgb(229, 231, 235);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 245, 177);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(31, 41, 51);
            g.RowTemplate.MinimumHeight = 32;
            g.DataError += (s, e) => { e.ThrowException = false; };
        }

        private void BuildOptimizePage()
        {
            pgOpt = new TabPage("參數最佳化") { BackColor = Color.White };
            var help = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 56, Font = HelpFont, ForeColor = Color.DimGray, Padding = new Padding(10, 8, 10, 0),
                Text = "勾選要掃的參數並填起、步、終；沒勾的用「策略參數」頁的值。策略若有寫檔的開關（例：寫出交易記錄 CSV），最佳化前請先關掉。\n"
                     + "跑完後雙擊結果表的一列（或選一列按「用這組跑單次回測」），會用那組參數跑一次單次回測，看完整報告。"
            };
            cbOptMode.Items.AddRange(new object[] { "完整組合（窮舉）", "隨機抽樣（蒙地卡羅）" });
            cbOptSort.Items.AddRange(OptSortNames);
            cbOptMode.SelectedIndex = 0;
            cbOptSort.SelectedIndex = 0;
            nuOptCount.Value = 500;
            nuOptSeed.Value = 1;
            nuOptSeed.ThousandsSeparator = false;
            nuOptThreads.Value = Math.Max(1, Math.Min(OptSettings.MaxThreads, Environment.ProcessorCount - 1));
            nuOptForwardPct.Value = 25;
            nuOptCount.Width = nuOptSeed.Width = 110;
            nuOptThreads.Width = nuOptForwardPct.Width = 70;
            foreach (Control c in new Control[] { cbOptMode, cbOptSort, nuOptCount, nuOptSeed, nuOptThreads, nuOptForwardPct }) c.Margin = new Padding(0, 5, 0, 5);
            ckOptForward.Margin = new Padding(16, 8, 0, 0);
            btOptStart.Font = BoldFont;
            btOptStart.FlatAppearance.BorderSize = 0;
            btOptStart.Margin = new Padding(18, 2, 0, 2);
            btOptStart.EnabledChanged += (s, e) => { btOptStart.BackColor = btOptStart.Enabled ? OptGreen : Color.FromArgb(168, 213, 181); };
            btOptReset.Margin = new Padding(12, 5, 0, 0);
            optBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6, 2, 6, 2) };
            optBar.Controls.AddRange(new Control[] { OptLabel("方式"), cbOptMode, OptLabel("抽樣組數"), nuOptCount, OptLabel("亂數種子"), nuOptSeed,
                OptLabel("同時執行"), nuOptThreads, OptLabel("排序依據"), cbOptSort, ckOptForward, OptLabel("樣本外比例 %"), nuOptForwardPct, btOptStart, btOptReset });
            var lbOptHint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 28, Font = HelpFont, ForeColor = MutedColor, Padding = new Padding(10, 2, 10, 0),
                Text = "組合太多時：先用隨機抽樣 300～1,000 組找出好的區間，再對那個區間用較小的範圍窮舉。預估時間只是參考。" };

            // 上：參數範圍表
            StyleGrid(gridOptParams);
            gridOptParams.SelectionMode = DataGridViewSelectionMode.CellSelect;
            gridOptParams.EditMode = DataGridViewEditMode.EditOnEnter;
            gridOptParams.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            gridOptParams.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "參數", ReadOnly = true, FillWeight = 38 });
            gridOptParams.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "最佳化", FillWeight = 9 });
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Start", HeaderText = "起", FillWeight = 12 });
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Step", HeaderText = "步", FillWeight = 12 });
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stop", HeaderText = "終", FillWeight = 12 });
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "組數", ReadOnly = true, FillWeight = 8 });
            gridOptParams.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "程式名稱", ReadOnly = true, FillWeight = 15 });
            gridOptParams.Columns["Label"].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            gridOptParams.Columns["Count"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            gridOptParams.Columns["Code"].DefaultCellStyle.Font = new Font("Microsoft JhengHei", 10f);
            gridOptParams.Columns["Code"].DefaultCellStyle.ForeColor = Color.FromArgb(140, 149, 159);
            foreach (DataGridViewColumn c in gridOptParams.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            // 可改的起步終欄黃底（同策略參數頁）；唯讀的不上色
            gridOptParams.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                string n = gridOptParams.Columns[e.ColumnIndex].Name;
                if ((n == "Start" || n == "Step" || n == "Stop") && !gridOptParams.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly)
                { e.CellStyle.BackColor = Color.FromArgb(255, 248, 197); e.CellStyle.SelectionBackColor = Color.FromArgb(255, 236, 153); }
            };
            gridOptParams.CurrentCellDirtyStateChanged += (s, e) => { if (gridOptParams.IsCurrentCellDirty && gridOptParams.CurrentCell is DataGridViewCheckBoxCell) gridOptParams.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            gridOptParams.CellValueChanged += (s, e) =>
            {
                if (loadingOpt || e.RowIndex < 0) return;
                string n = gridOptParams.Columns[e.ColumnIndex].Name;
                if (n != "Enabled" && n != "Start" && n != "Step" && n != "Stop") return;
                RecalcOptRow(gridOptParams.Rows[e.RowIndex]);
                UpdateOptCount();
            };

            // 下：結果表
            StyleGrid(gridOptResults);
            gridOptResults.ReadOnly = true;
            gridOptResults.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            gridOptResults.MultiSelect = false;
            gridOptResults.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            gridOptResults.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            gridOptResults.CellFormatting += OptResultFormatting;
            gridOptResults.ColumnHeaderMouseClick += (s, e) =>
            {
                if (optRunning || e.ColumnIndex < 0 || e.Button != MouseButtons.Left) return;
                var c = gridOptResults.Columns[e.ColumnIndex].Tag as OptCol;
                if (c == null) return;
                bool desc = e.ColumnIndex == optSortCol ? !optSortDesc
                    : (c.Kind == OptColKind.Metric || c.Kind == OptColKind.OutMetric) && !Optimizer.SmallerIsBetter(c.Key);
                SortOptResults(e.ColumnIndex, desc);
            };
            gridOptResults.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) RerunSelected(); };

            var resBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(6), WrapContents = false };
            resBar.Controls.AddRange(new Control[] { btOptRerun, btOptExport, lbOptSummary });
            optSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Color.FromArgb(208, 215, 222), SplitterWidth = 6 };
            optSplit.Panel1.BackColor = optSplit.Panel2.BackColor = Color.White;
            optSplit.Panel1.Controls.Add(gridOptParams);
            optSplit.Panel2.Controls.Add(gridOptResults);
            optSplit.Panel2.Controls.Add(resBar);

            // 最後加入的 Dock=Top 排在最上面
            pgOpt.Controls.Add(optSplit);
            pgOpt.Controls.Add(lbOptHint);
            pgOpt.Controls.Add(lbOptCount);
            pgOpt.Controls.Add(optBar);
            pgOpt.Controls.Add(help);
            // 上 40%／下 60%：第一次顯示時才知道實際高度
            pgOpt.VisibleChanged += (s, e) =>
            {
                if (!pgOpt.Visible || optSplitSet) return;
                optSplitSet = true;
                try { optSplit.SplitterDistance = Math.Max(80, (int)(optSplit.Height * 0.4)); } catch { }
            };

            EventHandler recount = (s, e) => { if (!loadingOpt) UpdateOptCount(); };
            cbOptMode.SelectedIndexChanged += recount;
            nuOptCount.ValueChanged += recount;
            nuOptThreads.ValueChanged += recount;
            ckOptForward.CheckedChanged += recount;
            nuOptForwardPct.ValueChanged += recount;
            btOptStart.Click += (s, e) => StartOptimize();
            btOptReset.Click += (s, e) => ResetOptRanges();
            btOptRerun.Click += (s, e) => RerunSelected();
            btOptExport.Click += (s, e) => ExportOptCsv();
            optTimer.Tick += (s, e) => OptTick();
            UpdateOptCount();
        }

        // 範圍表：每個策略參數一列；預設 起＝終＝策略參數頁目前值、步＝Increment（沒有就 1）；有存過的照 last.txt
        private void FillOptParams()
        {
            loadingOpt = true;
            try
            {
                gridOptParams.Rows.Clear();
                var info = CurrentStrategy;
                optGridClass = info == null ? null : info.Type.FullName;
                if (info == null) return;
                foreach (DataGridViewRow prow in grid.Rows)
                {
                    var p = prow.Tag as ParamInfo;
                    if (p == null) continue;
                    int r = gridOptParams.Rows.Add();
                    var row = gridOptParams.Rows[r];
                    row.Tag = p;
                    row.Cells["Label"].Value = prow.Cells["Label"].Value;
                    row.Cells["Code"].Value = p.Name;
                    object cell = prow.Cells["Value"].Value;
                    string cur = cell is bool ? ((bool)cell ? "True" : "False") : Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "";
                    string step = p.Increment.HasValue && p.Increment.Value > 0 ? p.Increment.Value.ToString(CultureInfo.InvariantCulture) : "1";
                    bool en = false;
                    string a = cur, st = step, z = cur, sv;
                    if (saved.TryGetValue("opt." + info.Type.FullName + "." + p.Name, out sv))
                    {
                        string[] parts = sv.Split('|');
                        if (parts.Length == 4)
                        {
                            en = parts[0] == "1";
                            if (p.ValueType != typeof(bool)) { a = parts[1]; st = parts[2]; z = parts[3]; }
                        }
                    }
                    bool can = OptRanges.IsOptimizable(p);
                    row.Cells["Enabled"].Value = en && can;
                    row.Cells["Enabled"].ReadOnly = !can;
                    if (!can)
                    {
                        // 列舉、字串、顏色、日期：不可掃，整列灰字
                        row.Cells["Start"].Value = DisplayOf(p, ParseOrNull(p, cur)) ?? cur;
                        row.Cells["Step"].Value = ""; row.Cells["Stop"].Value = "";
                        row.Cells["Count"].Value = "—";
                        row.DefaultCellStyle.ForeColor = Color.FromArgb(140, 149, 159);
                        row.Cells["Start"].ReadOnly = row.Cells["Step"].ReadOnly = row.Cells["Stop"].ReadOnly = true;
                        row.Cells["Enabled"].ToolTipText = "這種型別不能最佳化，固定用「策略參數」頁的值";
                    }
                    else if (p.ValueType == typeof(bool))
                    {
                        row.Cells["Start"].Value = "關閉"; row.Cells["Step"].Value = "—"; row.Cells["Stop"].Value = "開啟";
                        row.Cells["Start"].ReadOnly = row.Cells["Step"].ReadOnly = row.Cells["Stop"].ReadOnly = true;
                    }
                    else
                    {
                        row.Cells["Start"].Value = a; row.Cells["Step"].Value = st; row.Cells["Stop"].Value = z;
                    }
                    RecalcOptRow(row);
                }
            }
            finally { loadingOpt = false; }
            UpdateOptCount();
        }

        private static object ParseOrNull(ParamInfo p, string text)
        {
            try { return ParseValue(p, text); } catch { return null; }
        }

        // 把範圍表目前的勾選與範圍寫進 saved（只記勾選的參數；沒勾的下次依策略參數頁目前值重新帶入）
        private void StoreOptParams()
        {
            if (optGridClass == null) return;
            foreach (DataGridViewRow row in gridOptParams.Rows)
            {
                var p = row.Tag as ParamInfo;
                if (p == null) continue;
                string k = "opt." + optGridClass + "." + p.Name;
                if (OptRowEnabled(row))
                    saved[k] = "1|" + (p.ValueType == typeof(bool) ? "||" : CellText(row, "Start") + "|" + CellText(row, "Step") + "|" + CellText(row, "Stop"));
                else saved.Remove(k);
            }
        }

        private static string CellText(DataGridViewRow row, string col) { return (Convert.ToString(row.Cells[col].Value, CultureInfo.InvariantCulture) ?? "").Trim().Replace("|", ""); }
        private static bool OptRowEnabled(DataGridViewRow row) { object v = row.Cells["Enabled"].Value; return v is bool && (bool)v; }

        private static bool TryNum(string s, out double v)
        {
            return double.TryParse((s ?? "").Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // 一列的候選值；格式錯回 null 並給錯誤訊息
        private static List<object> ExpandOptRow(DataGridViewRow row, out string err)
        {
            var p = (ParamInfo)row.Tag;
            if (p.ValueType == typeof(bool)) return OptRanges.Expand(p, 0, 0, 0, out err);
            double a, st, z;
            if (!TryNum(CellText(row, "Start"), out a) || !TryNum(CellText(row, "Step"), out st) || !TryNum(CellText(row, "Stop"), out z))
            {
                err = "參數「" + (string.IsNullOrEmpty(p.Label) ? p.Name : p.Label) + "」：起、步、終必須是數字";
                return null;
            }
            return OptRanges.Expand(p, a, st, z, out err);
        }

        // 重算一列的組數；錯誤時顯示「？」並整列淡紅
        private void RecalcOptRow(DataGridViewRow row)
        {
            var p = row.Tag as ParamInfo;
            if (p == null || !OptRanges.IsOptimizable(p)) return;
            bool was = loadingOpt;
            loadingOpt = true;
            try
            {
                if (!OptRowEnabled(row)) { row.Cells["Count"].Value = ""; row.DefaultCellStyle.BackColor = Color.Empty; row.Cells["Count"].ToolTipText = ""; return; }
                string err;
                var vals = ExpandOptRow(row, out err);
                row.Cells["Count"].Value = err != null ? "？" : vals.Count.ToString("N0", CultureInfo.InvariantCulture);
                row.Cells["Count"].ToolTipText = err ?? "";
                row.DefaultCellStyle.BackColor = err != null ? Color.FromArgb(255, 235, 233) : Color.Empty;
            }
            finally { loadingOpt = was; }
        }

        // 讀範圍表成 OptParam 清單；errRow >= 0 表示那一列有錯（err 是訊息）
        private List<OptParam> ReadOptParams(out string err, out int errRow)
        {
            err = null; errRow = -1;
            gridOptParams.EndEdit();
            var list = new List<OptParam>();
            foreach (DataGridViewRow row in gridOptParams.Rows)
            {
                var p = row.Tag as ParamInfo;
                if (p == null || !OptRanges.IsOptimizable(p) || !OptRowEnabled(row)) continue;
                var vals = ExpandOptRow(row, out err);
                if (err != null) { errRow = row.Index; return null; }
                list.Add(new OptParam { Name = p.Name, Label = Convert.ToString(row.Cells["Label"].Value) ?? p.Name, ValueType = p.ValueType, Values = vals });
            }
            return list;
        }

        private OptSettings OptSettingsFromUi()
        {
            return new OptSettings
            {
                Mode = (OptMode)Math.Max(0, cbOptMode.SelectedIndex),
                RandomCount = (int)nuOptCount.Value,
                Seed = (int)nuOptSeed.Value,
                Threads = (int)nuOptThreads.Value,
                Forward = ckOptForward.Checked,
                ForwardPct = (double)nuOptForwardPct.Value / 100.0,
                SortKey = (OptSortKey)Math.Max(0, cbOptSort.SelectedIndex)
            };
        }

        // 每組秒數：開跑前用上次同策略、同商品、同週期的單次回測耗時，沒有就 2 秒
        private double OptSecondsPerPass()
        {
            var info = CurrentStrategy;
            if (lastResult != null && info != null && symbol != null && lastResult.StrategyClass == info.Type.FullName && lastResult.Symbol == symbol.Symbol
                && lastResult.Settings != null && lastResult.Settings.Timeframe == (cbTf.SelectedItem as string) && lastResult.Elapsed.TotalSeconds > 0)
                return Math.Max(0.01, lastResult.Elapsed.TotalSeconds);
            return 2;
        }

        private static double OptEstimate(long run, bool forward, int threads, double secPerPass)
        {
            return run * (forward ? 2 : 1) * secPerPass / Math.Max(1, Math.Min(threads, Math.Max(1, run)));
        }

        private static string Dur(double sec)
        {
            if (double.IsNaN(sec) || double.IsInfinity(sec)) return "—";
            sec = Math.Max(0, Math.Round(sec));
            if (sec < 60) return sec.ToString("0", CultureInfo.InvariantCulture) + " 秒";
            if (sec < 3600) return Math.Floor(sec / 60).ToString("0", CultureInfo.InvariantCulture) + " 分 " + (sec % 60).ToString("0", CultureInfo.InvariantCulture) + " 秒";
            return Math.Floor(sec / 3600).ToString("0", CultureInfo.InvariantCulture) + " 小時 " + Math.Floor(sec % 3600 / 60).ToString("0", CultureInfo.InvariantCulture) + " 分";
        }

        // 組合數列：組合數、實際要跑幾組、預估時間；超過上限紅字
        private void UpdateOptCount()
        {
            bool random = cbOptMode.SelectedIndex == 1;
            nuOptCount.Enabled = nuOptSeed.Enabled = random;
            nuOptForwardPct.Enabled = ckOptForward.Checked;
            var ps = new List<OptParam>();
            bool bad = false;
            foreach (DataGridViewRow row in gridOptParams.Rows)
            {
                var p = row.Tag as ParamInfo;
                if (p == null || !OptRanges.IsOptimizable(p) || !OptRowEnabled(row)) continue;
                string err;
                var vals = ExpandOptRow(row, out err);
                if (err != null) { bad = true; continue; }
                ps.Add(new OptParam { Name = p.Name, Values = vals });
            }
            lbOptCount.ForeColor = Color.FromArgb(31, 41, 51);
            if (bad) { lbOptCount.ForeColor = ErrorColor; lbOptCount.Text = "有參數的起、步、終不正確（組數欄顯示「？」的那一列，滑鼠停在「？」上看原因）"; return; }
            if (ps.Count == 0) { lbOptCount.ForeColor = MutedColor; lbOptCount.Text = "請在下表勾選要最佳化的參數（可多選），並填起、步、終。"; return; }
            long total = OptRanges.CountCombinations(ps);
            if (!random && total > OptSettings.MaxCombinations)
            {
                lbOptCount.ForeColor = ErrorColor;
                lbOptCount.Text = string.Format(CultureInfo.InvariantCulture, "組合數 {0:N0} 超過上限 {1:N0}，請縮小範圍或改用隨機抽樣", total, OptSettings.MaxCombinations);
                return;
            }
            var o = OptSettingsFromUi();
            long run = Optimizer.PlannedCount(total, o);
            double sec = OptSecondsPerPass();
            int th = (int)Math.Min(o.Threads, Math.Max(1, run));
            lbOptCount.Text = string.Format(CultureInfo.InvariantCulture, "組合數 {0:N0}；將跑 {1:N0} 組（{2}）{3}，預估約 {4}（以每組 {5:0.##} 秒、{6} 個執行緒估）",
                total, run, random ? "隨機" : "窮舉", o.Forward ? " × 2 段（前進測試）" : "", Dur(OptEstimate(run, o.Forward, o.Threads, sec)), sec, th);
        }

        private void ResetOptRanges()
        {
            var info = CurrentStrategy;
            if (info == null || optRunning) return;
            string prefix = "opt." + info.Type.FullName + ".";
            foreach (string k in saved.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToList()) saved.Remove(k);
            FillOptParams();
        }

        private void StartOptimize()
        {
            if (bridgeBusy || optRunning || btStop.Enabled) return;
            var info = CurrentStrategy;
            if (info == null) { MessageBox.Show("請先選擇並載入策略 DLL。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            PickTypedSymbol();
            if (symbol == null || cbTf.SelectedItem == null) { MessageBox.Show("請先選擇有資料的商品與週期。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            if (dtTo.Value.Date < dtFrom.Value.Date) { MessageBox.Show("結束日期不能早於開始日期。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            List<KeyValuePair<string, string>> raw;
            var baseParams = ReadParams(out raw);
            if (baseParams == null) return;
            foreach (var kv in raw) saved["param." + info.Type.FullName + "." + kv.Key] = kv.Value;

            string err; int errRow;
            var ops = ReadOptParams(out err, out errRow);
            if (ops == null)
            {
                tabs.SelectedTab = pgOpt;
                if (errRow >= 0 && errRow < gridOptParams.Rows.Count) { gridOptParams.FirstDisplayedScrollingRowIndex = Math.Max(0, errRow - 2); gridOptParams.CurrentCell = gridOptParams.Rows[errRow].Cells["Start"]; }
                MessageBox.Show(err, "參數範圍錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ops.Count == 0) { tabs.SelectedTab = pgOpt; MessageBox.Show("請至少勾選一個要最佳化的參數。", "自製回測器"); return; }
            var o = OptSettingsFromUi();
            long total = OptRanges.CountCombinations(ops);
            if (o.Mode == OptMode.Full && total > OptSettings.MaxCombinations)
            {
                MessageBox.Show(string.Format(CultureInfo.InvariantCulture, "組合數 {0:N0} 超過上限 {1:N0}。\n\n請縮小範圍、加大步距，或改用「隨機抽樣（蒙地卡羅）」。", total, OptSettings.MaxCombinations), "自製回測器");
                return;
            }
            // 缺匯率：最佳化不經資料橋抓，先重讀資料夾一次，仍沒有就請使用者先跑單次回測
            if (fxPair != null && nuFx.Value <= 0) RefreshFx(false);
            string block;
            var s = BuildSettingsFromUi(out block);
            if (s == null) { MessageBox.Show(block, "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            if (o.Forward)
            {
                TesterSettings a, b; DateTime split;
                if (!Optimizer.SplitForward(s, o.ForwardPct, out a, out b, out split)) { MessageBox.Show("回測期間太短，無法切出樣本外。請拉長回測期間或調整樣本外比例。", "自製回測器"); return; }
            }
            long run = Optimizer.PlannedCount(total, o);
            double eta = OptEstimate(run, o.Forward, o.Threads, OptSecondsPerPass());
            if (eta > 3600 && MessageBox.Show("預估要跑約 " + Dur(eta) + "（只是粗估）。\n\n要開始嗎？", "自製回測器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SaveSettings();

            // 開跑：結果表依這次掃的參數重建欄位
            optRunParams = ops;
            optRunForward = o.Forward;
            optRunSort = o.SortKey;
            optRunClass = info.Type.FullName; optRunName = info.ProjectName; optRunSymbol = symbol.Symbol; optRunTf = s.Timeframe;
            optResults = new List<OptResult>();
            lock (optQueue) optQueue.Clear();
            optDone = 0;
            optTotal = run;
            optBest = null;
            BuildOptResultColumns();
            cancel = false;
            optRunning = true;
            SetOptRunning(true);
            optStarted = DateTime.Now;
            progress.Value = 0;
            lbStatus.ForeColor = Color.FromArgb(31, 41, 51); lbStatus.Font = BaseFont;
            lbStatus.Text = "最佳化中：" + info.ProjectName + "　" + symbol.Symbol + " " + s.Timeframe + "，準備資料…";
            UpdateOptSummary();
            // 整個最佳化在子程式裡跑：策略當掉、卡住不會拖垮視窗（spec.md 13.2）
            var job = new WorkerJob { DataFolder = symbol.Folder, DllPath = currentDll, StrategyClass = info.Type.FullName, Parameters = baseParams, Settings = s, OptParams = ops, Opt = o };
            var wc = new WorkerClient { TimeoutSeconds = (int)nuTimeout.Value };
            wc.OptResultReceived = r =>
            {
                lock (optQueue) optQueue.Enqueue(r);
                Interlocked.Increment(ref optDone);
            };
            worker = wc;
            AppLog.Info("最佳化開始：" + Path.GetFileName(currentDll) + " " + info.Type.FullName + " " + symbol.Symbol + " " + s.Timeframe + "，預計 " + run + " 組，同時 " + o.Threads);
            optTimer.Start();
            Task.Run(() => wc.RunOptimize(job, () => cancel))
                .ContinueWith(task =>
                {
                    SafeInvoke(() => { worker = null; FinishOptimize(task.IsFaulted ? null : task.Result, task.Exception, task.IsFaulted ? null : wc.Failure); });
                    try
                    {
                        if (task.IsFaulted) AppLog.Error("最佳化", task.Exception);
                        else AppLog.Info("最佳化結束：" + (wc.Failure ?? (wc.ForcedStop ? "強制停止" : "完成")) + "，" + task.Result.Count + " 組");
                    }
                    catch { }
                });
        }

        // 最佳化期間：設定頁、策略參數頁與最佳化分頁的輸入停用，「停止」啟用；結果表可看可捲
        private void SetOptRunning(bool on)
        {
            btStart.Enabled = !on;
            btStop.Enabled = on;
            pgSettings.Enabled = pgParams.Enabled = !on;
            optBar.Enabled = !on;
            gridOptParams.Enabled = !on;
            btOptRerun.Enabled = btOptExport.Enabled = !on;
        }

        private void BuildOptResultColumns()
        {
            gridOptResults.Rows.Clear();
            gridOptResults.Columns.Clear();
            optSortCol = -1;
            Action<string, OptCol, int, string> add = (title, c, width, tip) =>
            {
                var col = new DataGridViewTextBoxColumn { HeaderText = title, Width = width, Tag = c, SortMode = DataGridViewColumnSortMode.Programmatic, ToolTipText = tip ?? "" };
                if (c.Kind != OptColKind.Note && c.Kind != OptColKind.Param) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                gridOptResults.Columns.Add(col);
            };
            add("序號", new OptCol { Kind = OptColKind.Pass }, 70, "第幾組（產生組合的順序）");
            add("淨利", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.NetProfit }, 120, null);
            add("獲利因子", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.ProfitFactor }, 100, null);
            add("期望收益", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.ExpectedPayoff }, 100, "每筆交易平均損益");
            add("淨值回撤 %", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.EquityDDPct }, 100, "淨值最大回撤 %（小的好）");
            add("交易筆數", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.Trades }, 90, null);
            add("勝率 %", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.WinRate }, 90, null);
            add("恢復因子", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.RecoveryFactor }, 100, "淨利 ÷ 淨值最大回撤");
            add("夏普", new OptCol { Kind = OptColKind.Metric, Key = OptSortKey.Sharpe }, 80, "夏普比率（以交易為單位，未年化）");
            if (optRunForward)
            {
                add("樣本外淨利", new OptCol { Kind = OptColKind.OutMetric, Key = OptSortKey.NetProfit }, 120, "前進測試後段");
                add("樣本外獲利因子", new OptCol { Kind = OptColKind.OutMetric, Key = OptSortKey.ProfitFactor }, 110, "前進測試後段");
                add("樣本外回撤 %", new OptCol { Kind = OptColKind.OutMetric, Key = OptSortKey.EquityDDPct }, 110, "前進測試後段");
                add("樣本外交易筆數", new OptCol { Kind = OptColKind.OutMetric, Key = OptSortKey.Trades }, 110, "前進測試後段");
            }
            foreach (OptParam p in optRunParams) add(p.Label, new OptCol { Kind = OptColKind.Param, Param = p.Name }, 130, p.Name);
            add("備註", new OptCol { Kind = OptColKind.Note }, 320, null);
        }

        private static object OptCellValue(OptCol c, OptResult r)
        {
            switch (c.Kind)
            {
                case OptColKind.Pass: return r.Pass;
                case OptColKind.Metric: return Optimizer.Metric(r, c.Key);
                case OptColKind.OutMetric: return Optimizer.OutMetric(r, c.Key);
                case OptColKind.Param: object v; return r.Params != null && r.Params.TryGetValue(c.Param, out v) ? v : null;
                default: return Optimizer.NoteOf(r);
            }
        }

        private DataGridViewRow MakeOptRow(OptResult r)
        {
            var row = new DataGridViewRow();
            row.CreateCells(gridOptResults);
            foreach (DataGridViewColumn col in gridOptResults.Columns) row.Cells[col.Index].Value = OptCellValue((OptCol)col.Tag, r);
            row.Tag = r;
            return row;
        }

        private static string FmtMetric(OptSortKey k, double v)
        {
            if (double.IsNaN(v)) return "—";
            if (double.IsPositiveInfinity(v)) return "∞";
            return v.ToString(k == OptSortKey.Trades ? "N0" : "N2", CultureInfo.InvariantCulture);
        }

        // 數字格式：沒有值「—」、無限大「∞」、交易筆數整數、其他兩位；淨利正綠負紅；bool 參數寫開啟／關閉
        private void OptResultFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var c = gridOptResults.Columns[e.ColumnIndex].Tag as OptCol;
            if (c == null || e.Value == null) return;
            // 參數欄先處理：double 參數（例 0.0015）照原值顯示，不套指標的兩位小數與紅綠色
            if (c.Kind == OptColKind.Param)
            {
                e.Value = e.Value is bool ? ((bool)e.Value ? "開啟" : "關閉") : Convert.ToString(e.Value, CultureInfo.InvariantCulture);
                e.FormattingApplied = true;
                return;
            }
            if (e.Value is double)
            {
                double v = (double)e.Value;
                e.Value = FmtMetric(c.Key, v);
                e.FormattingApplied = true;
                if (c.Key == OptSortKey.NetProfit && !double.IsNaN(v) && v != 0) e.CellStyle.ForeColor = v > 0 ? Color.FromArgb(26, 127, 55) : ErrorColor;
            }
            else if (e.Value is bool) { e.Value = (bool)e.Value ? "開啟" : "關閉"; e.FormattingApplied = true; }
            else if (c.Kind == OptColKind.Param) { e.Value = Convert.ToString(e.Value, CultureInfo.InvariantCulture); e.FormattingApplied = true; }
        }

        private static int CompareNaNLast(double x, double y, bool desc, int passA, int passB)
        {
            bool nx = double.IsNaN(x), ny = double.IsNaN(y);
            if (nx != ny) return nx ? 1 : -1;
            if (!nx) { int c = x.CompareTo(y); if (c != 0) return desc ? -c : c; }
            return passA.CompareTo(passB);
        }

        private static double ParamNumber(OptResult r, string name)
        {
            object v;
            if (r.Params == null || !r.Params.TryGetValue(name, out v) || v == null) return double.NaN;
            if (v is bool) return (bool)v ? 1 : 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return double.NaN; }
        }

        // 依某一欄排序記憶體中的結果並重填表格；標題顯示 ▲▼
        private void SortOptResults(int colIndex, bool desc)
        {
            if (colIndex < 0 || colIndex >= gridOptResults.Columns.Count) return;
            var c = (OptCol)gridOptResults.Columns[colIndex].Tag;
            Comparison<OptResult> cmp;
            switch (c.Kind)
            {
                case OptColKind.Metric: cmp = (a, b) => Optimizer.Compare(a, b, c.Key, desc); break;
                case OptColKind.OutMetric: cmp = (a, b) => CompareNaNLast(Optimizer.OutMetric(a, c.Key), Optimizer.OutMetric(b, c.Key), desc, a.Pass, b.Pass); break;
                case OptColKind.Param: cmp = (a, b) => CompareNaNLast(ParamNumber(a, c.Param), ParamNumber(b, c.Param), desc, a.Pass, b.Pass); break;
                case OptColKind.Note: cmp = (a, b) => { int k = string.CompareOrdinal(Optimizer.NoteOf(a), Optimizer.NoteOf(b)); return k != 0 ? (desc ? -k : k) : a.Pass.CompareTo(b.Pass); }; break;
                default: cmp = (a, b) => desc ? b.Pass.CompareTo(a.Pass) : a.Pass.CompareTo(b.Pass); break;
            }
            optResults.Sort(cmp);
            optSortCol = colIndex;
            optSortDesc = desc;
            RefillOptResults();
        }

        private void RefillOptResults()
        {
            gridOptResults.SuspendLayout();
            try
            {
                gridOptResults.Rows.Clear();
                if (optResults.Count > 0) gridOptResults.Rows.AddRange(optResults.Select(MakeOptRow).ToArray());
                foreach (DataGridViewColumn col in gridOptResults.Columns)
                    col.HeaderCell.SortGlyphDirection = col.Index != optSortCol ? SortOrder.None : optSortDesc ? SortOrder.Descending : SortOrder.Ascending;
                gridOptResults.ClearSelection();
            }
            finally { gridOptResults.ResumeLayout(); }
        }

        private void UpdateOptSummary()
        {
            int ok = optResults.Count(r => !r.Cancelled && r.Error == null), errs = optResults.Count(r => r.Error != null), can = optResults.Count(r => r.Cancelled);
            lbOptSummary.Text = string.Format(CultureInfo.InvariantCulture, "共 {0:N0} 組，完成 {1:N0}，錯誤 {2:N0}，已中止 {3:N0}", optTotal, ok, errs, can);
        }

        private string BestText(OptResult best, OptSortKey key)
        {
            if (best == null) return "";
            return "，最佳 " + OptSortNames[(int)key].Replace("（小的好）", "") + " " + FmtMetric(key, Optimizer.Metric(best, key)) + "（第 " + best.Pass + " 組）";
        }

        // 每 0.5 秒：從佇列取最多 200 組補進表格，更新進度與狀態列（不在這裡排序）
        private void OptTick()
        {
            var batch = new List<OptResult>();
            lock (optQueue) while (optQueue.Count > 0 && batch.Count < 200) batch.Add(optQueue.Dequeue());
            if (batch.Count > 0)
            {
                optResults.AddRange(batch);
                gridOptResults.Rows.AddRange(batch.Select(MakeOptRow).ToArray());
                bool desc = !Optimizer.SmallerIsBetter(optRunSort);
                foreach (OptResult r in batch)
                    if (!double.IsNaN(Optimizer.Metric(r, optRunSort)) && (optBest == null || Optimizer.Compare(r, optBest, optRunSort, desc) < 0)) optBest = r;
                UpdateOptSummary();
            }
            int done = Volatile.Read(ref optDone);
            if (optTotal > 0) progress.Value = (int)Math.Max(0, Math.Min(1000, done * 1000L / optTotal));
            double el = (DateTime.Now - optStarted).TotalSeconds;
            string eta = done > 0 && done < optTotal ? "，預估剩餘約 " + Dur(el / done * (optTotal - done)) : "";
            lbStatus.Text = (cancel ? "正在停止…" : "最佳化中：") + string.Format(CultureInfo.InvariantCulture, "已完成 {0:N0} / {1:N0} 組", done, optTotal)
                + (cancel ? "" : eta) + BestText(optBest, optRunSort);
        }

        private void FinishOptimize(List<OptResult> list, Exception ex, string failure = null)
        {
            optTimer.Stop();
            lock (optQueue) { optResults.AddRange(optQueue); optQueue.Clear(); }
            if (list != null) optResults = list;   // Run 回傳的是完整結果
            optRunning = false;
            SetOptRunning(false);
            bool desc = !Optimizer.SmallerIsBetter(optRunSort);
            int sortCol = -1;
            foreach (DataGridViewColumn col in gridOptResults.Columns)
            {
                var c = col.Tag as OptCol;
                if (c != null && c.Kind == OptColKind.Metric && c.Key == optRunSort) sortCol = col.Index;
            }
            if (sortCol >= 0) SortOptResults(sortCol, desc); else RefillOptResults();
            optBest = optResults.FirstOrDefault(r => !double.IsNaN(Optimizer.Metric(r, optRunSort)));
            UpdateOptSummary();
            UpdateOptCount();
            double secs = (DateTime.Now - optStarted).TotalSeconds;
            int ok = optResults.Count(r => !r.Cancelled && r.Error == null);
            lbStatus.Font = BoldFont;
            if (list == null)
            {
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "最佳化失敗：" + (ex == null ? "未知錯誤" : ex.GetBaseException().Message);
                return;
            }
            // 子程式中途出事（卡住逾時、當掉、記憶體超過上限）：已完成的組留在表格
            if (failure != null)
            {
                progress.Value = optTotal > 0 ? (int)Math.Min(1000, ok * 1000L / optTotal) : 0;
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "最佳化中途停止（已完成 " + ok.ToString("N0", CultureInfo.InvariantCulture) + " 組留在表格）：" + failure.Split('\r', '\n')[0];
                tabs.SelectedTab = pgOpt;
                ErrorDialog.Show(this, "最佳化中途停止", failure + "\r\n\r\n已完成的 " + ok.ToString("N0", CultureInfo.InvariantCulture) + " 組結果保留在表格裡。", null);
                return;
            }
            bool stopped = cancel || optResults.Any(r => r.Cancelled) || optResults.Count < optTotal;
            string best = optBest == null ? "，沒有任何一組有交易（原因：用「用這組跑單次回測」看日誌）" : BestText(optBest, optRunSort);
            lbStatus.ForeColor = optBest == null ? ErrorColor : Color.FromArgb(26, 127, 55);
            lbStatus.Text = (stopped ? "最佳化已中止：完成 " + ok.ToString("N0", CultureInfo.InvariantCulture) + " / " + optTotal.ToString("N0", CultureInfo.InvariantCulture) + " 組"
                                     : "最佳化完成：" + ok.ToString("N0", CultureInfo.InvariantCulture) + " 組")
                + "，耗時 " + Dur(secs) + best;
            progress.Value = optTotal > 0 ? (int)Math.Min(1000, ok * 1000L / optTotal) : 1000;
            tabs.SelectedTab = pgOpt;
        }

        // 用選取列的參數跑單次回測：寫回策略參數頁，再走既有的「開始回測」（spec 12.5）
        private void RerunSelected()
        {
            if (optRunning) return;
            // 只認真的選取列：沒選時不要默默用第一列
            DataGridViewRow sel = gridOptResults.SelectedRows.Count > 0 ? gridOptResults.SelectedRows[0] : null;
            var r = sel == null ? null : sel.Tag as OptResult;
            if (r == null) { MessageBox.Show("請先在結果表選一列。", "自製回測器"); return; }
            var info = CurrentStrategy;
            if (info == null || info.Type.FullName != optRunClass)
            {
                MessageBox.Show("這份結果是策略「" + optRunName + "」跑的，目前選的是「" + (info == null ? "（無）" : info.ProjectName) + "」。請先換回原策略。", "自製回測器");
                return;
            }
            foreach (DataGridViewRow prow in grid.Rows)
            {
                var p = prow.Tag as ParamInfo;
                object v;
                if (p == null || r.Params == null || !r.Params.TryGetValue(p.Name, out v)) continue;
                if (v is bool) prow.Cells["Value"].Value = (bool)v;
                else prow.Cells["Value"].Value = Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            UpdateParamHint();
            tabs.SelectedTab = pgParams;
            StartRun();
        }

        private void ExportOptCsv()
        {
            if (optRunning) return;
            if (optResults.Count == 0) { MessageBox.Show("還沒有最佳化結果。", "自製回測器"); return; }
            string name = "最佳化_" + optRunName + "_" + optRunSymbol + "_" + optRunTf + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".csv";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            using (var d = new SaveFileDialog { Filter = "CSV 檔 (*.csv)|*.csv", FileName = name })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, Optimizer.ToCsv(optResults, optRunParams, optRunForward), new UTF8Encoding(true)); }
                catch (Exception ex)
                {
                    MessageBox.Show("無法寫入 " + d.FileName + "：" + ex.Message + "\n\n檔案可能正被 Excel 開著，關掉後再試。", "匯出 CSV", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                lbStatus.Font = BaseFont; lbStatus.ForeColor = Color.FromArgb(31, 41, 51);
                lbStatus.Text = "已匯出到 " + d.FileName;
            }
        }

        private void LoadOptSettings()
        {
            cbOptMode.SelectedIndex = Math.Max(0, Math.Min(1, GetInt("opt.mode", 0)));
            nuOptCount.Value = Clamp(nuOptCount, GetDec("opt.count", 500));
            nuOptSeed.Value = Clamp(nuOptSeed, GetDec("opt.seed", 1));
            nuOptThreads.Value = Clamp(nuOptThreads, GetDec("opt.threads", Math.Max(1, Environment.ProcessorCount - 1)));
            cbOptSort.SelectedIndex = Math.Max(0, Math.Min(OptSortNames.Length - 1, GetInt("opt.sort", 0)));
            ckOptForward.Checked = Get("opt.forward", "0") == "1";
            nuOptForwardPct.Value = Clamp(nuOptForwardPct, GetDec("opt.forwardpct", 25));
        }

        private void SaveOptSettings()
        {
            saved["opt.mode"] = cbOptMode.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            saved["opt.count"] = Inv(nuOptCount.Value);
            saved["opt.seed"] = Inv(nuOptSeed.Value);
            saved["opt.threads"] = Inv(nuOptThreads.Value);
            saved["opt.sort"] = cbOptSort.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            saved["opt.forward"] = ckOptForward.Checked ? "1" : "0";
            saved["opt.forwardpct"] = Inv(nuOptForwardPct.Value);
            StoreOptParams();
        }

        // ---------------------------------------------------------------- 載入
        private void Initialize()
        {
            LoadSettings();
            loading = true;
            tbData.Text = Get("data", DefaultData);
            cbMode.SelectedIndex = Math.Max(0, Math.Min(2, GetInt("mode", 0)));
            ckAutoBridge.Checked = Get("autobridge", "0") == "1";
            nuTimeout.Value = Clamp(nuTimeout, GetDec("timeout", 60));
            LoadOptSettings();
            RefreshFiles();
            loading = false;

            // 免責說明：第一次啟動或文字改版後（spec.md 13.5）；不同意就關閉
            if (!DisclaimerForm.EnsureAccepted(this, GetInt("disclaimer.version", 0)))
            {
                AppLog.Info("使用者未同意免責說明，關閉");
                BeginInvoke(new Action(Close));
                return;
            }
            saved["disclaimer.version"] = Disclaimer.Version.ToString(CultureInfo.InvariantCulture);

            RefreshSymbols();
            LoadSelectedDll();
            ShowBridgeState();
            bridgeTimer.Start();
            SaveSettings();
            if (AppLog.PreviousRunCrashed)
                MessageBox.Show(this, "回測器上次沒有正常關閉（可能當掉或被強制結束）。\n\n如果一再發生，請把日誌資料夾裡最近的檔案提供給我們：\n" + AppLog.LogFolder,
                    "自製回測器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- 資料橋（spec.md 3.5）
        private void ShowBridgeState()
        {
            if (bridgeBusy) return;
            Bridge.Heartbeat hb = Bridge.ReadHeartbeat(Bridge.DefaultRoot);
            if (!hb.Online)
            {
                lbBridge.ForeColor = ErrorColor;
                lbBridge.Text = "未連線：交易王沒有掛 HistoryBridge 指標，或交易王沒開"
                    + (hb.Exists ? "（最後回應 " + hb.Utc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "）" : "");
                return;
            }
            string s = "已連線（HistoryBridge v" + hb.Version + (hb.Chart.Length > 0 ? "，掛在 " + hb.Chart + " 圖上" : "") + "）";
            if (hb.Busy.Length > 0) s += "，正在處理一張請求" + (hb.Queue > 1 ? "，另有 " + (hb.Queue - 1) + " 張排隊" : "");
            bool mismatch = hb.DataRoot.Length > 0 && !SamePath(hb.DataRoot, tbData.Text);
            if (mismatch) s += "\n注意：指標寫到 " + hb.DataRoot + "，這裡讀的是上面的「歷史資料資料夾」，更新後會看不到新資料";
            lbBridge.ForeColor = mismatch ? ErrorColor : Color.FromArgb(26, 127, 55);
            lbBridge.Text = s;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // 商品欄打完字：清單裡有就選它；沒有就當新代號，等使用者按「從交易王更新」
        private void PickTypedSymbol()
        {
            string txt = cbSymbol.Text.Trim().ToUpperInvariant();
            if (txt.Length == 0) return;
            string match = cbSymbol.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, txt, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                if (!string.Equals(cbSymbol.SelectedItem as string, match, StringComparison.Ordinal)) cbSymbol.SelectedItem = match;
                return;
            }
            if (symbol == null && cbTf.Items.Count == 0 && lbBanner.Text.Contains("「" + txt + "」")) return;
            loading = true;
            symbol = null;
            cbTf.Items.Clear();
            loading = false;
            lbRange.Text = "";
            lbNotes.Text = "";
            SetBanner("「" + txt + "」還沒有資料。請按「交易王連線」列的「從交易王更新」向交易王索取（交易王要先在任一張圖掛 HistoryBridge 指標）。週期還沒選時會先要 H1" + (cbMode.SelectedIndex == 0 ? " 和 M1" : "") + "。");
        }

        // 向交易王要目前商品的資料；then = 成功後接著做的事（回測前自動更新用）
        // 手動按鈕（then = null）不看新鮮度、只抓新的部分；自動觸發的會略過剛抓過的週期（spec 3.5 不重複抓）
        private void UpdateFromBridge(Action then)
        {
            if (bridgeBusy) return;
            string sym = cbSymbol.Text.Trim().ToUpperInvariant();
            if (sym.Length == 0) { MessageBox.Show("請先在「商品」欄選或輸入商品代號。", "自製回測器"); return; }
            // 新代號還沒有週期可選：用上次的週期，沒有就 H1
            string tf = (symbol != null ? cbTf.SelectedItem as string : null) ?? Get("tf", "H1");
            List<string> tfs = Bridge.PlanTimeframes(tf, cbMode.SelectedIndex == 0);
            var skipped = new List<string>();
            List<Bridge.TfPlan> plans = Bridge.PlanFetch(tbData.Text, sym, tfs, then != null, DateTime.UtcNow, skipped);
            if (plans.Count == 0)
            {
                // 全部剛抓過：不送請求，直接往下走
                lbStatus.Font = BaseFont; lbStatus.ForeColor = Color.FromArgb(31, 41, 51);
                lbStatus.Text = sym + " 資料已是最新，不重抓：" + string.Join("、", skipped);
                AfterUpdate(then);
                return;
            }
            Bridge.Heartbeat hb = Bridge.ReadHeartbeat(Bridge.DefaultRoot);
            if (!hb.Online)
            {
                MessageBox.Show("交易王沒有連線。\n\n請在交易王的 EvoCode 編譯「HistoryBridge.cs」，再到任一張圖加上指標「HistoryBridge」，圖左上角出現「資料橋運作中」後回來再按一次。",
                    "自製回測器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string skipNote = skipped.Count > 0 ? "（略過 " + string.Join("、", skipped) + "）" : "";
            RunBridge(sym, plans, (r, err) => FinishBridge(sym, tf, r, err, then, skipNote));
        }

        // 送一張請求給交易王並鎖住畫面等結果；done 在 UI 執行緒呼叫
        private void RunBridge(string sym, List<Bridge.TfPlan> plans, Action<Bridge.Result, string> done)
        {
            var tfs = plans.Select(p => p.Tf + (p.From.HasValue ? " 從 " + p.From.Value.ToString("MM-dd", CultureInfo.InvariantCulture) : " 全部")).ToList();
            bridgeBusy = true;
            cancel = false;
            btBridge.Enabled = btStart.Enabled = false;
            btStop.Enabled = true;
            pgSettings.Enabled = pgParams.Enabled = false;
            progress.Value = 0;
            lbStatus.ForeColor = Color.FromArgb(31, 41, 51); lbStatus.Font = BaseFont;
            lbStatus.Text = "已送出請求：" + sym + " " + string.Join("、", tfs) + "，等交易王回應…";
            lbBridge.ForeColor = MutedColor;
            lbBridge.Text = "更新中…";
            string root = Bridge.DefaultRoot;
            Task.Run(() =>
            {
                string err;
                Bridge.Result r = Bridge.Run(root, sym, plans, TimeSpan.FromMinutes(15),
                    p => SafeInvoke(() => ShowBridgeProgress(sym, p)), () => cancel, out err);
                return Tuple.Create(r, err);
            }).ContinueWith(task => SafeInvoke(() =>
            {
                bridgeBusy = false;
                btBridge.Enabled = btStart.Enabled = true;
                btStop.Enabled = false;
                pgSettings.Enabled = pgParams.Enabled = true;
                ShowBridgeState();
                done(task.IsFaulted ? null : task.Result.Item1, task.IsFaulted ? task.Exception.GetBaseException().Message : task.Result.Item2);
            }));
        }

        // ---------------------------------------------------------------- 自動匯率（spec 6.2）
        private readonly HashSet<string> fxTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 載入商品時自動抓過的貨幣對，不重複抓

        private bool NeedFxPair { get { return fxPair != null && symbol != null && !string.Equals(fxPair, symbol.Symbol, StringComparison.OrdinalIgnoreCase); } }

        // 載入商品發現缺匯率：交易王有連線就自動抓一次，不用使用者動手
        private void AutoFetchFx()
        {
            if (bridgeBusy || btStop.Enabled || !NeedFxPair || nuFx.Value > 0 || fxTried.Contains(fxPair)) return;
            if (!Bridge.ReadHeartbeat(Bridge.DefaultRoot).Online) return;
            fxTried.Add(fxPair);
            FetchFx(null);
        }

        // 向交易王要換算用貨幣對的 D1（只用最後收盤價，D1 最快），完成後重算匯率；then 只在拿到匯率時執行
        private void FetchFx(Action then)
        {
            string pair = fxPair;
            var skipped = new List<string>();
            List<Bridge.TfPlan> plans = Bridge.PlanFetch(tbData.Text, pair, new List<string> { "D1" }, true, DateTime.UtcNow, skipped);
            if (plans.Count == 0)
            {
                // 貨幣對剛抓過：直接用資料夾裡的
                RefreshFx(true);
                if ((fxPair == null || nuFx.Value > 0) && then != null) then();
                return;
            }
            if (!Bridge.ReadHeartbeat(Bridge.DefaultRoot).Online)
            {
                // 交易王沒連線：資料夾裡已有的照用（RefreshFx 會讀到）
                RefreshFx(true);
                if ((fxPair == null || nuFx.Value > 0) && then != null) then();
                return;
            }
            RunBridge(pair, plans, (r, err) =>
            {
                bool got = r != null && r.Ok && r.Tfs.Any(x => x.Got > 0);
                if (got) Bridge.MarkFetched(Path.Combine(tbData.Text, pair), new[] { "D1" }, DateTime.UtcNow);
                if (got && !cbSymbol.Items.Cast<string>().Any(x => string.Equals(x, pair, StringComparison.OrdinalIgnoreCase)))
                {
                    loading = true;
                    string keep = cbSymbol.SelectedItem as string;
                    var all = cbSymbol.Items.Cast<string>().Concat(new[] { pair }).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    cbSymbol.Items.Clear();
                    foreach (string x in all) cbSymbol.Items.Add(x);
                    if (keep != null) cbSymbol.SelectedItem = keep;
                    loading = false;
                }
                RefreshFx(true);
                bool ok = fxPair == null || nuFx.Value > 0;
                lbStatus.Font = BaseFont;
                lbStatus.ForeColor = ok ? Color.FromArgb(26, 127, 55) : ErrorColor;
                lbStatus.Text = ok ? "已從交易王取得匯率：" + (fxSource ?? pair)
                    : "抓不到 " + pair + " 的匯率：" + (r == null ? err : r.Ok ? string.Join("；", r.Tfs.Select(x => x.Msg)) : r.Msg) + "。可在設定頁「匯率」欄自己填。";
                if (ok && then != null) then();
            });
        }

        // 重讀資料夾算匯率；匯率欄是自動值或空的才換成新值，使用者手動填的不動
        private void RefreshFx(bool replaceAuto)
        {
            if (symbol == null || cbTf.SelectedItem == null) return;
            var notes = new List<string>();
            TesterSettings d = Defaults.For(symbol, (string)cbTf.SelectedItem, notes);
            bool wasAuto = nuFx.Value <= 0 || nuFx.Value == fxAuto;
            fxPair = d.FxPair; fxQuote = d.QuoteCurrency; fxSource = d.FxSource;
            fxAuto = double.IsNaN(d.FxPairPrice) || !(d.FxPairPrice > 0) ? -1 : Clamp(nuFx, (decimal)d.FxPairPrice);
            if (fxPair != null)
            {
                nuFx.Enabled = true;
                nuFx.DecimalPlaces = FxRates.PairDigits(fxPair);
                if (fxAuto > 0 && (replaceAuto && wasAuto || nuFx.Value <= 0)) nuFx.Value = fxAuto;
            }
            ShowFxInfo();
            ShowNotes(notes);
        }

        private void ShowBridgeProgress(string sym, Bridge.Progress p)
        {
            if (!bridgeBusy) return;
            if (p.Count > 0) progress.Value = Math.Max(0, Math.Min(1000, (p.Index - 1) * 1000 / p.Count));
            lbStatus.Text = "交易王更新 " + sym + "：第 " + p.Index + " / " + p.Count + " 個週期"
                + (p.Tf.Length == 0 ? "，準備中…" : " " + p.Tf + "，等平台 " + p.Waited + " 秒" + (p.Bars > 0 ? "（" + p.Bars.ToString("N0", CultureInfo.InvariantCulture) + " 根）" : ""));
        }

        private void FinishBridge(string sym, string tf, Bridge.Result r, string err, Action then, string skipNote)
        {
            if (r == null)
            {
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "從交易王更新失敗：" + (err ?? "未知錯誤");
                return;
            }
            if (!r.Ok)
            {
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "交易王回覆：" + (r.Msg.Length > 0 ? r.Msg : "失敗");
                MessageBox.Show("交易王回覆：" + r.Msg + "\n\n請確認商品代號和交易王裡的一樣（例 EURUSD、XAUUSD、NDAQ100）。", "自製回測器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var parts = new List<string>();
            bool allGot = r.Tfs.Count > 0;
            foreach (Bridge.TfResult x in r.Tfs)
            {
                if (x.Got > 0) parts.Add(x.Tf + " 新增 " + x.Added.ToString("N0", CultureInfo.InvariantCulture) + " 根（共 " + x.Total.ToString("N0", CultureInfo.InvariantCulture) + "）");
                else { parts.Add(x.Tf + " " + x.Msg); allGot = false; }
            }
            progress.Value = 1000;
            lbStatus.Font = BoldFont;
            lbStatus.ForeColor = allGot ? Color.FromArgb(26, 127, 55) : ErrorColor;
            lbStatus.Text = "已從交易王更新 " + sym + "：" + string.Join("；", parts) + skipNote;
            // 記下拿到資料的週期，之後自動觸發時不重抓
            Bridge.MarkFetched(Path.Combine(tbData.Text, sym), r.Tfs.Where(x => x.Got > 0).Select(x => x.Tf), DateTime.UtcNow);
            // 重新讀資料夾，選回這個商品與週期
            saved["symbol"] = sym;
            if (tf != null) saved["tf"] = tf;
            RefreshSymbols();
            AfterUpdate(then);
        }

        // 商品資料更新完（或不用更新）：非美元報價的順便確認匯率，再執行 then
        private void AfterUpdate(Action then)
        {
            if (symbol == null || cbTf.SelectedItem == null) return;
            // 非美元報價：順便更新換算用貨幣對的報價（匯率用回測結束日前最後收盤）
            if (NeedFxPair)
            {
                fxTried.Add(fxPair);
                string done = lbStatus.Text;
                FetchFx(() => { lbStatus.Text = done + "；匯率 " + fxSource; if (then != null) then(); });
                return;
            }
            if (then != null) then();
        }

        private void RefreshFiles()
        {
            cbFile.Items.Clear();
            string dir = Get("dlls", DllLoader.DefaultStrategyFolder);
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x)) cbFile.Items.Add(new FileItem(f));
            string last = Get("dll", "");
            if (last.Length > 0 && File.Exists(last) && !cbFile.Items.Cast<FileItem>().Any(x => x.Path == last)) cbFile.Items.Add(new FileItem(last));
            // 第一次開啟時避開探測、測試用的策略檔，免得一跑就是 0 筆交易
            var files = cbFile.Items.Cast<FileItem>().ToList();
            Func<FileItem, bool> normal = x => x.ToString().IndexOf("Probe", StringComparison.OrdinalIgnoreCase) < 0 && x.ToString().IndexOf("Test", StringComparison.OrdinalIgnoreCase) < 0;
            var pick = files.FirstOrDefault(x => x.Path == last)
                       ?? files.FirstOrDefault(x => normal(x) && x.ToString().IndexOf("Auto", StringComparison.OrdinalIgnoreCase) >= 0)   // 自動交易策略優先
                       ?? files.FirstOrDefault(normal)
                       ?? files.FirstOrDefault();
            if (pick != null) cbFile.SelectedItem = pick;
        }

        private sealed class FileItem
        {
            public readonly string Path;
            public FileItem(string p) { Path = p; }
            public override string ToString() { return System.IO.Path.GetFileName(Path); }
        }

        private void BrowseStrategy()
        {
            using (var d = new OpenFileDialog { Filter = "EvoCode 編譯的策略 (*.dll)|*.dll", InitialDirectory = Get("dlls", DllLoader.DefaultStrategyFolder) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                saved["dlls"] = Path.GetDirectoryName(d.FileName);
                saved["dll"] = d.FileName;
                loading = true;
                RefreshFiles();
                loading = false;
                LoadSelectedDll();
            }
        }

        // 背景執行 work，期間畫面照常重繪（不會變成「沒有回應」），但不接受操作（spec.md 13.3）
        private T RunBusy<T>(string status, Func<T> work)
        {
            var task = Task.Run(work);
            if (task.Wait(150)) return task.Result;
            string oldText = lbStatus.Text; Color oldColor = lbStatus.ForeColor; Font oldFont = lbStatus.Font;
            lbStatus.ForeColor = Color.FromArgb(31, 41, 51); lbStatus.Font = BaseFont;
            lbStatus.Text = status;
            bool tabsOn = tabs.Enabled, startOn = btStart.Enabled;
            tabs.Enabled = false; btStart.Enabled = false; UseWaitCursor = true;
            busy = true;
            try
            {
                while (!task.Wait(50)) Application.DoEvents();
            }
            finally
            {
                busy = false;
                tabs.Enabled = tabsOn; btStart.Enabled = startOn; UseWaitCursor = false;
                if (lbStatus.Text == status) { lbStatus.Text = oldText; lbStatus.ForeColor = oldColor; lbStatus.Font = oldFont; }
                // 等待中按了關閉：等呼叫端把畫面填完再關，設定才不會存成一半新一半舊
                if (closeAfterBusy) BeginInvoke(new Action(Close));
            }
            return task.Result;   // 例外照原樣丟出（包在 AggregateException 裡）
        }

        private void BrowseData()
        {
            using (var d = new FolderBrowserDialog { Description = "選擇 HistoryData 資料夾（裡面每個商品一個子資料夾）", SelectedPath = tbData.Text })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                tbData.Text = d.SelectedPath;
                RefreshSymbols();
            }
        }

        // 載入 EvoCode 編譯的策略 DLL：改寫成接回測器的模擬介面、檢查相容性（spec.md 13.1）
        private void LoadSelectedDll()
        {
            var fi = cbFile.SelectedItem as FileItem;
            loading = true;
            cbStrategy.Items.Clear();
            loading = false;
            strategies = new List<StrategyInfo>();
            currentDll = null;
            lbDllInfo.Visible = false;
            if (fi == null)
            {
                FillParams();
                btStart.Enabled = false;
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "找不到策略 DLL：請先在交易王的 EvoCode 編譯策略（會放在 My scripts\\bin\\Strategies），再按「重新載入」。";
                return;
            }
            saved["dll"] = fi.Path;
            DllLoadResult lr;
            try { lr = RunBusy("正在載入 " + fi + " …", () => DllLoader.Load(fi.Path)); }
            catch (Exception ex)
            {
                lr = new DllLoadResult { Output = "讀取策略 DLL 失敗：" + (ex.InnerException ?? ex).Message };
                AppLog.Error("載入策略 DLL " + fi.Path, ex);
            }
            strategies = lr.Strategies;
            loading = true;
            foreach (var s in strategies) cbStrategy.Items.Add(s);
            string lastClass = Get("class", "");
            var pick = strategies.FirstOrDefault(x => x.Type.FullName == lastClass) ?? strategies.FirstOrDefault();
            if (pick != null) cbStrategy.SelectedItem = pick;
            loading = false;
            FillParams();
            btStart.Enabled = lr.Ok;
            // 提醒：原始碼比 DLL 新、交易王版本和檢查過的不同
            var tips = new List<string>();
            if (lr.SourceNewer) tips.Add("策略原始碼（" + Path.GetFileName(lr.SourcePath) + "）比這個 DLL 新：改過的程式碼還沒在 EvoCode 重新編譯，回測用的是舊版。");
            string ver = DllLoader.PlatformVersionNote();
            if (!string.IsNullOrEmpty(ver)) tips.Add(ver);
            lbDllInfo.Text = string.Join("\n", tips);
            lbDllInfo.Visible = tips.Count > 0;
            if (lr.Ok)
            {
                currentDll = fi.Path;
                AppLog.Info("載入策略 " + fi.Path + "：" + strategies.Count + " 個策略，換時鐘 " + lr.ClockSites + " 處");
                UpdateReadyStatus();
            }
            else
            {
                lbStatus.ForeColor = ErrorColor;
                lbStatus.Text = "策略 DLL 無法使用，詳見日誌分頁。";
                AppLog.Info("載入策略失敗 " + fi.Path + "：" + lr.Output);
                logLines = new List<string>((lr.Output ?? "").Replace("\r\n", "\n").Split('\n'));
                ShowLog();
                tabs.SelectedTab = pgLog;
            }
        }

        private void RefreshSymbols()
        {
            loading = true;
            string last = Get("symbol", "");
            cbSymbol.Items.Clear();
            foreach (string s in SymbolData.ListSymbols(tbData.Text)) cbSymbol.Items.Add(s);
            if (cbSymbol.Items.Count == 0)
            {
                lbNotes.Text = "";
                SetBanner("「歷史資料資料夾」裡還沒有資料。請在交易王任一張圖掛「HistoryBridge」指標，回來在「商品」欄輸入代號，按「從交易王更新」；或按「選資料夾…」指到已匯出的資料夾。");
                symbol = null;
                cbTf.Items.Clear();
                if (last.Length > 0) cbSymbol.Text = last;
                loading = false;
                return;
            }
            string pickSym = cbSymbol.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, last, StringComparison.OrdinalIgnoreCase));
            cbSymbol.SelectedItem = pickSym ?? cbSymbol.Items[0];
            loading = false;
            LoadSymbol();
        }

        private void LoadSymbol()
        {
            string name = cbSymbol.SelectedItem as string;
            if (name == null) return;
            symbol = SymbolData.Open(Path.Combine(tbData.Text, name));
            loading = true;
            string lastTf = Get("tf", "H1");
            cbTf.Items.Clear();
            foreach (string tf in symbol.AvailableTimeframes()) cbTf.Items.Add(tf);
            if (cbTf.Items.Count > 0) cbTf.SelectedItem = cbTf.Items.Contains(lastTf) ? lastTf : cbTf.Items[cbTf.Items.Count > 4 ? 4 : 0];
            loading = false;
            bool sameSymbol = Get("symbol", "") == name && Get("tf", "") == (cbTf.SelectedItem as string);
            ApplyDefaults(false, sameSymbol);
        }

        // 依商品資料填預設；keepSaved = 同一個商品週期時保留使用者上次的設定
        private void ApplyDefaults(bool force, bool keepSaved = false)
        {
            if (symbol == null || cbTf.SelectedItem == null) return;
            string tf = (string)cbTf.SelectedItem;
            var notes = new List<string>();
            // 大檔（M1 可能上百萬根）在背景讀進快取，畫面不卡（spec.md 13.3）；讀失敗留給下面照原本方式處理
            SymbolData sym = symbol;
            try { RunBusy("正在讀取 " + sym.Symbol + " " + tf + " 的歷史資料…", () => { sym.Load(tf); if (sym.HasTimeframe("M1")) sym.Load("M1"); return 0; }); }
            catch (Exception ex) { AppLog.Error("讀取歷史資料 " + sym.Folder + " " + tf, ex); }
            TesterSettings d = Defaults.For(symbol, tf, notes);
            BarSeries series = symbol.Load(tf);
            if (series.Count == 0)
            {
                lbRange.Text = tf + ".csv 裡沒有可讀的 K 棒，請在交易王重新匯出這個週期。";
                SetBanner("這個週期沒有資料，下面的數值是上一個商品留下的，請改選其他週期。");
                fxPair = null; fxAuto = -1; nuFx.Enabled = false; ShowFxInfo();
                return;
            }
            if (series.Count > 0)
            {
                DateTime first = series.Bars[0].Time, last = series.Bars[series.Count - 1].Time;
                lbRange.Text = string.Format("{0} 資料：{1:yyyy-MM-dd} 到 {2:yyyy-MM-dd}，共 {3:N0} 根", tf, first, last, series.Count);
                var m1 = symbol.HasTimeframe("M1") ? symbol.Load("M1") : null;
                bool hasM1 = m1 != null && m1.Count > 0;
                if (hasM1) lbRange.Text += string.Format("\nM1 資料：{0:yyyy-MM-dd} 到 {1:yyyy-MM-dd}", m1.Bars[0].Time, m1.Bars[m1.Count - 1].Time);
                else lbRange.Text += "\n沒有 M1 資料";
                lbMode.Text = hasM1 ? "M1 資料範圍以外的區間會自動改用本週期 OHLC" : "這個商品沒有 M1 資料，選 M1 模式時會自動改用本週期 OHLC";
                lbMode.Visible = true;
                dtFrom.MinDate = dtTo.MinDate = DateTimePicker.MinimumDateTime;
                dtFrom.MaxDate = dtTo.MaxDate = DateTimePicker.MaximumDateTime;
            }
            bool keep = keepSaved && !force;
            dtFrom.Value = keep ? GetDate("from", d.FromUtc) : d.FromUtc;
            dtTo.Value = keep ? GetDate("to", d.ToUtc.AddDays(-1)) : d.ToUtc.AddDays(-1);
            nuDeposit.Value = Clamp(nuDeposit, keep ? GetDec("deposit", (decimal)d.InitialDeposit) : (decimal)d.InitialDeposit);
            nuLeverage.Value = Clamp(nuLeverage, keep ? GetDec("leverage", (decimal)d.Leverage) : (decimal)d.Leverage);
            nuSpread.Value = Clamp(nuSpread, keep ? GetDec("spread", (decimal)Math.Round(d.Spread / d.TickSize)) : (decimal)Math.Round(d.Spread / d.TickSize));
            nuComm.Value = Clamp(nuComm, keep ? GetDec("comm", 0) : 0);
            nuCommPct.Value = Clamp(nuCommPct, keep ? GetDec("commpct", (decimal)d.CommissionPercent) : (decimal)d.CommissionPercent);
            nuCommMin.Value = Clamp(nuCommMin, keep ? GetDec("commmin", (decimal)d.CommissionMin) : (decimal)d.CommissionMin);
            nuContract.Value = Clamp(nuContract, keep ? GetDec("contract", (decimal)d.ContractSize) : (decimal)d.ContractSize);
            // 匯率欄填貨幣對報價；同一個貨幣對才沿用上次手動值
            fxPair = d.FxPair; fxQuote = d.QuoteCurrency; fxSource = d.FxSource;
            fxAuto = double.IsNaN(d.FxPairPrice) ? -1 : (decimal)d.FxPairPrice;
            if (fxPair == null)
            {
                nuFx.DecimalPlaces = 2; nuFx.Value = 1; nuFx.Enabled = false;
            }
            else
            {
                nuFx.Enabled = true;
                nuFx.DecimalPlaces = FxRates.PairDigits(fxPair);
                decimal def = fxAuto > 0 ? fxAuto : 0;
                nuFx.Value = Clamp(nuFx, keep && Get("fxpair", "") == fxPair ? GetDec("fxquote", def) : def);
            }
            ShowFxInfo();
            nuMinLot.Value = Clamp(nuMinLot, keep ? GetDec("minlot", (decimal)d.MinLot) : (decimal)d.MinLot);
            nuLotStep.Value = Clamp(nuLotStep, keep ? GetDec("lotstep", (decimal)d.LotStep) : (decimal)d.LotStep);
            nuStopOut.Value = Clamp(nuStopOut, keep ? GetDec("stopout", (decimal)d.StopOutPercent) : (decimal)d.StopOutPercent);
            curTick = d.TickSize; curDigits = d.PriceDigits;
            curIsFx = symbol.Symbol.Length == 6 && symbol.Symbol.All(char.IsLetter);
            ShowSpreadInfo();
            string q = symbol.QuoteCurrency;
            string info = "商品 " + symbol.Symbol + "：最小跳動點 " + d.TickSize.ToString("0.##########", CultureInfo.InvariantCulture)
                        + "，報價幣別 " + (q ?? "未知");
            string exported; DateTime exUtc;
            if (symbol.Header.TryGetValue("ExportedUtc", out exported))
                info += "，匯出時間 " + (DateTime.TryParse(exported, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out exUtc)
                    ? exUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : exported + " UTC");
            lbNotes.Text = info;
            ShowNotes(notes);
            if (fxPair != null && nuFx.Value <= 0) BeginInvokeSafe(AutoFetchFx);
        }

        // 提醒條 = 商品設定的提醒 ＋ 回測期間內的資料缺口（改日期時重算缺口）
        private List<string> baseNotes = new List<string>();

        private void ShowNotes(List<string> notes)
        {
            baseNotes = notes ?? new List<string>();
            RefreshBanner();
        }

        private void RefreshBanner()
        {
            var all = new List<string>(baseNotes);
            string gap = GapNote();
            if (gap != null) all.Add(gap);
            SetBanner(all.Count > 0 ? "提醒：\n• " + string.Join("\n• ", all) : "");
        }

        // 回測期間內本週期超過 5 天沒有 K 棒的地方（spec 3.2）
        private string GapNote()
        {
            if (symbol == null || cbTf.SelectedItem == null) return null;
            string tf = (string)cbTf.SelectedItem;
            BarSeries s = symbol.Load(tf);
            if (s.Count < 2) return null;
            DateTime from = DateTime.SpecifyKind(dtFrom.Value.Date, DateTimeKind.Utc), to = DateTime.SpecifyKind(dtTo.Value.Date.AddDays(1), DateTimeKind.Utc);
            var gaps = DataQuality.FindGaps(s, from, to);
            if (gaps.Count == 0) return null;
            var parts = gaps.Take(3).Select(g => string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd} → {1:yyyy-MM-dd}（{2:N0} 天）", g.From, g.To, g.Days));
            string list = string.Join("、", parts) + (gaps.Count > 3 ? " 等 " + gaps.Count + " 段" : "");
            DateTime after = gaps[gaps.Count - 1].To.Date.AddDays(1);
            return "回測期間內 " + tf + " 資料有缺口（超過 5 天沒有 K 棒）：" + list + "。缺口期間不會有交易，缺口前後的價差會當成一次跳空，持倉中的單可能用很差的價格停損。"
                 + "要避開就把開始日改到 " + after.ToString("yyyy-MM-dd") + " 以後；要補資料，在交易王對這個商品重新掛 HistoryExporter（v0.2 會自動補缺口）。";
        }

        private static decimal Clamp(NumericUpDown n, decimal v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        // 等目前的事件處理完（匯率狀態更新好）再執行
        private void BeginInvokeSafe(Action a)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(a); else a();
        }

        // 底部狀態：有擋住回測的問題時（缺匯率）直接講還差什麼，不要說「可以開始回測」
        private void UpdateReadyStatus()
        {
            if (btStop.Enabled) return;   // 回測中不動
            var fi = cbFile.SelectedItem as FileItem;
            lbStatus.Font = BaseFont;
            if (fxPair != null && nuFx.Value <= 0)
            {
                lbStatus.ForeColor = Color.FromArgb(154, 103, 0);
                lbStatus.Text = "還差一步：缺 " + fxPair + " 匯率；交易王掛著 HistoryBridge 時，按「開始回測」會自動抓";
            }
            else if (CurrentStrategy != null)
            {
                lbStatus.ForeColor = Color.FromArgb(31, 41, 51);
                lbStatus.Text = "已載入：" + fi + "，可以開始回測。";
            }
        }

        // 匯率欄右邊的說明：用哪個貨幣對、換算結果、報價來源
        private void ShowFxInfo()
        {
            // 只在狀態列是「準備好／還差一步」時跟著更新，不蓋掉回測結果
            if (lbStatus.Text.StartsWith("還差一步") || lbStatus.Text.StartsWith("已載入")) BeginInvokeSafe(UpdateReadyStatus);
            nuFx.BackColor = SystemColors.Window;
            lbFxInfo.Font = HelpFont;
            if (fxPair == null) { lbFxInfo.Text = fxQuote == null ? "" : "美元報價，不用換算"; lbFxInfo.ForeColor = MutedColor; return; }
            if (nuFx.Value <= 0)
            {
                // 缺匯率：欄位標紅、說明加粗，提醒條也會寫
                nuFx.BackColor = Color.FromArgb(255, 235, 233);
                lbFxInfo.Font = new Font(HelpFont, FontStyle.Bold);
                lbFxInfo.Text = "還沒有 " + fxPair + " 的報價。交易王連線時會自動抓（按「開始回測」也會先抓）；沒連線時請在交易王掛 HistoryBridge，或在這裡自己填。";
                lbFxInfo.ForeColor = ErrorColor;
                return;
            }
            lbFxInfo.ForeColor = MutedColor;
            double rate = FxRates.RateFromPair(fxPair, fxQuote, (double)nuFx.Value);
            lbFxInfo.Text = fxPair + " 報價，1 " + fxQuote + " = " + rate.ToString(rate < 0.01 ? "0.######" : "0.####", CultureInfo.InvariantCulture) + " USD"
                + "（" + (nuFx.Value == fxAuto && fxSource != null ? "自動：" + fxSource : "手動填寫") + "）";
        }

        // ---------------------------------------------------------------- 官方槓桿表
        private void ShowLeverageInfo()
        {
            lbLevInfo.Text = LeverageTable.Count > 0
                ? "官方槓桿表：" + LeverageTable.Count + " 項商品，" + (LeverageTable.FetchedAt.Length > 0 ? LeverageTable.FetchedAt + " 抓取" : "抓取時間未知")
                : "找不到官方槓桿表（" + LeverageTable.FilePath + "），請按「更新官方槓桿表」。";
        }

        // 下載官網「全商品槓桿」頁更新 data\official_leverage.csv；頁面看起來不完整時不覆蓋
        private void UpdateLeverageTable(Button bt)
        {
            bt.Enabled = false;
            lbLevInfo.Text = "正在從群益官網下載全商品槓桿表…";
            // 更新前的預設槓桿：目前欄位等於它才自動換新值，使用者手動改過的不覆蓋
            SymbolData sym = symbol;
            string tf = cbTf.SelectedItem as string;
            decimal before = sym != null && tf != null ? Clamp(nuLeverage, (decimal)Defaults.For(sym, tf, null).Leverage) : -1;
            Task.Run(() => LeverageTable.Update()).ContinueWith(task =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new Action(() => AfterLeverageUpdate(bt, task, sym, tf, before))); }
                catch (InvalidOperationException) { }   // 視窗剛好關閉
            });
        }

        private void AfterLeverageUpdate(Button bt, Task<LeverageTable.UpdateResult> task, SymbolData sym, string tf, decimal before)
        {
            {
                bt.Enabled = true;
                ShowLeverageInfo();
                LeverageTable.UpdateResult r = task.IsFaulted ? new LeverageTable.UpdateResult { Error = "更新失敗：" + task.Exception.GetBaseException().Message } : task.Result;
                if (!r.Ok) { MessageBox.Show(r.Error, "更新官方槓桿表", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("更新完成：共 " + r.Total + " 項商品。");
                sb.AppendLine("新增 " + r.Added.Count + "、槓桿調整 " + r.Changed.Count + "、官網已下架 " + r.Removed.Count + "。");
                AppendList(sb, "槓桿調整", r.Changed);
                AppendList(sb, "新增", r.Added);
                AppendList(sb, "官網已下架", r.Removed);
                // 只處理更新前選的同一個商品週期；期間換了商品就由 ApplyDefaults 依新表填
                if (sym != null && sym == symbol && tf == (cbTf.SelectedItem as string))
                {
                    decimal lev = Clamp(nuLeverage, (decimal)Defaults.For(sym, tf, null).Leverage);
                    if (lev != before)
                    {
                        sb.AppendLine();
                        if (nuLeverage.Value == before)
                        {
                            sb.AppendLine("目前商品 " + sym.Symbol + " 的槓桿已從 " + before.ToString("0.##") + " 改為官方新值 " + lev.ToString("0.##") + "。");
                            nuLeverage.Value = lev;
                        }
                        else sb.AppendLine("目前商品 " + sym.Symbol + " 的官方槓桿改為 " + lev.ToString("0.##") + "；你手動設的 " + nuLeverage.Value.ToString("0.##") + " 沒有改動。");
                    }
                }
                MessageBox.Show(sb.ToString(), "更新官方槓桿表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void AppendList(System.Text.StringBuilder sb, string title, List<string> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine(title + "：");
            foreach (string s in items.Take(15)) sb.AppendLine("  " + s);
            if (items.Count > 15) sb.AppendLine("  …另外 " + (items.Count - 15) + " 項");
        }

        // ---------------------------------------------------------------- 執行
        private void StartRun(bool bridgeDone = false)
        {
            if (bridgeBusy) return;
            var info = CurrentStrategy;
            if (info == null) { MessageBox.Show("請先選擇並載入策略 DLL。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            PickTypedSymbol();
            // 回測前自動更新：先向交易王要資料，成功後再回到這裡
            if (ckAutoBridge.Checked && !bridgeDone && cbSymbol.Text.Trim().Length > 0)
            {
                string autoSym = cbSymbol.Text.Trim().ToUpperInvariant();
                string autoTf = (symbol != null ? cbTf.SelectedItem as string : null) ?? Get("tf", "H1");
                bool fresh = Bridge.PlanFetch(tbData.Text, autoSym, Bridge.PlanTimeframes(autoTf, cbMode.SelectedIndex == 0), true, DateTime.UtcNow, null).Count == 0;
                if (fresh || Bridge.ReadHeartbeat(Bridge.DefaultRoot).Online) { UpdateFromBridge(() => StartRun(true)); return; }
                if (symbol != null && MessageBox.Show("交易王沒有連線，這次無法自動更新。\n\n要用現有資料直接回測嗎？", "自製回測器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            if (symbol == null || cbTf.SelectedItem == null) { MessageBox.Show(cbSymbol.Items.Count == 0 || cbSymbol.Text.Trim().Length > 0 && cbSymbol.SelectedItem == null
                ? "這個商品還沒有資料。\n\n請在交易王任一張圖掛「HistoryBridge」指標，再按設定頁「交易王連線」列的「從交易王更新」；或按「選資料夾…」指到已匯出的資料夾。"
                : "請先選擇商品與週期。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            if (dtTo.Value.Date < dtFrom.Value.Date) { MessageBox.Show("結束日期不能早於開始日期。", "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            List<KeyValuePair<string, string>> raw;
            var parameters = ReadParams(out raw);
            if (parameters == null) return;
            foreach (var kv in raw) saved["param." + info.Type.FullName + "." + kv.Key] = kv.Value;

            if (fxPair != null)
            {
                // 資料夾可能剛多了貨幣對：先重讀一次
                if (nuFx.Value <= 0) RefreshFx(false);
                // 還是沒有：交易王有連線就自動抓，抓到後接著回測
                if (nuFx.Value <= 0 && NeedFxPair && Bridge.ReadHeartbeat(Bridge.DefaultRoot).Online)
                {
                    fxTried.Add(fxPair);
                    FetchFx(() => StartRun(true));
                    return;
                }
                if (nuFx.Value <= 0)
                {
                    MessageBox.Show("這個商品用 " + fxQuote + " 報價，損益要換算成美元，需要 " + fxPair + " 的報價，但交易王沒有連線，無法自動抓。\n\n"
                        + "請在交易王任一張圖掛「HistoryBridge」指標，回來再按一次「開始回測」就會自動抓；或在設定頁「匯率」欄直接填 " + fxPair + " 的報價。", "自製回測器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    tabs.SelectedTab = pgSettings;
                    pgSettings.ScrollControlIntoView(nuFx);
                    nuFx.Focus();
                    return;
                }
            }
            string block;
            var s = BuildSettingsFromUi(out block);
            if (s == null) { MessageBox.Show(block, "自製回測器"); tabs.SelectedTab = pgSettings; return; }
            SaveSettings();

            cancel = false;
            btStart.Enabled = false;
            btStop.Enabled = true;
            pgSettings.Enabled = pgParams.Enabled = false;
            progress.Value = 0;
            lbStatus.ForeColor = Color.FromArgb(31, 41, 51); lbStatus.Font = BaseFont;
            lbStatus.Text = "回測中：" + info.ProjectName + "　" + symbol.Symbol + " " + s.Timeframe + " …";
            // 策略在子程式裡跑（spec.md 13.2）：卡住、當掉、吃光記憶體都只結束子程式
            var job = new WorkerJob { DataFolder = symbol.Folder, DllPath = currentDll, StrategyClass = info.Type.FullName, Parameters = parameters, Settings = s };
            var wc = new WorkerClient { TimeoutSeconds = (int)nuTimeout.Value };
            wc.Progress = p => SafeInvoke(() => progress.Value = Math.Max(0, Math.Min(1000, (int)(p * 1000))));
            worker = wc;
            AppLog.Info("回測開始：" + Path.GetFileName(currentDll) + " " + info.Type.FullName + " " + symbol.Symbol + " " + s.Timeframe + " " + s.FromUtc.ToString("yyyy-MM-dd") + "～" + s.ToUtc.ToString("yyyy-MM-dd") + " 模式 " + s.Mode);
            Task.Run(() => wc.RunBacktest(job, () => cancel))
                .ContinueWith(task =>
                {
                    BacktestResult r = task.IsFaulted ? null : task.Result;
                    // 先回畫面，日誌就算出錯也不會讓開始鈕卡在停用
                    SafeInvoke(() => { worker = null; Finish(r, task.Exception, wc.Failure, wc.ForcedStop); });
                    try
                    {
                        if (task.IsFaulted) AppLog.Error("回測", task.Exception);
                        else AppLog.Info("回測結束：" + (r != null ? (r.Cancelled ? "已中止" : "完成") + "，交易 " + r.Trades.Count + " 筆" : wc.Failure ?? (wc.ForcedStop ? "強制停止" : "沒有結果")));
                    }
                    catch { }
                });
        }

        // 從背景執行緒更新畫面；視窗已關閉就略過
        private void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }   // 視窗剛好關閉
        }

        // 由設定頁控制項組回測設定（單次回測與參數最佳化共用）。
        // 需要匯率而匯率欄 <= 0 時回 null 並在 blockReason 說明；匯率自動抓取留在 StartRun（spec 12.3）
        private TesterSettings BuildSettingsFromUi(out string blockReason)
        {
            blockReason = null;
            var s = Defaults.For(symbol, (string)cbTf.SelectedItem, null);
            s.FromUtc = DateTime.SpecifyKind(dtFrom.Value.Date, DateTimeKind.Utc);
            s.ToUtc = DateTime.SpecifyKind(dtTo.Value.Date.AddDays(1), DateTimeKind.Utc);
            s.Mode = (TickMode)cbMode.SelectedIndex;
            s.InitialDeposit = (double)nuDeposit.Value;
            s.Leverage = (double)nuLeverage.Value;
            s.Spread = (double)nuSpread.Value * s.TickSize;
            s.CommissionPerLot = (double)nuComm.Value;
            s.CommissionPercent = (double)nuCommPct.Value;
            s.CommissionMin = (double)nuCommMin.Value;
            s.ContractSize = (double)nuContract.Value;
            if (fxPair != null)
            {
                if (nuFx.Value <= 0)
                {
                    blockReason = "這個商品用 " + fxQuote + " 報價，需要 " + fxPair + " 的報價才能把損益換算成美元，但設定頁的「匯率」欄是空的。\n\n請先跑一次單次回測（交易王連線時會自動抓匯率），或在設定頁「匯率」欄直接填 " + fxPair + " 的報價。";
                    return null;
                }
                s.FxPairPrice = (double)nuFx.Value;
                s.FxRate = FxRates.RateFromPair(fxPair, fxQuote, s.FxPairPrice);
                s.FxSource = nuFx.Value == fxAuto ? fxSource : "手動填寫";
            }
            s.MinLot = (double)nuMinLot.Value;
            s.LotStep = (double)nuLotStep.Value;
            s.StopOutPercent = (double)nuStopOut.Value;
            return s;
        }

        private void Finish(BacktestResult r, Exception ex, string failure = null, bool forcedStop = false)
        {
            btStart.Enabled = true;
            btStop.Enabled = false;
            pgSettings.Enabled = pgParams.Enabled = true;
            if (r == null)
            {
                lbStatus.Font = BoldFont;
                lbStatus.ForeColor = forcedStop ? Color.FromArgb(154, 103, 0) : ErrorColor;
                if (forcedStop) lbStatus.Text = "已停止：策略沒有在時限內回應停止，已直接結束回測（這次沒有報告）。";
                else if (failure != null)
                {
                    lbStatus.Text = "回測沒有完成：" + failure.Split('\r', '\n')[0];
                    ErrorDialog.Show(this, "回測沒有完成", failure, null);
                }
                else
                {
                    lbStatus.Text = "回測失敗：" + (ex == null ? "未知錯誤" : ex.GetBaseException().Message);
                    if (ex != null) ErrorDialog.Show(this, "回測失敗", "回測失敗：" + ex.GetBaseException().Message, ex);
                }
                return;
            }
            lastResult = r;
            var st = Stats.From(r);
            browser.DocumentText = HtmlReport.Build(r, false);
            curve.SetData(r.Curve, r.Settings.InitialDeposit, st.Trades);
            logLines = new List<string>(r.Log);
            // K 線圖失敗的訊息要在設定日誌之後加，不然會被蓋掉
            try { kchart.SetData(symbol == null ? null : ChartData.Build(symbol.Load(r.Settings.Timeframe), r), NoTradeReason(r), r); }
            catch (Exception kex) { kchart.SetData(null); logLines.Insert(0, "K 線圖畫不出來：" + kex.Message); }
            ShowLog();
            pgLog.Text = "日誌（" + r.Log.Count + "）";
            tabs.SelectedTab = pgReport;
            string secs = r.Elapsed.TotalSeconds < 0.1 ? "不到 0.1" : r.Elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture);
            lbStatus.Text = r.Error != null ? "回測錯誤：" + r.Error
                : st.Trades == 0 ? (r.Cancelled ? "已中止" : "完成") + "：沒有任何交易，" + NoTradeReason(r)
                : string.Format(CultureInfo.InvariantCulture, "{0}：交易 {1} 筆，淨利 {2:N2}，獲利因子 {3:N2}，最大回撤 {4:N2}%，耗時 {5} 秒",
                    r.Cancelled ? "已中止" : "完成", st.Trades, st.NetProfit, st.ProfitFactor, st.EquityDDPct, secs);
            lbStatus.ForeColor = r.Error != null || st.NetProfit < 0 ? ErrorColor : st.NetProfit > 0 ? Color.FromArgb(26, 127, 55) : Color.FromArgb(31, 41, 51);
            lbStatus.Font = BoldFont;
            progress.Value = 1000;
        }

        // 0 筆交易的原因：下單開關關著就直接講，否則請使用者看日誌
        private string NoTradeReason(BacktestResult r)
        {
            var off = HtmlReport.OrderSwitchesOff(r).Select(n => "「" + ParamDisplayName(n) + "」").ToList();
            return off.Count > 0 ? string.Join("、", off) + "沒打勾：到「策略參數」頁打勾後再跑一次" : "原因請看「日誌」分頁";
        }

        // 參數的中文名稱（說明括號前那段）；找不到就用程式名稱
        private string ParamDisplayName(string name)
        {
            var info = CurrentStrategy;
            var p = info == null ? null : info.Params.FirstOrDefault(x => x.Name == name);
            if (p == null || string.IsNullOrEmpty(p.Label)) return name;
            string lab = p.Label.Replace("（" + p.Name + "）", "").Replace("(" + p.Name + ")", "").Replace(p.Name, "").Trim();
            int cut = lab.IndexOfAny(new[] { '（', '(' });
            lab = cut > 0 ? lab.Substring(0, cut).Trim() : lab;
            return lab.Length > 0 ? lab : name;
        }

        private void SaveReport()
        {
            if (lastResult == null) { MessageBox.Show("還沒有回測結果。", "自製回測器"); return; }
            string name = "回測報告_" + lastResult.StrategyName + "_" + lastResult.Symbol + "_" + lastResult.Settings.Timeframe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".html";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            using (var d = new SaveFileDialog { Filter = "HTML 報告 (*.html)|*.html", FileName = name })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, HtmlReport.Build(lastResult, true), new UTF8Encoding(true));
                lbStatus.Text = "報告已存到 " + d.FileName;
            }
        }

        private void OpenInBrowser()
        {
            if (lastResult == null) { MessageBox.Show("還沒有回測結果。", "自製回測器"); return; }
            string path = Path.Combine(Path.GetTempPath(), "CtaBacktest", "last_report.html");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, HtmlReport.Build(lastResult, true), new UTF8Encoding(true));
            System.Diagnostics.Process.Start(path);
        }

        // ---------------------------------------------------------------- 記住上次設定
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) saved[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                saved["data"] = tbData.Text;
                if (cbSymbol.SelectedItem != null) saved["symbol"] = (string)cbSymbol.SelectedItem;
                if (cbTf.SelectedItem != null) saved["tf"] = (string)cbTf.SelectedItem;
                if (CurrentStrategy != null) saved["class"] = CurrentStrategy.Type.FullName;
                saved["mode"] = cbMode.SelectedIndex.ToString(CultureInfo.InvariantCulture);
                saved["autobridge"] = ckAutoBridge.Checked ? "1" : "0";
                saved["timeout"] = Inv(nuTimeout.Value);
                // 沒有載入商品時，下面的欄位只是空白初值，不覆蓋上次存的設定
                if (symbol != null)
                {
                saved["from"] = dtFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                saved["to"] = dtTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                saved["deposit"] = Inv(nuDeposit.Value); saved["leverage"] = Inv(nuLeverage.Value); saved["spread"] = Inv(nuSpread.Value);
                saved["comm"] = Inv(nuComm.Value); saved["commpct"] = Inv(nuCommPct.Value); saved["commmin"] = Inv(nuCommMin.Value); saved["contract"] = Inv(nuContract.Value);
                // 只存手動填的匯率；自動值不存，下次開啟會取新匯出的報價
                saved.Remove("fx"); saved["fxpair"] = fxPair ?? ""; saved["fxquote"] = nuFx.Value != fxAuto ? Inv(nuFx.Value) : "";
                saved["minlot"] = Inv(nuMinLot.Value); saved["lotstep"] = Inv(nuLotStep.Value); saved["stopout"] = Inv(nuStopOut.Value);
                }
                SaveOptSettings();
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, saved.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(true));
            }
            catch { }
        }

        private static string Inv(decimal v) { return v.ToString(CultureInfo.InvariantCulture); }
        private string Get(string k, string def) { string v; return saved.TryGetValue(k, out v) && v.Length > 0 ? v : def; }
        private int GetInt(string k, int def) { int v; return int.TryParse(Get(k, ""), out v) ? v : def; }
        private decimal GetDec(string k, decimal def) { decimal v; return decimal.TryParse(Get(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def; }
        private DateTime GetDate(string k, DateTime def)
        {
            DateTime v;
            return DateTime.TryParseExact(Get(k, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out v) ? v : def;
        }
    }

    // 資金曲線（GDI 繪製，不依賴瀏覽器）
    internal sealed class CurvePanel : Panel
    {
        // 文字對齊設定共用一份，不在每次重繪時新建
        private static readonly StringFormat CenterFmt = new StringFormat { Alignment = StringAlignment.Center };
        private static readonly StringFormat RightFmt = new StringFormat { Alignment = StringAlignment.Far };
        private List<EquityPoint> pts = new List<EquityPoint>();
        private double initial;

        public CurvePanel() { DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true; }

        private int trades = -1;   // -1 = 還沒跑過
        private double normalGap;  // 分鐘

        public void SetData(List<EquityPoint> curve, double init, int tradeCount)
        {
            pts = curve ?? new List<EquityPoint>();
            initial = init;
            trades = tradeCount;
            // 一般相鄰兩點的時間間隔（中位數），用來判斷休市；只算一次，重繪時不再排序
            var gaps = new List<double>();
            for (int i = 1; i < pts.Count; i++) gaps.Add((pts[i].Time - pts[i - 1].Time).TotalMinutes);
            gaps.Sort();
            normalGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
            Invalidate();
        }

        private static readonly Color Muted = Color.FromArgb(87, 96, 106);

        // 置中顯示兩行說明（空狀態）
        private void DrawCenter(Graphics g, string title, string sub)
        {
            using (var f1 = new Font("Microsoft JhengHei", 16f))
            using (var f2 = new Font("Microsoft JhengHei", 12f))
            using (var b = new SolidBrush(Muted))
            {
                var fmt = CenterFmt;
                float h1 = g.MeasureString(title, f1).Height;
                float y = Height / 2f - h1;
                g.DrawString(title, f1, Brushes.Black, new RectangleF(0, y, Width, h1 + 4), fmt);
                g.DrawString(sub, f2, b, new RectangleF(20, y + h1 + 8, Width - 40, 80), fmt);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = g.DpiX / 96f;
            if (pts.Count < 2)
            {
                DrawCenter(g, "還沒有回測結果", "設定好策略與商品後，按左下角綠色的「開始回測」。");
                return;
            }
            using (var font = new Font("Microsoft JhengHei", 11f))
            using (var muted = new SolidBrush(Muted))
            {
                int left = (int)(110 * k), top = (int)(44 * k), right = (int)(24 * k), bottom = (int)(44 * k);
                var area = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
                double min = initial, max = initial;
                foreach (var p in pts) { min = Math.Min(min, Math.Min(p.Balance, p.Equity)); max = Math.Max(max, Math.Max(p.Balance, p.Equity)); }
                int dec;
                List<double> ticks = ChartAxis.NiceTicks(ref min, ref max, 5, out dec);
                Func<double, float> Y = v => (float)(area.Bottom - (v - min) / (max - min) * area.Height);
                // 橫軸依時間比例（不是依資料點順序）
                DateTime t0 = pts[0].Time, t1 = pts[pts.Count - 1].Time;
                double span = Math.Max(1, (t1 - t0).Ticks);
                Func<int, float> X = i => (float)(area.Left + (pts[i].Time - t0).Ticks / span * area.Width);
                var right1 = RightFmt;
                using (var gridPen = new Pen(Color.FromArgb(234, 238, 242)))
                    foreach (double v in ticks)
                    {
                        float y = Y(v);
                        g.DrawLine(gridPen, area.Left, y, area.Right, y);
                        // 0 筆交易時刻度沒有意義，只留初始資金線
                        if (trades != 0) g.DrawString(v.ToString("N" + dec, CultureInfo.InvariantCulture), font, muted, new RectangleF(0, y - 10 * k, area.Left - 8 * k, 22 * k), right1);
                    }
                // 日期刻度：時間平均分成 5 段
                int nDates = Math.Max(2, Math.Min(6, area.Width / (int)(150 * k)));
                using (var tickPen = new Pen(Color.FromArgb(208, 215, 222)))
                    for (int d = 0; d < nDates; d++)
                    {
                        DateTime td = t0.AddTicks((long)(span * d / (nDates - 1)));
                        float x = (float)(area.Left + (double)area.Width * d / (nDates - 1));
                        string s = td.ToString("yyyy-MM-dd");
                        var sz = g.MeasureString(s, font);
                        float tx = Math.Max(area.Left, Math.Min(area.Right - sz.Width, x - sz.Width / 2));
                        g.DrawLine(tickPen, x, area.Bottom, x, area.Bottom + 5 * k);
                        g.DrawString(s, font, muted, tx, area.Bottom + 8 * k);
                    }
                using (var dash = new Pen(Color.FromArgb(140, 149, 159), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, area.Left, Y(initial), area.Right, Y(initial));
                int step = Math.Max(1, pts.Count / Math.Max(1, area.Width * 2));
                DrawSeries(g, X, Y, p => p.Equity, Color.FromArgb(45, 164, 78), step);
                DrawSeries(g, X, Y, p => p.Balance, Color.FromArgb(9, 105, 218), step);
                // 圖例：寬度依實際文字量
                float lx = area.Left, ly = 12 * k;
                lx = Legend(g, font, lx, ly, Color.FromArgb(9, 105, 218), "餘額", false);
                lx = Legend(g, font, lx, ly, Color.FromArgb(45, 164, 78), "淨值（含未平倉）", false);
                lx = Legend(g, font, lx, ly, Color.FromArgb(140, 149, 159), "初始資金 " + initial.ToString("N0", CultureInfo.InvariantCulture), true);
                using (var nb = new SolidBrush(Color.FromArgb(140, 149, 159))) g.DrawString("（線條水平的一段 = 休市沒有報價）", font, nb, lx, ly);
                if (trades == 0)
                    using (var f = new Font("Microsoft JhengHei", 14f))
                    {
                        var fmt = CenterFmt;
                        g.DrawString("這次回測沒有任何交易（原因請看「日誌」分頁）", f, muted, new RectangleF(area.Left, area.Top + area.Height / 3f, area.Width, 40 * k), fmt);
                    }
            }
        }

        private static float Legend(Graphics g, Font font, float x, float y, Color c, string text, bool dashed)
        {
            float k = g.DpiX / 96f;
            if (dashed) using (var p = new Pen(c, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, x, y + 10 * k, x + 22 * k, y + 10 * k);
            else using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y + 4 * k, 14 * k, 12 * k);
            float tx = x + (dashed ? 28 : 20) * k;
            g.DrawString(text, font, Brushes.Black, tx, y);
            return tx + g.MeasureString(text, font).Width + 22 * k;
        }

        private void DrawSeries(Graphics g, Func<int, float> X, Func<double, float> Y, Func<EquityPoint, double> f, Color c, int step)
        {
            // 休市（週末、假日）期間數值不變：先畫水平再接到下一點，不畫成長斜線
            double normal = normalGap;
            var list = new List<PointF>();
            int prev = -1;
            for (int i = 0; ; i += step)
            {
                if (i >= pts.Count) i = pts.Count - 1;
                if (prev >= 0 && normal > 0 && (pts[i].Time - pts[prev].Time).TotalMinutes > normal * 3 * step)
                    list.Add(new PointF(X(i), Y(f(pts[prev]))));
                list.Add(new PointF(X(i), Y(f(pts[i]))));
                prev = i;
                if (i == pts.Count - 1) break;
            }
            using (var pen = new Pen(c, 2f)) if (list.Count >= 2) g.DrawLines(pen, list.ToArray());
        }
    }
}
