namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed class StockMasterEntry
    {
        public string StockNo { get; init; } = string.Empty;
        public string StockName { get; init; } = string.Empty;
        public string Market { get; init; } = "-";
    }
}

