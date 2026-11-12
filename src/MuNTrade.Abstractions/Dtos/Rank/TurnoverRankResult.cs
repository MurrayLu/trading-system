using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Dtos.Rank
{
    public sealed record TurnoverRankResult(
        DateTimeOffset AsOf,
        int TotalCount,
        int StockCount,
        int NonStockCount,
        int UnknownCount,
        IReadOnlyList<TurnoverRankItem> Items
    );
}

