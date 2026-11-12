using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.Trading;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    public interface IOrderManagementService
    {
        Task<string> CancelAsync(OrderKey orderKey);
        Task<string> ReduceAsync(OrderKey orderKey, int qty);
        Task<string> ReplacePriceAsync(OrderKey orderKey, decimal newPrice);
    }
}

