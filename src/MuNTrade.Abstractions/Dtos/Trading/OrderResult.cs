namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record OrderResult(
        string OrderId,
        bool Accepted,
        bool FullyFilled,
        int FilledQuantity,
        decimal? AvgFillPrice,
        string? RejectReason
    );
}
