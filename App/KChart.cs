using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CtaBacktest;

// K 線圖分頁：K 棒＋每筆交易的進出場標記，用來確認策略邏輯（spec 9 節）
namespace BacktesterApp
{
    internal static class ChartColors
    {
        // 配色集中在這裡，要換慣例只改這幾行
        public static Color Up = Color.FromArgb(38, 166, 91);        // 收漲 K 棒
        public static Color Down = Color.FromArgb(220, 53, 69);      // 收跌 K 棒
        public static Color Buy = Color.FromArgb(9, 105, 218);       // 買進標記
        public static Color Sell = Color.FromArgb(207, 34, 46);      // 賣出標記
        public static Color Win = Color.FromArgb(26, 127, 55);       // 獲利的連線、出場
        public static Color Loss = Color.FromArgb(207, 34, 46);      // 虧損的連線、出場
        public static Color Grid = Color.FromArgb(238, 241, 244);
        public static Color Axis = Color.FromArgb(87, 96, 106);
        public static Color Highlight = Color.FromArgb(255, 251, 230);
        public static Color RowSelected = Color.FromArgb(221, 244, 255);
        // 均線：第 1～4 個週期欄的顏色（避開 K 棒的紅綠與買賣的藍紅）
        public static Color[] Lines = { Color.FromArgb(227, 98, 9), Color.FromArgb(130, 80, 223), Color.FromArgb(27, 124, 131), Color.FromArgb(154, 103, 0) };
    }

