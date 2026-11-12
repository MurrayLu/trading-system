namespace MuNTrade.Abstractions.Domain.Capital
{
    public record CapitalSnapshot(
        string AccountKey,
        decimal Equity,
        decimal BuyingPower,
        decimal DailyPnL
    );
}

