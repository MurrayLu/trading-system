using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Dtos.Rank
{
    [Obsolete("Yahoo rank snapshot is deprecated. WantGoo-only rank is used.")]
    public sealed record YahooTurnoverRankEntry(
        string RawSymbol,
        string StockNo,
        int Rank
    );

    [Obsolete("Yahoo rank snapshot is deprecated. WantGoo-only rank is used.")]
    public sealed record YahooTurnoverRankSnapshot(
        DateTimeOffset AsOf,
        IReadOnlyList<YahooTurnoverRankEntry> Items
    );
}

