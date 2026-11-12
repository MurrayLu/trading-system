namespace MuNTrade.Abstractions.Domain.Capital
{
    public class CapitalState
    {
        // 多帳戶列表
        public List<AccountProfile> Accounts { get; set; } = new();
        // 目前選取的帳戶 ID
        public string ActiveAccountId { get; set; } = string.Empty;
    }

    // Compatibility model for stock account state.
    public class StockAccountState
    {
        public decimal InitialEquity { get; set; } = 90000m;
        public decimal RealizedPnL { get; set; }
        public decimal DayTradeQuotaUsed { get; set; }
    }

    // Persisted account profile.
    public class AccountProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Type { get; set; } = ""; // 如 Simulated、Real
        public string GatewayMode { get; set; } = "PAPER"; // PAPER or LIVE
        public decimal InitialEquity { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal UsedQuota { get; set; }
        public int CurrentPhaseIndex { get; set; } = 0; // Persisted Phase State

        // 用於實盤記錄今日損益，與歷史總損益分開
        public DateOnly DayPnLDate { get; set; } = DateOnly.MinValue;
        public decimal StockDayRealizedPnL { get; set; }
        public decimal FuturesDayRealizedPnL { get; set; }
    }
}

