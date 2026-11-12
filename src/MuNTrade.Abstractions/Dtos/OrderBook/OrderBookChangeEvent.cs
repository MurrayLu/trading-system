using MuNTrade.Abstractions.Domain.OrderBook.Enums;

namespace MuNTrade.Abstractions.Dtos.OrderBook
{
    // DTO item inside OrderBookUpdateEvent; not published to event bus
    public record OrderBookChangeEvent(
        decimal Price,
        int Delta,
        OrderBookAction Action,
        bool IsBid
    );
}
