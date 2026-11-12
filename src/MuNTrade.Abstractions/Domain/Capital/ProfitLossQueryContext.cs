namespace MuNTrade.Abstractions.Domain.Capital
{
    public enum ProfitLossQueryType
    {
        Unrealized = 0,
        Realized = 1,
        DayTrade = 2
    }

    public enum ProfitLossFormat
    {
        Summary = 1,
        Detail = 2
    }

    public sealed record ProfitLossQueryContext
    {
        public string Account { get; init; } = "";
        public ProfitLossQueryType QueryType { get; init; }
        public ProfitLossFormat Format { get; init; }
        public System.DateTime? StartDate { get; init; }
        public System.DateTime? EndDate { get; init; }
    }
}

