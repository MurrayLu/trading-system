using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record OrderIntent(
        string Symbol,
        OrderSide Side,
        ExecutionPlan Plan,
        string CorrelationId,
        ArrivalTs CreatedAt,
        string StrategyId,
        string AccountKey,
        MuNTrade.Abstractions.Domain.Common.Enums.MarketKind MarketKind = MuNTrade.Abstractions.Domain.Common.Enums.MarketKind.Unknown,
        MuNTrade.Abstractions.Domain.Common.Enums.MarketNo MarketNo = MuNTrade.Abstractions.Domain.Common.Enums.MarketNo.Unknown,
        string IntentKind = ""
    )
    {
        public TickFeedKind FeedKind { get; init; } = TickFeedKind.RealTime;
    }
}

