using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record OrderFillLight( // 若已有 OrderFillEvent 就不用此型別
        string OrderId,
        string Symbol,
        OrderSide Side,
        int FilledQuantity,
        decimal AvgFillPrice,
        Liquidity Liquidity,
        ArrivalTs FilledAt
    );
}
