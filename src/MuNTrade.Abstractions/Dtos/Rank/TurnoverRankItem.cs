using MuNTrade.Abstractions.Domain.Rank;

namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed class TurnoverRankItem
    {
        public int Rank { get; init; }
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = "-";
        public string Market { get; init; } = "-";
        public decimal TurnoverRatePercent { get; init; }
        public InstrumentKind Kind { get; init; }
    }
}

