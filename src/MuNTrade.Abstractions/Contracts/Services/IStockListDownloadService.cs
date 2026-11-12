using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Abstractions.Contracts.Services
{
    public interface IStockListDownloadService
    {
        Task<StockListDownloadResult> DownloadAsync(CancellationToken ct = default);
    }
}

