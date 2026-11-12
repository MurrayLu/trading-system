namespace MuNTrade.Abstractions.Domain.MarketData.Enums
{
    public enum TickAttachState
    {
        None,
        Attaching,
        Attached,
        RetryPending,
        Failed,
        Disabled
    }
}

