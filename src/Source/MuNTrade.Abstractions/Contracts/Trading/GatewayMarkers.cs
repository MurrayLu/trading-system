using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.Trading;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    // 讓 DI 可以同時注入兩套，不要再搶同一個 IOrderGateway
    public interface IPaperOrderGateway : IOrderGateway { }
    // LIVE gateway intentionally absent in offline candidate.
}

