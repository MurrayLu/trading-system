namespace MuNTrade.Abstractions.Dtos.Capital
{
    /// <summary>
    /// 代表單一帳戶（證券或期貨）的詳細資訊
    /// </summary>
    public class AccountDetail
    {
        public string AccountId { get; set; } = string.Empty; // 帳號號碼
        public decimal Equity { get; set; }          // 權益總值
        public decimal BuyingPower { get; set; }     // 可用餘額/購買力
        public decimal RealizedPnL { get; set; }     // 今日已實現損益
        public decimal UnrealizedPnL { get; set; }   // 未實現損益 (浮動損益)

        //期貨專用欄位 ---
        public decimal? InitialMargin { get; set; }   // 部位原始保證金
        public decimal? MaintenanceMargin { get; set; } // 部位維持保證金
        public decimal? MarginLevel { get; set; }     // 維持率(%)
    }

    /// <summary>
    /// 彙總所有帳戶資訊和系統狀態
    /// </summary>
    public class AccountStatusDto
    {
        public string TradingMode { get; set; } = string.Empty; // "Paper" 或 "Live"
        public AccountDetail StockAccount { get; set; } = new();
        public AccountDetail FutureAccount { get; set; } = new();
    }
}
