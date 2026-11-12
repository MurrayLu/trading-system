namespace MuNTrade.Abstractions.Dtos.Trading
{
    /// <summary>
    /// 利潤捕捉模組發出的指令：依據目標進行部分平倉（分批出場）
    /// </summary>
    public sealed record ProfitActionScaleOut(string Symbol, ProfitTarget Target) : ITradingAction;
}
