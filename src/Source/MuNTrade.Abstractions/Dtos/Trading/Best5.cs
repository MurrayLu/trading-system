using System.Collections.Generic;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record Best5(IReadOnlyList<BestLevel> Bids, IReadOnlyList<BestLevel> Asks);
}
