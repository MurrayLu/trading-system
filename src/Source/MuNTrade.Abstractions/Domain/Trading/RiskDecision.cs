namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 代表一次風險評估的決策結果。
    /// 這是一個不可變的(immutable)結構，確保決策一旦做出就不會被更改。
    /// </summary>
    public readonly record struct RiskDecision
    {
        /// <summary>
        /// 決策是否為「核准」。
        /// </summary>
        public bool IsApproved { get; }

        /// <summary>
        /// 若決策為「拒絕」，此處為拒絕的理由。
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Backtest Stage or policy tag (optional).
        /// </summary>
        public string Stage { get; }

        /// <summary>
        /// Remaining daily loss budget at decision time (optional).
        /// </summary>
        public decimal DailyLossRemaining { get; }

        // 私有建構式，強制外部只能透過 Approve() 和 Reject() 方法來建立實例
        private RiskDecision(bool isApproved, string reason, string stage, decimal dailyLossRemaining)
        {
            IsApproved = isApproved;
            Reason = reason;
            Stage = stage;
            DailyLossRemaining = dailyLossRemaining;
        }

        // --- 以下為輔助方法 (Factory Methods)，讓程式碼更易讀 ---

        /// <summary>
        /// 建立一個「核准」的決策。
        /// </summary>
        public static RiskDecision Approve(string stage = "", decimal dailyLossRemaining = 0m)
        {
            return new RiskDecision(true, string.Empty, stage, dailyLossRemaining);
        }

        /// <summary>
        /// 建立一個帶有原因的「拒絕」決策。
        /// </summary>
        /// <param name="reason">拒絕的原因。</param>
        public static RiskDecision Reject(string reason, string stage = "", decimal dailyLossRemaining = 0m)
        {
            return new RiskDecision(false, reason ?? "Unknown Reason", stage, dailyLossRemaining);
        }
    }
}

