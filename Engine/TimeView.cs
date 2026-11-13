using System;
using System.Globalization;

namespace CtaBacktest
{
    // 畫面與報告的時間顯示時區（spec.md 15）。只影響顯示；資料與策略一律 UTC
    public static class TimeView
    {
        public static bool Taiwan = true;

        public static string Label { get { return Taiwan ? "台灣時間" : "UTC"; } }

        public static DateTime Show(DateTime utc)
        {
            return Taiwan ? utc.AddHours(8) : utc;
        }

        public static string Fmt(DateTime utc, string format)
        {
            return Show(utc).ToString(format, CultureInfo.InvariantCulture);
        }

        // 顯示時區的日期（當天 00:00）轉成 UTC
        public static DateTime DateToUtc(DateTime shownDate)
        {
            var d = DateTime.SpecifyKind(shownDate.Date, DateTimeKind.Utc);
            return Taiwan ? d.AddHours(-8) : d;
        }
    }
}
