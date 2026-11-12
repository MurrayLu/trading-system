namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// 風控模組發出的指令：移動停損點至新價位
    /// </summary>
    public sealed record RiskActionMoveStop(string Symbol, StopSpec NewStop) : ITradingAction;
}
