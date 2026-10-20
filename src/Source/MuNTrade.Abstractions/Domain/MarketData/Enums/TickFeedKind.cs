namespace MuNTrade.Abstractions.Domain.MarketData.Enums
{
    /// <summary>
    /// Tick 資料流來源：歷史回補 / 即時串流
    /// </summary>
    public enum TickFeedKind
    {
        History = 0,
        RealTime = 1
    }
}

