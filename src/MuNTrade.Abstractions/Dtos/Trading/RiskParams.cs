namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record RiskParams(
        decimal InitialStopPrice,
        decimal RiskPerShare,     // |Entry-Stop|
        decimal MinRMultiple,     // 例如 2 或 3
        decimal? TargetRMultiple  // null 代表只靠動態出場
    );
}
