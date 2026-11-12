using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Abstractions.Contracts.Capital
{
    public interface ICapitalStateRepository
    {
        Task<CapitalState> LoadAsync();
        Task SaveAsync(CapitalState state);
    }
}