    // 整個分頁：左邊圖＋捲軸，右邊交易清單
    internal sealed class KChartView : UserControl
    {
        private readonly KChartPanel chart = new KChartPanel { Dock = DockStyle.Fill };
        private readonly HScrollBar scroll = new HScrollBar { Dock = DockStyle.Bottom, Height = 20 };
        private readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = true, ShowItemToolTips = true, MultiSelect = false, GridLines = false, Font = new Font("Microsoft JhengHei", 11f), BorderStyle = BorderStyle.FixedSingle };
        private readonly Label lbEmpty = new Label { Text = "沒有交易", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Microsoft JhengHei", 12f), ForeColor = Color.FromArgb(140, 149, 159), BackColor = Color.White, Visible = false };
        private readonly List<Button> navButtons = new List<Button>();
        private bool syncing;
        private int marked = -1;   // 清單裡目前上色的那一列
        private readonly NumericUpDown[] maBoxes = new NumericUpDown[4];
        private readonly NumericUpDown bandBox = new NumericUpDown { Minimum = 0, Maximum = 50, DecimalPlaces = 2, Increment = 0.5m, Width = 64, Font = new Font("Microsoft JhengHei", 10.5f), Margin = new Padding(0, 2, 0, 0) };
        private ChartData current;
        private bool loadingOverlay;

        private void ApplyOverlay()
        {
            if (loadingOverlay || current == null) return;
            current.SetSma(maBoxes.Select(b => (int)b.Value).ToList(), (double)bandBox.Value);
            chart.Invalidate();
        }

        // 選取的那列用淺藍底（系統的選取色在清單沒焦點時幾乎看不到）
        private void MarkRow(int i)
        {
            if (marked >= 0 && marked < list.Items.Count) SetRowBack(list.Items[marked], Color.White);
            marked = i;
            if (i >= 0 && i < list.Items.Count) SetRowBack(list.Items[i], ChartColors.RowSelected);
        }

        private static void SetRowBack(ListViewItem it, Color c) { foreach (ListViewItem.ListViewSubItem s in it.SubItems) s.BackColor = c; }

        public KChartView()
        {
            var right = new Panel { Dock = DockStyle.Right, Width = 380, Padding = new Padding(8, 0, 0, 0), BackColor = Color.White };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
            var btPrev = new Button { Text = "◀ 上一筆", AutoSize = true, Height = 34 };
            var btNext = new Button { Text = "下一筆 ▶", AutoSize = true, Height = 34 };
            var btFit = new Button { Text = "看全部交易", AutoSize = true, Height = 34 };
            btPrev.Click += (s, e) => chart.Step(-1);
            btNext.Click += (s, e) => chart.Step(1);
            btFit.Click += (s, e) => chart.FitAll();
            bar.Controls.AddRange(new Control[] { btPrev, btNext, btFit });
            navButtons.AddRange(new[] { btPrev, btNext, btFit });
            list.Columns.Add("#", 40, HorizontalAlignment.Right);
            list.Columns.Add("方向", 48);
            list.Columns.Add("進場時間（UTC）", 150);
            list.Columns.Add("手數", 60, HorizontalAlignment.Right);
            list.Columns.Add("價差", 80, HorizontalAlignment.Right);   // 順方向為正（spec.md K 線圖）
            list.Columns.Add("原因", 74);   // 和報告、README 同名
            list.Columns.Add("損益", 90, HorizontalAlignment.Right);
            list.SmallImageList = new ImageList { ImageSize = new Size(1, 28) };   // 撐開列高
            right.Width = 590;
            right.Controls.Add(lbEmpty);
            right.Controls.Add(list);
            right.Controls.Add(bar);
            // 說明分兩段：上面是操作方式，下面是時區與價格說明（淡灰）
            var help = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(4, 16, 4, 0) };   // 上方留白，清單最後半列不貼著說明
            help.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(140, 149, 159), Font = new Font("Microsoft JhengHei", 10f),
                Text = "時間都是 UTC（台灣時間 = UTC + 8 小時）。\nK 棒是賣價；買單成交價含點差，可能略高於 K 棒。" });
            help.Controls.Add(new Label { Dock = DockStyle.Top, Height = 78, ForeColor = ChartColors.Axis, Font = new Font("Microsoft JhengHei", 10.5f),
                Text = "▲ 多進（買進開倉）　▼ 空進（賣出開倉）　⊗ 平倉（縮小時是小圓點）\n點一筆交易：圖會跳過去並畫出停損、停利\n滾輪縮放 ｜ 按住拖曳平移 ｜ ← → 換筆" });
            right.Controls.Add(help);

            // 均線列：最多 4 條，0 = 不畫；第一條可加上下 ±% 通道。改了數字立刻重畫
            var maBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 6, 0, 0), WrapContents = false, BackColor = Color.White };
            var lf = new Font("Microsoft JhengHei", 10.5f);
            maBar.Controls.Add(new Label { Text = "均線（SMA，收盤價）", AutoSize = true, Font = lf, ForeColor = ChartColors.Axis, Margin = new Padding(0, 6, 6, 0) });
            for (int i = 0; i < maBoxes.Length; i++)
            {
                maBar.Controls.Add(new Label { Text = "━", AutoSize = true, Font = new Font("Microsoft JhengHei", 11f, FontStyle.Bold), ForeColor = ChartColors.Lines[i], Margin = new Padding(i == 0 ? 0 : 8, 5, 0, 0) });
                maBoxes[i] = new NumericUpDown { Minimum = 0, Maximum = 5000, Width = 64, Font = lf, Margin = new Padding(0, 2, 0, 0) };
                maBoxes[i].ValueChanged += (s, e) => ApplyOverlay();
                maBar.Controls.Add(maBoxes[i]);
            }
            maBar.Controls.Add(new Label { Text = "第一條上下 ±", AutoSize = true, Font = lf, ForeColor = ChartColors.Axis, Margin = new Padding(16, 6, 0, 0) });
            bandBox.ValueChanged += (s, e) => ApplyOverlay();
            maBar.Controls.Add(bandBox);
            maBar.Controls.Add(new Label { Text = "%（虛線；0 = 不畫）", AutoSize = true, Font = lf, ForeColor = ChartColors.Axis, Margin = new Padding(2, 6, 0, 0) });

            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(chart);
            left.Controls.Add(scroll);
            left.Controls.Add(maBar);
            left.Controls.Add(BuildReplayBar(lf));
            Controls.Add(left);
            Controls.Add(right);

            list.SelectedIndexChanged += (s, e) =>
            {
                if (syncing || list.SelectedIndices.Count == 0) return;
                int i = list.SelectedIndices[0];
                // 回放中點一筆：暫停並跳到那筆平倉那根（spec.md 14.1）
                if (chart.Replay >= 0 && current != null && i < current.Marks.Count)
                {
                    SetPlaying(false);
                    var m = current.Marks[i];
                    int to = Math.Max(m.EntryIndex, m.ExitIndex);
                    if (to > chart.Replay) { chart.Replay = to; SyncReplayUi(); }
                }
                chart.Select(i, true);
            };
            chart.SelectedChanged += i =>
            {
                syncing = true;
                // 保留系統選取（清單裡可用上下鍵換筆）；沒焦點時隱藏系統的淡灰，改顯示自己的淺藍底
                if (i >= 0 && i < list.Items.Count) { list.Items[i].Selected = true; list.Items[i].Focused = true; list.EnsureVisible(i); }
                MarkRow(i);
                syncing = false;
            };
            chart.ViewChanged += () =>
            {
                syncing = true;
                scroll.Maximum = Math.Max(0, chart.BarCount - 1);
                scroll.LargeChange = Math.Max(1, chart.VisibleBars);
                scroll.Value = Math.Max(scroll.Minimum, Math.Min(Math.Max(0, scroll.Maximum - scroll.LargeChange + 1), chart.FirstVisible));
                syncing = false;
            };
            // 回放中拖捲軸看過去：暫停跟隨，免得下一格又被拉回最新那根
            scroll.Scroll += (s, e) => { if (!syncing) { chart.Follow = false; chart.ScrollTo(scroll.Value); } };
            Disposed += (s, e) => replayTimer.Dispose();
            SetData(null);
        }

        public void SetData(ChartData cd, string noTradeReason = null, BacktestResult result = null)
        {
            chart.NoTradeReason = noTradeReason;
            current = cd;
            // 策略參數有均線週期就自動帶入；沒有就保留上次填的
            var periods = new List<int>();
            double band;
            if (ChartData.SuggestSma(result, periods, out band))
            {
                loadingOverlay = true;
                for (int i = 0; i < maBoxes.Length; i++) maBoxes[i].Value = i < periods.Count ? Math.Min(maBoxes[i].Maximum, periods[i]) : 0;
                bandBox.Value = (decimal)Math.Min(50, band);
                loadingOverlay = false;
            }
            if (cd != null) cd.SetSma(maBoxes.Select(b => (int)b.Value).ToList(), (double)bandBox.Value);
            list.BeginUpdate();
            list.Items.Clear();
            var seenPos = new HashSet<string>();
            if (cd != null)
                foreach (var m in cd.Marks)
                {
                    var t = m.Trade;
                    // 同一個部位分次平倉：第二筆起標「部分」，免得以為重複進場
                    bool part = !seenPos.Add(t.PositionId ?? ("#" + t.No));
                    double move = (t.ClosePrice - t.OpenPrice) * (t.IsLong ? 1 : -1);
                    var it = new ListViewItem(new[] { t.No.ToString(CultureInfo.InvariantCulture), (t.IsLong ? "買" : "賣") + (part ? "（部分）" : ""),
                        t.OpenTime.ToString("yyyy-MM-dd HH:mm"), t.Volume.ToString("0.##", CultureInfo.InvariantCulture),
                        move.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture), t.Reason ?? "", t.Profit.ToString("N2", CultureInfo.InvariantCulture) });
                    it.UseItemStyleForSubItems = false;
                    // 滑鼠停在列上顯示出場時間與進出場價格（清單放不下第二個時間欄）
                    it.ToolTipText = "進場 " + t.OpenTime.ToString("yyyy-MM-dd HH:mm") + " @" + t.OpenPrice.ToString("0.#####", CultureInfo.InvariantCulture)
                        + "\n出場 " + t.CloseTime.ToString("yyyy-MM-dd HH:mm") + " @" + t.ClosePrice.ToString("0.#####", CultureInfo.InvariantCulture) + "（UTC）";
                    it.SubItems[1].ForeColor = t.IsLong ? ChartColors.Buy : ChartColors.Sell;
                    it.SubItems[4].ForeColor = move >= 0 ? ChartColors.Win : ChartColors.Loss;
                    it.SubItems[6].ForeColor = t.Profit >= 0 ? ChartColors.Win : ChartColors.Loss;
                    list.Items.Add(it);
                }
            list.EndUpdate();
            marked = -1;
            bool any = cd != null && cd.Marks.Count > 0;
            lbEmpty.Text = cd == null ? "還沒有回測結果" : "沒有交易";
            lbEmpty.Visible = !any;
            list.Visible = any;   // 沒交易時整個換成「沒有交易」，不留空白表格
            foreach (var b in navButtons) { b.Enabled = any; b.ForeColor = any ? SystemColors.ControlText : Color.FromArgb(140, 149, 159); }
            SetPlaying(false);
            chart.SetData(cd);
            bool can = cd != null && cd.CanReplay;
            replayBar.Enabled = can;
            btReplayEnd.Visible = false;
            tbReplay.Maximum = Math.Max(1, cd == null ? 1 : cd.Bars.Count - 1);
            tbReplay.Value = 0;
            lbReplay.Text = can ? IdleHint : cd == null ? "" : "這次結果沒有資金曲線，不能回放";
        }

        // ---------------------------------------------------------------- 回放（spec.md 14）
        private FlowLayoutPanel replayBar;
        private Button btPlay, btReplayEnd;
        private readonly ComboBox cbSpeed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72 };
        private readonly TrackBar tbReplay = new TrackBar { Width = 260, TickStyle = TickStyle.None, Minimum = 0, Maximum = 1, AutoSize = false, Height = 30 };
        private readonly Label lbReplay = new Label { AutoSize = true, ForeColor = ChartColors.Axis, Margin = new Padding(6, 8, 0, 0) };
        private readonly Timer replayTimer = new Timer { Interval = 40 };
        private static readonly int[] Speeds = { 1, 2, 5, 10, 20, 50, 100, 500 };
        private const string IdleHint = "從回測第一根逐根重播";
        private bool playing, syncingTrack;
        private double carry;           // 不足一根的累積量
        private DateTime lastTick;

        private Control BuildReplayBar(Font lf)
        {
            replayBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(4, 6, 0, 0), WrapContents = false, BackColor = Color.FromArgb(246, 248, 250) };
            var title = new Label { Text = "回放", AutoSize = true, Font = new Font("Microsoft JhengHei", 10.5f, FontStyle.Bold), ForeColor = ChartColors.Axis, Margin = new Padding(0, 8, 6, 0) };
            var btStart = new Button { Text = "⏮ 從頭", AutoSize = true, Height = 32, Font = lf };
            btPlay = new Button { Text = "▶ 播放", AutoSize = true, Height = 32, MinimumSize = new Size(96, 32), Font = lf };
            btReplayEnd = new Button { Text = "結束回放", AutoSize = true, Height = 32, Font = lf, Visible = false };
            foreach (int s in Speeds) cbSpeed.Items.Add(s.ToString(CultureInfo.InvariantCulture));
            cbSpeed.SelectedItem = "10";
            cbSpeed.Font = lf;
            tbReplay.Margin = new Padding(8, 2, 0, 0);
            lbReplay.Font = lf;

            btStart.Click += (s, e) => { if (current == null) return; chart.Replay = current.StartIndex; chart.Follow = true; chart.ShowLatest(); SyncReplayUi(); };
            btPlay.Click += (s, e) =>
            {
                if (current == null) return;
                // 還沒開始或已播完：從回測第一根開始
                if (chart.Replay < 0 || chart.Replay >= current.Bars.Count - 1) chart.Replay = current.StartIndex;
                chart.Follow = true;
                SetPlaying(!playing);
            };
            btReplayEnd.Click += (s, e) => { SetPlaying(false); chart.Replay = -1; btReplayEnd.Visible = false; tbReplay.Value = 0; lbReplay.Text = IdleHint; };
            tbReplay.Scroll += (s, e) =>
            {
                if (syncingTrack || current == null) return;
                chart.Replay = tbReplay.Value;
                chart.Follow = true;
                chart.ShowLatest();
                SyncReplayUi();
            };
            replayTimer.Tick += (s, e) => ReplayTick();

            replayBar.Controls.AddRange(new Control[] { title, btStart, btPlay,
                new Label { Text = "速度", AutoSize = true, Font = lf, ForeColor = ChartColors.Axis, Margin = new Padding(12, 8, 4, 0) }, cbSpeed,
                new Label { Text = "根／秒", AutoSize = true, Font = lf, ForeColor = ChartColors.Axis, Margin = new Padding(4, 8, 0, 0) },
                tbReplay, lbReplay, btReplayEnd });
            return replayBar;
        }

        private void SetPlaying(bool on)
        {
            playing = on && current != null && current.CanReplay;
            if (btPlay != null) btPlay.Text = playing ? "❚❚ 暫停" : "▶ 播放";
            carry = 0;
            lastTick = DateTime.UtcNow;
            if (playing) { replayTimer.Start(); ShowReplayChart(); } else replayTimer.Stop();
            SyncReplayUi();
        }

        private void ShowReplayChart()
        {
            if (chart.Follow) chart.ShowLatest();
        }

        private void ReplayTick()
        {
            if (!playing || current == null) { replayTimer.Stop(); return; }
            DateTime now = DateTime.UtcNow;
            double dt = Math.Min(0.5, (now - lastTick).TotalSeconds);   // 視窗被拖住時不要一次跳太多
            lastTick = now;
            int speed = Speeds[Math.Max(0, cbSpeed.SelectedIndex)];
            carry += speed * dt;
            int n = (int)carry;
            if (n <= 0) return;
            carry -= n;
            int last = current.Bars.Count - 1;
            int next = Math.Min(last, Math.Max(0, chart.Replay) + n);
            chart.Replay = next;
            ShowReplayChart();
            if (next >= last) SetPlaying(false);
            else SyncReplayUi();
        }

        private void SyncReplayUi()
        {
            if (current == null || tbReplay == null) return;
            int r = chart.Replay;
            btReplayEnd.Visible = r >= 0;
            if (r < 0) return;
            syncingTrack = true;
            tbReplay.Value = Math.Max(tbReplay.Minimum, Math.Min(tbReplay.Maximum, r));
            syncingTrack = false;
            // 時間已顯示在圖上方資訊列，這裡只放進度
            lbReplay.Text = "第 " + (r + 1).ToString("N0", CultureInfo.InvariantCulture) + " / " + current.Bars.Count.ToString("N0", CultureInfo.InvariantCulture) + " 根"
                + (r >= current.Bars.Count - 1 ? "（播完）" : "");
        }
    }

    internal sealed class KChartPanel : Control
    {
        private ChartData data;
        private int sel = -1;
        private double barW = 9;          // 每根 K 棒佔的像素（可小於 1：一個像素擠好幾根）
        private const double MinBarW = 0.05;
        // 最小寬度：一般 0.05 像素/根；K 棒太多（M1 長期間）時放寬到能整段塞進畫面
        private double MinW { get { return Math.Min(MinBarW, (double)Plot.Width / Math.Max(1, BarCount)); } }

        // 滾輪訊息只給有焦點的控制項：滑鼠移進圖就拿焦點，不用先點（點了可能換到別筆）
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (!Focused && CanFocus) Focus(); }

        // 分頁第一次顯示或拉大視窗時，重算可見根數與捲軸
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (data != null) { Clamp(); Invalidate(); } }
        private double first;             // 畫面最左邊是第幾根
        private int hover = -1;
        private Point dragFrom; private double dragFirst; private bool dragging, dragMoved;

        public string NoTradeReason;      // 0 筆交易時寫在上方的原因
        public event Action<int> SelectedChanged;
        public event Action ViewChanged;

        public KChartPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
            Font = new Font("Microsoft JhengHei", 10f);
            SetStyle(ControlStyles.Selectable, true);
        }

        public int BarCount { get { return data == null ? 0 : data.Bars.Count; } }

        // 回放（spec.md 14）：-1 = 一般模式；否則只畫到第 replay 根
        private int replay = -1;
        public bool Follow = true;            // 畫面跟著最新那根捲動；使用者拖曳時關掉
        public event Action UserScrolled;
        public int Replay
        {
            get { return replay; }
            set { replay = data == null || value < 0 ? -1 : Math.Min(value, BarCount - 1); Clamp(); Invalidate(); }
        }
        private int Shown { get { return replay >= 0 ? replay + 1 : BarCount; } }
        private int LastBar { get { return Shown - 1; } }

        // 最新那根放在畫面右邊約 85% 的位置
        public void ShowLatest()
        {
            if (replay < 0) return;
            first = replay + 1 - VisibleBars * 0.85;
            Clamp();
            Invalidate();
        }
        public int VisibleBars { get { return Math.Max(1, (int)(Plot.Width / barW)); } }
        public int FirstVisible { get { return (int)first; } }

        private Rectangle Plot
        {
            get
            {
                float k = DeviceDpi / 96f;
                int top = (int)(56 * k), right = (int)(92 * k), bottom = (int)(30 * k), left = (int)(8 * k);
                return new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            }
        }

        public void SetData(ChartData cd)
        {
            data = cd;
            sel = -1; hover = -1; replay = -1;
            barW = 9;
            if (data != null && data.Marks.Count > 0)
            {
                // 一開始停在第一筆交易附近
                int e = Math.Max(0, data.Marks[0].EntryIndex);
                first = Math.Max(0, e - VisibleBars / 4);
            }
            else first = Math.Max(0, BarCount - VisibleBars);
            Clamp();
            Invalidate();
        }

        // 「看全部交易」：第一筆進場到最後一筆出場；沒有交易時看全部 K 棒
        public void FitAll()
        {
            if (BarCount == 0) return;
            int a = 0, b = BarCount - 1;
            if (data.Marks.Count > 0)
            {
                a = data.Marks.Where(m => m.EntryIndex >= 0).Select(m => m.EntryIndex).DefaultIfEmpty(0).Min();
                b = data.Marks.Select(m => Math.Max(m.EntryIndex, m.ExitIndex)).Max();
                a = Math.Max(0, a - 10); b = Math.Min(BarCount - 1, b + 10);
            }
            b = Math.Min(b, LastBar); a = Math.Min(a, b);   // 回放中只看到目前為止
            barW = (double)Plot.Width / Math.Max(1, b - a + 1);   // M1 長期間可能小於 MinBarW，Clamp 會依根數放寬
            first = a;
            Clamp();
            Invalidate();
        }

        public void ScrollTo(int index) { first = index; Clamp(); Invalidate(); }

        public void Step(int dir)
        {
            if (data == null || data.Marks.Count == 0) return;
            int i = sel < 0 ? (dir > 0 ? 0 : data.Marks.Count - 1) : Math.Max(0, Math.Min(data.Marks.Count - 1, sel + dir));
            Select(i, true);
        }

        public void Select(int i, bool center)
        {
            if (data == null || i < 0 || i >= data.Marks.Count) return;
            sel = i;
            if (center)
            {
                var m = data.Marks[i];
                int a = Math.Max(0, m.EntryIndex), b = Math.Max(a, m.ExitIndex);
                // 整筆交易置中，左右各留至少 15 根（或交易長度的四分之一）看前後走勢
                double pad = Math.Max(15, (b - a) * 0.25);
                if (b - a + 2 * pad > VisibleBars) barW = Math.Max(1, (double)Plot.Width / (b - a + 2 * pad));
                first = (a + b) / 2.0 - VisibleBars / 2.0;
                Clamp();
            }
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(i);
        }

        private void Clamp()
        {
            barW = Math.Max(MinW, Math.Min(60, barW));
            // 右邊最多多捲 5 根空白（回放時多留，讓最新那根在 85% 處），但至少留 1 根在畫面內
            int blank = replay >= 0 ? Math.Max(5, (int)(VisibleBars * 0.3)) : 5;
            first = Math.Max(0, Math.Min(Math.Max(0, Shown - VisibleBars + Math.Min(blank, VisibleBars - 1)), first));
            if (ViewChanged != null) ViewChanged();
        }

        private float X(int i) { var p = Plot; return (float)(p.Left + (i - first + 0.5) * barW); }
        private int IndexAt(int x) { return (int)Math.Floor((x - Plot.Left) / barW + first); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        // 左右、上下鍵都換筆（滑鼠移進圖會拿走清單的焦點，上下鍵要照樣能用）
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) Step(-1);
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) Step(1);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (BarCount == 0) return;
            // 以滑鼠位置為中心縮放
            double at = (e.X - Plot.Left) / barW + first;
            barW *= e.Delta > 0 ? 1.25 : 0.8;
            barW = Math.Max(MinW, Math.Min(60, barW));
            first = at - (e.X - Plot.Left) / barW;
            Clamp();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; dragMoved = false; dragFrom = e.Location; dragFirst = first; Cursor = Cursors.SizeWE; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                if (Math.Abs(e.X - dragFrom.X) > 3 && !dragMoved) { dragMoved = true; Follow = false; if (UserScrolled != null) UserScrolled(); }
                first = dragFirst - (e.X - dragFrom.X) / barW;
                Clamp();
                Invalidate();
                return;
            }
            int h = Plot.Contains(e.Location) ? IndexAt(e.X) : -1;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Cursor = Cursors.Default;
            if (!dragMoved) PickAt(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        // 點在 K 棒上：選那根進場或出場的交易（最近的一筆）
        private void PickAt(Point p)
        {
            if (data == null) return;
            int idx = IndexAt(p.X);
            int best = -1, bestD = int.MaxValue;
            for (int i = 0; i < data.Marks.Count; i++)
            {
                var m = data.Marks[i];
                int d = Math.Min(Math.Abs(m.EntryIndex - idx), Math.Abs(m.ExitIndex - idx));
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0 && bestD * barW <= 14) Select(best, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float k = DeviceDpi / 96f;
            if (data == null || data.Bars.Count == 0)
            {
                using (var f1 = new Font("Microsoft JhengHei", 16f))
                using (var f2 = new Font("Microsoft JhengHei", 12f))
                using (var b = new SolidBrush(ChartColors.Axis))
                using (var c = new StringFormat { Alignment = StringAlignment.Center })
                {
                    g.DrawString("還沒有回測結果", f1, Brushes.Black, new RectangleF(0, Height / 2f - 40, Width, 34), c);
                    g.DrawString("跑完回測後，這裡會畫出 K 線與每一筆買賣的進出場位置。", f2, b, new RectangleF(20, Height / 2f, Width - 40, 60), c);
                }
                return;
            }
            var plot = Plot;
            // 回放時只畫到目前那根（LastBar）；之後留白
            int from = Math.Max(0, (int)Math.Floor(first)), to = Math.Min(LastBar, (int)Math.Ceiling(first + plot.Width / barW));
            if (to < from) return;
            int cur = LastBar;
            bool replaying = replay >= 0;

            // 價格範圍：畫面內的 K 棒＋選取交易的停損停利
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = from; i <= to; i++) { lo = Math.Min(lo, data.Bars[i].Low); hi = Math.Max(hi, data.Bars[i].High); }
            if (sel >= 0 && data.Marks[sel].EntryIndex <= cur)
            {
                var t = data.Marks[sel].Trade;
                bool closed = data.Marks[sel].ExitIndex <= cur;
                // 持倉中不納入停損停利：那是平倉當下的值，回放時算未來資訊
                foreach (double v in new[] { closed ? t.StopLoss : double.NaN, closed ? t.TakeProfit : double.NaN, t.OpenPrice, closed ? t.ClosePrice : double.NaN })
                    if (!double.IsNaN(v) && v > 0 && v > lo - (hi - lo) * 2 && v < hi + (hi - lo) * 2) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            }
            // 均線與通道也納入（離 K 棒範圍太遠的不納入，免得 K 棒被壓扁）
            {
                double blo = lo, bhi = hi, span = Math.Max(bhi - blo, Math.Abs(bhi) * 1e-4);
                foreach (var ln in data.Lines)
                    for (int i = from; i <= to; i++)
                    {
                        double v = ln.Values[i];
                        if (double.IsNaN(v) || v < blo - span * 2 || v > bhi + span * 2) continue;
                        lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                    }
            }
            double pad = (hi - lo) * 0.08;
            if (pad <= 0) pad = Math.Max(1e-6, Math.Abs(hi) * 0.001);
            lo -= pad; hi += pad;
            int dec;
            List<double> ticks = ChartAxis.NiceTicks(ref lo, ref hi, 6, out dec);
            dec = Math.Min(Math.Max(dec, 0), data.PriceDigits);
            Func<double, float> Y = v => (float)(plot.Bottom - (v - lo) / (hi - lo) * plot.Height);

            g.SmoothingMode = SmoothingMode.None;
            // 選取交易的持倉區間：淡黃底（回放中畫到目前那根）
            if (sel >= 0 && data.Marks[sel].EntryIndex <= cur)
            {
                var m = data.Marks[sel];
                float x1 = X(Math.Max(0, m.EntryIndex)) - (float)barW / 2, x2 = X(Math.Max(0, Math.Min(cur, m.ExitIndex))) + (float)barW / 2;
                using (var b = new SolidBrush(ChartColors.Highlight)) g.FillRectangle(b, Math.Max(plot.Left, x1), plot.Top, Math.Max(1, Math.Min(plot.Right, x2) - Math.Max(plot.Left, x1)), plot.Height);
            }
            using (var gp = new Pen(ChartColors.Grid))
            using (var ab = new SolidBrush(ChartColors.Axis))
            {
                foreach (double v in ticks)
                {
                    float y = Y(v);
                    if (y < plot.Top - 1 || y > plot.Bottom + 1) continue;
                    g.DrawLine(gp, plot.Left, y, plot.Right, y);
                    g.DrawString(v.ToString("N" + dec, CultureInfo.InvariantCulture), Font, ab, plot.Right + 6 * k, y - 8 * k);
                }
                // 時間刻度：至少相隔 130 像素；文字會和前一個重疊就不寫
                int every = Math.Max(1, (int)Math.Ceiling(130 * k / barW));
                bool intraday = data.Timeframe != "D1" && data.Timeframe != "W1" && data.Timeframe != "MN1";
                // 一個刻度間隔超過 2 天時只標日期，時分沒有意義
                bool showTime = intraday && (data.Bars[Math.Min(to, from + every)].Time - data.Bars[from].Time).TotalDays < 2;
                int lastYear = -1;
                float lastRight = float.MinValue;
                // 畫面最左邊那根一定標，之後每 every 根一個刻度
                var idx = new List<int> { from };
                for (int i = from - from % every + every; i <= to; i += every) if (i > from) idx.Add(i);
                foreach (int i in idx)
                {
                    float x = X(i);
                    DateTime t = data.Bars[i].Time;
                    // 第一個刻度與跨年的刻度帶年份
                    string s = t.ToString((t.Year != lastYear ? "yyyy-" : "") + "MM-dd" + (showTime ? " HH:mm" : ""));
                    var sz = g.MeasureString(s, Font);
                    float tx = Math.Max(plot.Left, Math.Min(plot.Right - sz.Width, x - sz.Width / 2));
                    if (tx < lastRight + 12 * k) continue;
                    if (i != from) g.DrawLine(gp, x, plot.Top, x, plot.Bottom);
                    g.DrawString(s, Font, ab, tx, plot.Bottom + 6 * k);
                    lastRight = tx + sz.Width;
                    lastYear = t.Year;
                }
            }
            // 資料缺口：相鄰兩根相隔太久（分鐘線到 H4 超過 5 天）時畫灰色虛線並標「缺 N 天」
            {
                double gapDays = DataQuality.GapThresholdDays(data.Timeframe, 5);
                float lastGapRight = float.MinValue;
                using (var gpen = new Pen(Color.FromArgb(150, 150, 150)) { DashStyle = DashStyle.Dash })
                using (var gb = new SolidBrush(Color.FromArgb(110, 110, 110)))
                using (var gbg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    for (int i = Math.Max(1, from); i <= to; i++)
                    {
                        double days = (data.Bars[i].Time - data.Bars[i - 1].Time).TotalDays;
                        if (days <= gapDays) continue;
                        float x = X(i) - (float)barW / 2;
                        g.DrawLine(gpen, x, plot.Top, x, plot.Bottom);
                        string s = "缺 " + Math.Round(days).ToString("N0", CultureInfo.InvariantCulture) + " 天";
                        var sz = g.MeasureString(s, Font);
                        float tx = Math.Min(plot.Right - sz.Width, x + 3 * k);
                        if (tx < lastGapRight + 6 * k) continue;
                        g.FillRectangle(gbg, tx, plot.Top + 4 * k, sz.Width, sz.Height);
                        g.DrawString(s, Font, gb, tx, plot.Top + 4 * k);
                        lastGapRight = tx + sz.Width;
                    }
            }
            using (var border = new Pen(Color.FromArgb(208, 215, 222))) g.DrawRectangle(border, plot);

            // K 棒
            var clip = g.Clip; g.SetClip(plot);   // g.Clip 取得的是複本，最後還原後要釋放
            using (var up = new SolidBrush(ChartColors.Up)) using (var dn = new SolidBrush(ChartColors.Down))
            using (var upPen = new Pen(ChartColors.Up)) using (var dnPen = new Pen(ChartColors.Down))
                for (int i = from; i <= to; i++)
                {
                    var b = data.Bars[i];
                    // 一個像素擠好幾根時合併成一根（取區間高低），不然幾萬根逐根畫會很慢
                    int n = barW < 1 ? (int)Math.Ceiling(1 / barW) : 1;
                    if (n > 1)
                    {
                        int j2 = Math.Min(to, i + n - 1);
                        double h = b.High, l = b.Low;
                        for (int j = i + 1; j <= j2; j++) { h = Math.Max(h, data.Bars[j].High); l = Math.Min(l, data.Bars[j].Low); }
                        bool r2 = data.Bars[j2].Close >= b.Open;
                        g.DrawLine(r2 ? upPen : dnPen, X(i), Y(h), X(i), Y(l));
                        i = j2;
                        continue;
                    }
                    bool rise = b.Close >= b.Open;
                    float x = X(i);
                    g.DrawLine(rise ? upPen : dnPen, x, Y(b.High), x, Y(b.Low));
                    if (barW >= 3)
                    {
                        float w = (float)Math.Max(1, barW * 0.7), top = Y(Math.Max(b.Open, b.Close)), bot = Y(Math.Min(b.Open, b.Close));
                        g.FillRectangle(rise ? up : dn, x - w / 2, top, w, Math.Max(1, bot - top));
                    }
                }

            // 均線與通道：畫在 K 棒上、交易標記下；一個像素擠好幾根時每像素取一點
            g.SmoothingMode = SmoothingMode.AntiAlias;
            {
                int step = barW < 1 ? (int)Math.Ceiling(1 / barW) : 1;
                foreach (var ln in data.Lines)
                    using (var pen = new Pen(ChartColors.Lines[ln.ColorSlot % ChartColors.Lines.Length], (ln.Dashed ? 1.2f : 1.8f) * k))
                    {
                        if (ln.Dashed) pen.DashStyle = DashStyle.Dash;
                        var pts = new List<PointF>();
                        for (int i = from; i <= to + step; i += step)
                        {
                            int j = Math.Min(i, to);
                            double v = ln.Values[j];
                            if (double.IsNaN(v)) { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); }
                            else pts.Add(new PointF(X(j), Math.Max(-100000f, Math.Min(100000f, Y(v)))));   // 太遠的點夾住，GDI+ 座標過大會出錯
                            if (j == to) break;
                        }
                        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                    }
            }

            // 交易標記
            using (var lbl = new Font("Microsoft JhengHei", 10.5f, FontStyle.Bold))
            {
                // 部分平倉：同一個部位只畫一次進場箭頭；同一根 K 棒多筆進場：箭頭依序錯開
                var drawnPos = new HashSet<string>();
                var slotAt = new Dictionary<int, int>();
                for (int i = 0; i < data.Marks.Count; i++)
                {
                    var m = data.Marks[i];
                    if (m.EntryIndex < 0 || m.EntryIndex > cur) continue;   // 回放：還沒進場的不畫
                    string pid = m.Trade.PositionId ?? ("#" + m.Trade.No);
                    bool arrow = drawnPos.Add(pid);
                    int slot = 0;
                    if (arrow) { slotAt.TryGetValue(m.EntryIndex, out slot); slotAt[m.EntryIndex] = slot + 1; }
                    if (replaying && m.ExitIndex > cur)
                    {
                        // 持倉中：畫到目前那根
                        if (m.EntryIndex <= to) DrawOpen(g, m, i == sel, Y, lbl, k, cur, arrow || i == sel, slot);
                        continue;
                    }
                    if (m.ExitIndex < from || m.EntryIndex > to) continue;
                    DrawTrade(g, m, i == sel, Y, lbl, k, arrow || i == sel, slot);
                }
            }
            g.Clip = clip;
            clip.Dispose();

            // 上方資訊：選取的交易、滑鼠所在的 K 棒
            using (var f = new Font("Microsoft JhengHei", 11f))
            using (var mb = new SolidBrush(ChartColors.Axis))
            {
                string head = data.Timeframe + "（UTC）　共 " + data.Bars.Count.ToString("N0") + " 根、" + data.Marks.Count + " 筆交易　";
                if (replaying) DrawReplayHead(g, f, plot, k, cur);
                else if (sel >= 0)
                {
                    // 總覽淡灰；這筆的方向用買賣色加粗，損益用盈虧色加粗
                    var t = data.Marks[sel].Trade;
                    string fmt = "N" + data.PriceDigits;
                    string who = "#" + t.No + " " + (t.IsLong ? "買" : "賣") + " " + t.Volume.ToString("0.##") + " 手";
                    string px = "　進 " + t.OpenPrice.ToString(fmt, CultureInfo.InvariantCulture)
                          + " → 出 " + t.ClosePrice.ToString(fmt, CultureInfo.InvariantCulture) + "（" + t.Reason + "）　";
                    using (var fb = new Font(f, FontStyle.Bold))
                    using (var gray = new SolidBrush(Color.FromArgb(87, 96, 106)))
                    using (var db = new SolidBrush(t.IsLong ? ChartColors.Buy : ChartColors.Sell))
                    using (var pb = new SolidBrush(t.Profit >= 0 ? ChartColors.Win : ChartColors.Loss))
                    {
                        float x = plot.Left;
                        g.DrawString(head, f, gray, x, 6 * k); x += g.MeasureString(head, f).Width + 12 * k;
                        g.DrawString(who, fb, db, x, 6 * k); x += g.MeasureString(who, fb).Width;
                        g.DrawString(px, f, Brushes.Black, x, 6 * k); x += g.MeasureString(px, f).Width;
                        g.DrawString("損益 " + t.Profit.ToString("N2", CultureInfo.InvariantCulture), fb, pb, x, 6 * k);
                    }
                }
                else if (data.Marks.Count == 0)
                    // 太長就截成「…」，不壓到右邊按鈕
                    TextRenderer.DrawText(g, head + "這次回測沒有任何交易：" + (NoTradeReason ?? "原因請看「日誌」分頁"), f,
                        new Rectangle(plot.Left, (int)(6 * k), plot.Width + (int)(80 * k), (int)(24 * k)), Color.FromArgb(154, 103, 0), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                else g.DrawString(head + "（點 K 棒上的箭頭或右邊清單選一筆交易）", f, Brushes.Black, plot.Left, 6 * k);
                if (hover >= 0 && hover <= cur)
                {
                    var b = data.Bars[hover];
                    string fmt = "N" + data.PriceDigits;
                    string ohlc = b.Time.ToString("yyyy-MM-dd HH:mm") + " UTC　開 " + b.Open.ToString(fmt, CultureInfo.InvariantCulture) + "　高 " + b.High.ToString(fmt, CultureInfo.InvariantCulture)
                        + "　低 " + b.Low.ToString(fmt, CultureInfo.InvariantCulture) + "　收 " + b.Close.ToString(fmt, CultureInfo.InvariantCulture);
                    g.DrawString(ohlc, f, mb, plot.Left, 30 * k);
                    // 均線值接在後面，用各自的顏色（通道不寫，圖上看虛線即可）
                    float hx = plot.Left + g.MeasureString(ohlc, f).Width + 8 * k;
                    foreach (var ln in data.Lines)
                    {
                        if (ln.Dashed) continue;
                        double v = ln.Values[hover];
                        string s = ln.Name + " " + (double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.InvariantCulture));
                        using (var lb = new SolidBrush(ChartColors.Lines[ln.ColorSlot % ChartColors.Lines.Length]))
                            g.DrawString(s, f, lb, hx, 30 * k);
                        hx += g.MeasureString(s, f).Width + 8 * k;
                    }
                    using (var hp = new Pen(Color.FromArgb(140, 149, 159)) { DashStyle = DashStyle.Dot })
                        g.DrawLine(hp, X(hover), plot.Top, X(hover), plot.Bottom);
                }
            }
        }

        private void DrawTrade(Graphics g, TradeMark m, bool selected, Func<double, float> Y, Font lbl, float k, bool arrow, int slot)
        {
            var t = m.Trade;
            int ei = m.EntryIndex, xi = Math.Max(m.EntryIndex, m.ExitIndex);
            float ex = X(ei), ey = Y(t.OpenPrice), xx = X(xi), xy = Y(t.ClosePrice);
            Color pc = t.Profit >= 0 ? ChartColors.Win : ChartColors.Loss;
            Color dc = t.IsLong ? ChartColors.Buy : ChartColors.Sell;
            // 縮得很小（看全部交易）時用精簡標記，免得全部疊成一團
            bool compact = barW < 3;
            float lw = selected ? 2.5f : compact ? 1f : 1.5f;

            // 停損、停利（只畫選取的那筆；精簡時不寫文字）
            // 縮太小時線很短又沒文字，看不懂，乾脆不畫
            if (selected && !compact)
            {
                DrawLevel(g, t.StopLoss, "停損", ChartColors.Loss, ex, xx, Y, k, true);
                DrawLevel(g, t.TakeProfit, "停利", ChartColors.Win, ex, xx, Y, k, true);
            }
            // 進出場連線
            using (var p = new Pen(compact && !selected ? Color.FromArgb(160, pc) : pc, lw) { DashStyle = DashStyle.Dash }) g.DrawLine(p, ex, ey, xx, xy);
            // 進場價小橫線
            float hw = (float)Math.Max(5 * k, barW * 0.6);
            if (!compact) using (var p = new Pen(dc, 2f)) g.DrawLine(p, ex - hw, ey, ex + hw, ey);
            if (arrow) DrawArrow(g, t, ei, ex, selected, compact, lbl, k, slot, dc, Y);
            DrawExit(g, t, xx, xy, selected, compact, lbl, k, pc);
        }

        // 進場箭頭：買在 K 棒下方向上、賣在上方向下
        private void DrawArrow(Graphics g, TradeRecord t, int ei, float ex, bool selected, bool compact, Font lbl, float k, int slot, Color dc, Func<double, float> Y)
        {
            var bar = data.Bars[ei];
            float s = (selected ? (compact ? 7 : 11) : compact ? 5 : 8) * k;
            float off = slot * s * 3.2f;   // 同一根第 2、3 筆往外錯開
            using (var b = new SolidBrush(dc))
            {
                if (t.IsLong)
                {
                    float y0 = Y(bar.Low) + 4 * k + off;
                    g.FillPolygon(b, new[] { new PointF(ex, y0), new PointF(ex - s, y0 + s * 1.4f), new PointF(ex + s, y0 + s * 1.4f) });
                    // 寫「多進」「空進」：避免把下一筆的賣出進場看成這筆的平倉
                    if (barW >= 4) ArrowText(g, "多進", lbl, b, ex - 14 * k, y0 + s * 1.4f + 1);
                }
                else
                {
                    float y0 = Y(bar.High) - 4 * k - off;
                    g.FillPolygon(b, new[] { new PointF(ex, y0), new PointF(ex - s, y0 - s * 1.4f), new PointF(ex + s, y0 - s * 1.4f) });
                    if (barW >= 4) ArrowText(g, "空進", lbl, b, ex - 14 * k, y0 - s * 1.4f - 17 * k);
                }
                // 精簡模式下選取那筆用深色外框強調，不放大
                if (compact && selected)
                    using (var op = new Pen(dc, 2f))
                    {
                        float cr = s * 1.6f + 2 * k;   // 和箭頭之間留一點空隙
                        float cy = t.IsLong ? Y(bar.Low) + 4 * k + off + s * 0.7f : Y(bar.High) - 4 * k - off - s * 0.7f;
                        g.DrawEllipse(op, ex - cr, cy - cr, cr * 2, cr * 2);
                    }
            }
        }

        private void DrawExit(Graphics g, TradeRecord t, float xx, float xy, bool selected, bool compact, Font lbl, float k, Color pc)
        {
            // 出場：空心圓＋叉（精簡時改實心小點）
            float r = (selected ? 6 : compact ? 3 : 5) * k;
            if (compact && !selected)
                using (var b = new SolidBrush(pc)) g.FillEllipse(b, xx - r, xy - r, r * 2, r * 2);
            else
            using (var p = new Pen(pc, 2f))
            {
                g.FillEllipse(Brushes.White, xx - r, xy - r, r * 2, r * 2);
                g.DrawEllipse(p, xx - r, xy - r, r * 2, r * 2);
                g.DrawLine(p, xx - r * 0.6f, xy - r * 0.6f, xx + r * 0.6f, xy + r * 0.6f);
                g.DrawLine(p, xx - r * 0.6f, xy + r * 0.6f, xx + r * 0.6f, xy - r * 0.6f);
            }
            // 出場旁寫損益（太擠時只有選取的那筆寫）：白底，獲利放上方、虧損放下方，避開 K 棒與連線
            // 出場點在畫面外就不寫，免得像是這裡有一筆出場
            bool exitVisible = xx >= Plot.Left && xx <= Plot.Right;
            if (exitVisible && (barW >= 5 || selected))
            {
                string ps = (t.Profit >= 0 ? "+" : "") + t.Profit.ToString("N2", CultureInfo.InvariantCulture);
                var sz = g.MeasureString(ps, lbl);
                float tx = Math.Max(Plot.Left + 2, Math.Min(Plot.Right - sz.Width - 4, xx - sz.Width / 2));
                float ty = t.Profit >= 0 ? xy - r - sz.Height - 4 * k : xy + r + 4 * k;
                using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillRectangle(bg, tx - 2, ty - 1, sz.Width + 4, sz.Height + 2);
                using (var b = new SolidBrush(pc)) g.DrawString(ps, lbl, b, tx, ty);
            }
        }

        // 回放時上方資訊列：時間、餘額、淨值、浮動損益、持倉與已平倉筆數（spec.md 14.2）
        private void DrawReplayHead(Graphics g, Font f, Rectangle plot, float k, int cur)
        {
            double bal, eq;
            data.EquityAt(cur, out bal, out eq);
            var openPos = new HashSet<string>();
            int closed = 0;
            foreach (var m in data.Marks)
            {
                if (m.EntryIndex < 0 || m.EntryIndex > cur) continue;
                if (m.ExitIndex <= cur) closed++;
                else openPos.Add(m.Trade.PositionId ?? ("#" + m.Trade.No));
            }
            double fl = eq - bal;
            var parts = new List<KeyValuePair<string, Color>>
            {
                new KeyValuePair<string, Color>("回放 " + data.Bars[cur].Time.ToString("yyyy-MM-dd HH:mm") + " UTC　", Color.FromArgb(31, 41, 51)),
                new KeyValuePair<string, Color>("餘額 " + bal.ToString("N2", CultureInfo.InvariantCulture) + "　淨值 " + eq.ToString("N2", CultureInfo.InvariantCulture) + "　", Color.FromArgb(31, 41, 51)),
                new KeyValuePair<string, Color>("浮動損益 " + (fl > 0 ? "+" : "") + fl.ToString("N2", CultureInfo.InvariantCulture) + "　", fl > 0 ? ChartColors.Win : fl < 0 ? ChartColors.Loss : ChartColors.Axis),
                new KeyValuePair<string, Color>("持倉 " + openPos.Count + " 筆　已平倉 " + closed + " / " + data.Marks.Count + " 筆", ChartColors.Axis)
            };
            float x = plot.Left;
            using (var fb = new Font(f, FontStyle.Bold))
                foreach (var p in parts)
                {
                    Font use = p.Key.StartsWith("浮動") ? fb : f;
                    using (var b = new SolidBrush(p.Value)) g.DrawString(p.Key, use, b, x, 6 * k);
                    x += g.MeasureString(p.Key, use).Width;
                }
        }

        // 回放中還沒平倉的單：進場箭頭＋進場價虛線畫到目前那根。
        // 停損停利不畫：結果只記錄平倉當下的值，畫出來等於偷看未來（移動停損尤其明顯）
        private void DrawOpen(Graphics g, TradeMark m, bool selected, Func<double, float> Y, Font lbl, float k, int cur, bool arrow, int slot)
        {
            var t = m.Trade;
            float ex = X(m.EntryIndex), cx = X(cur), ey = Y(t.OpenPrice);
            Color dc = t.IsLong ? ChartColors.Buy : ChartColors.Sell;
            bool compact = barW < 3;
            // 進場點遠在畫面左側時夾住座標（GDI+ 座標過大會出錯）
            float lx = Math.Max(ex, Plot.Left - 10);
            using (var p = new Pen(dc, selected ? 2.5f : 1.8f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, lx, ey, Math.Max(cx, lx + 1), ey);
            if (arrow) DrawArrow(g, t, m.EntryIndex, ex, selected, compact, lbl, k, slot, dc, Y);
            if (!compact)
            {
                string s = "持倉 " + (t.IsLong ? "買" : "賣") + " " + t.Volume.ToString("0.##", CultureInfo.InvariantCulture) + " 手";
                var sz = g.MeasureString(s, lbl);
                float tx = Math.Min(Plot.Right - sz.Width - 2, cx + 6 * k), ty = ey - sz.Height / 2;
                using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillRectangle(bg, tx - 2, ty - 1, sz.Width + 4, sz.Height + 2);
                using (var b = new SolidBrush(dc)) g.DrawString(s, lbl, b, tx, ty);
            }
        }

        // 箭頭旁的「買」「賣」：先鋪白底，避免壓在 K 棒上看不清
        private static void ArrowText(Graphics g, string s, Font f, Brush b, float x, float y)
        {
            var sz = g.MeasureString(s, f);
            using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillRectangle(bg, x - 2 + 3, y - 1, sz.Width - 2, sz.Height + 2);
            g.DrawString(s, f, b, x, y);
        }

        private void DrawLevel(Graphics g, double v, string name, Color c, float x1, float x2, Func<double, float> Y, float k, bool text)
        {
            if (double.IsNaN(v) || v <= 0) return;
            float y = Y(v);
            using (var p = new Pen(c, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, x1, y, Math.Max(x2, x1 + 40 * k), y);
            if (!text) return;
            // 文字放在線的左端上方（右端常被出場標記或圖邊擋住）
            // 紀錄的是平倉當下的值；有移動停損的策略，進場時的值可能不同
            string s = name + " " + v.ToString("N" + data.PriceDigits, CultureInfo.InvariantCulture) + "（平倉時）";
            var sz = g.MeasureString(s, Font);
            float tx = Math.Max(Plot.Left + 2, Math.Min(Plot.Right - sz.Width - 2, x1 + 6 * k));
            using (var bg = new SolidBrush(Color.FromArgb(220, Color.White))) g.FillRectangle(bg, tx, y - sz.Height - 1, sz.Width, sz.Height);
            using (var b = new SolidBrush(c)) g.DrawString(s, Font, b, tx, y - sz.Height - 1);
        }
    }
}
