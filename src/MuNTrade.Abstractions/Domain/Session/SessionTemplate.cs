using System;

namespace MuNTrade.Abstractions.Domain.Session
{
    /// <summary>
    /// 代表從 JSON 設定檔中讀取的原始時段範本。
    /// </summary>
    public class SessionTemplate
    {
        public string Label { get; set; } = string.Empty;
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public bool CrossesMidnight { get; set; }
        public bool IsNextDaySession { get; set; }
        public TimeSpan Anchor { get; set; }

        // Session values loaded from JSON configuration.
        public TimeSpan PreCloseAuctionStart { get; set; }
        public TimeSpan PreCloseExtension { get; set; }
    }
}
