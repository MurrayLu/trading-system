namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// 風控模組發出的指令：因特定原因平掉所有倉位
    /// </summary>
    public sealed record RiskActionCloseAll(string Symbol, string Reason) : ITradingAction;
}
