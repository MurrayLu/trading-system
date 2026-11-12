using System;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Abstractions.Contracts.Services
{
    [Obsolete("Yahoo provider is deprecated. WantGoo-only rank is used.")]
    public interface IYahooTurnoverRankProvider
    {
        Task<YahooTurnoverRankSnapshot> FetchTopAsync(int topN, CancellationToken ct);
    }
}

