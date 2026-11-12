using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Capital
{
    public readonly record struct LedgerKey(string AccountKey, MarketKind MarketKind);
}

