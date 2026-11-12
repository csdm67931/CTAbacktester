using System;
using System.Net;
using System.Text;

namespace CtaBacktest
{
    // 免責與差異揭露文字。內容以 README「和實盤可能不同的地方」為準。
    public static class Disclaimer
    {
        // 文字有實質修改時加一，使用者會被要求重新確認
        public const int Version = 1;

        private static readonly string[] Items = new string[]
        {
            "報價由 K 棒模擬產生，不是逐筆報價；策略在回測器裡執行的是模擬的交易王介面，不是真正的交易王。",
            "點差為固定值，不會隨時間變動。",
            "不計隔夜利息（群益每天公告、每天變動）。",
            "沒有下單延遲，委託與成交視為即時。",
            "非美元報價的商品，損益與手續費用換算貨幣對的歷史 K 棒開盤價換算美元；價格是 K 棒開盤價，不是逐筆報價。找不到歷史價格或自行改過匯率時，用固定匯率。",
            "內建指標公式照業界通用算法，沒有和交易王逐值核對。",
            "平台實際給多長的歷史由交易王決定。"
        };

        private const string Head = "回測結果只是以過去資料模擬的結果，不代表未來績效，也不構成任何投資建議。實際交易請自行評估風險。";
        private const string Sub = "回測與實盤可能不同的地方：";

        public static string Text
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(Head);
                sb.AppendLine();
                sb.AppendLine(Sub);
                for (int i = 0; i < Items.Length; i++)
                    sb.AppendLine("  " + (i + 1) + ". " + Items[i]);
                return sb.ToString().TrimEnd();
            }
        }

        // 給報告頁首用的 HTML 片段
        public static string ReportHtml()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<div class=\"disclaimer\">");
            sb.Append("<p><b>").Append(WebUtility.HtmlEncode(Head)).Append("</b></p>");
            sb.Append("<p>").Append(WebUtility.HtmlEncode(Sub)).Append("</p><ul>");
            foreach (string it in Items)
                sb.Append("<li>").Append(WebUtility.HtmlEncode(it)).Append("</li>");
            sb.Append("</ul></div>");
            return sb.ToString();
        }
    }
}
