namespace MuNTrade.Abstractions.Domain.Trading.Enums
{
    /// <summary>
    /// 定義錯過交易機會的原因。
    /// </summary>
    public enum MissedReason
    {
        GuardRejected,          // 被成交品質守門員拒絕
        RiskTooHigh,            // 計算後發現單筆風險過高
        InsufficientHeadroom,   // 獲利空間不足
        NotFilled,              // 掛單未成交
        Disabled,               // 系統或策略被手動禁用
        Other                   // 其他原因
    }
}

