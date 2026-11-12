namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed record StockListDownloadResult(
        int ListingCount,
        int OtcCount,
        string ListingPath,
        string OtcPath);
}

