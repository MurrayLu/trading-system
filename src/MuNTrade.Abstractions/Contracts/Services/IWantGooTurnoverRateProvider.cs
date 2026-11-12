using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Abstractions.Contracts.Services
{
    public interface IWantGooTurnoverRateProvider
    {
        Task<IReadOnlyList<WantGooTurnoverRateRawItem>> FetchAllAsync(CancellationToken ct);
    }
}

