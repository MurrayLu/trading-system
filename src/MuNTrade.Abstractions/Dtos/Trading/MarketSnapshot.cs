using System.Collections.Generic;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Trading
{
    public sealed record MarketSnapshot(
        string Symbol,
        ExchangeTs ExchangeTs,
        ArrivalTs ArrivalTs,
        decimal LastPrice,
        long LastVolume,
        Best5 Best5,
        int PriceDecimals,
        int LotSize,
        decimal TickPrice,
        int SpreadTicks,
        int BestBidDepthSum,
        int BestAskDepthSum,
        decimal OfiShortTerm,
        decimal AvwapDeltaPct,
        bool IsAuctionOrVI
    )
    {
        public ExchangeTs LastTradeExchangeTs { get; init; }
        public ArrivalTs LastTradeArrivalTs { get; init; }
        public ArrivalTs LastBest5ArrivalTs { get; init; }
        public bool LastTradeIsSimulated { get; init; }
        public bool LastBest5IsSimulated { get; init; }

        public ArrivalTs FreshTs
        {
            get
            {
                var tradeTs = LastTradeIsSimulated ? default : LastTradeArrivalTs;
                var best5Ts = LastBest5IsSimulated ? default : LastBest5ArrivalTs;

                if (tradeTs == default && best5Ts == default)
                    return ArrivalTs;

                if (tradeTs == default) return best5Ts;
                if (best5Ts == default) return tradeTs;
                return (tradeTs > best5Ts) ? tradeTs : best5Ts;
            }
        }
    }
}
