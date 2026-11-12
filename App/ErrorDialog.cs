using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace BacktesterApp
{
    // 可複製內容的錯誤視窗
    internal static class ErrorDialog
    {
        public static void Show(IWin32Window owner, string title, string message, Exception ex = null)
        {
            try
            {
                Control c = owner as Control;
                if (c != null && !c.IsDisposed && c.InvokeRequired)
                {
                    c.Invoke(new Action(() => ShowCore(owner, title, message, ex)));
                    return;
                }
                ShowCore(owner, title, message, ex);
            }
            catch { }
        }

        private static void ShowCore(IWin32Window owner, string title, string message, Exception ex)
        {
            string full = (message ?? "") + (ex != null ? Environment.NewLine + Environment.NewLine + ex : "");
            Font font = new Font("Microsoft JhengHei", 12f);
            using (Form f = new Form())
            {
                f.Text = string.IsNullOrEmpty(title) ? "發生錯誤" : title;
                f.Font = font;
                f.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                f.ClientSize = new Size(760, 480);
                f.MinimumSize = new Size(520, 320);
                f.ShowInTaskbar = owner == null;

                Label lb = new Label
                {
                    Text = "程式發生錯誤。可按「複製」把內容貼給客服或開發者，日誌也記有同樣的內容。",
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 56,
                    Padding = new Padding(12, 12, 12, 0)
                };
                TextBox tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Microsoft JhengHei", 11f),
                    BackColor = Color.White,
                    Text = full
                };
                Panel pbox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };
                pbox.Controls.Add(tb);

                FlowLayoutPanel bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 56,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(12, 8, 12, 8)
                };
                Button bClose = new Button { Text = "關閉", AutoSize = true, MinimumSize = new Size(96, 36), DialogResult = DialogResult.OK };
                Button bFolder = new Button { Text = "開啟日誌資料夾", AutoSize = true, MinimumSize = new Size(150, 36) };
                Button bCopy = new Button { Text = "複製", AutoSize = true, MinimumSize = new Size(96, 36) };
                bCopy.Click += delegate
                {
                    try { Clipboard.SetText(full); bCopy.Text = "已複製"; }
                    catch { bCopy.Text = "複製失敗"; }
                };
                bFolder.Click += delegate
                {
                    try
                    {
                        string dir = AppLog.LogFolder;
                        System.IO.Directory.CreateDirectory(dir);
                        Process.Start("explorer.exe", "\"" + dir + "\"");
                    }
                    catch { }
                };
                bar.Controls.Add(bClose);
                bar.Controls.Add(bFolder);
                bar.Controls.Add(bCopy);

                f.Controls.Add(pbox);
                f.Controls.Add(lb);
                f.Controls.Add(bar);
                f.AcceptButton = bClose;
                f.CancelButton = bClose;

                if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
            }
        }
    }
}
