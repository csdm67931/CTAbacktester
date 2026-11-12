using System;
using System.Drawing;
using System.Windows.Forms;
using CtaBacktest;

namespace BacktesterApp
{
    // 第一次啟動（或免責文字改版後）的說明視窗
    internal static class DisclaimerForm
    {
        // acceptedVersion 已大於等於目前版本就直接放行；否則顯示視窗，回傳是否按了「我了解」
        public static bool EnsureAccepted(IWin32Window owner, int acceptedVersion)
        {
            if (acceptedVersion >= Disclaimer.Version) return true;

            using (Form f = new Form())
            {
                f.Text = "使用前請先閱讀";
                f.Font = new Font("Microsoft JhengHei", 12f);
                f.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(760, 560);
                f.MinimumSize = new Size(560, 400);
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = owner == null;

                TextBox tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    WordWrap = true,
                    Dock = DockStyle.Fill,
                    BackColor = Color.White,
                    Text = Disclaimer.Text
                };
                Panel pbox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 8) };
                pbox.Controls.Add(tb);

                // 按鈕列自動排版（由右往左），任何 DPI 縮放都看得到、按得到；不用先打勾
                FlowLayoutPanel bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Padding = new Padding(12, 8, 12, 12)
                };
                Button bExit = new Button { Text = "離開", AutoSize = true, MinimumSize = new Size(110, 40), DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
                Button bOk = new Button { Text = "我了解，開始使用", AutoSize = true, MinimumSize = new Size(170, 40), DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
                bar.Controls.Add(bExit);
                bar.Controls.Add(bOk);

                f.Controls.Add(pbox);
                f.Controls.Add(bar);
                f.AcceptButton = bOk;
                f.CancelButton = bExit;
                f.Shown += delegate { bOk.Focus(); };

                DialogResult r = owner != null ? f.ShowDialog(owner) : f.ShowDialog();
                return r == DialogResult.OK;
            }
        }
    }
}
