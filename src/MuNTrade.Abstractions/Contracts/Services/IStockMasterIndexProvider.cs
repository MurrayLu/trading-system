using MuNTrade.Abstractions.Dtos.Rank;

namespace MuNTrade.Abstractions.Contracts.Services
{
    public interface IStockMasterIndexProvider
    {
        bool TryGet(string stockNo, out StockMasterEntry entry);
    }
}

