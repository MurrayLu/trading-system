using System;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// Trade tick joined with pre/post best5 snapshots within an epsilon window.
    /// </summary>
    public sealed record JoinedTradeQuote(
        string Symbol,
        ExchangeTs ExchangeTs,
        ArrivalTs ArrivalTs,
        decimal TradePrice,
        int TradeQty,
        decimal PreBestBid,
        int PreBid1,
        int[] PreBidDepth,
        decimal PreBestAsk,
        int PreAsk1,
        int[] PreAskDepth,
        decimal PostBestBid,
        int PostBid1,
        int[] PostBidDepth,
        decimal PostBestAsk,
        int PostAsk1,
        int[] PostAskDepth
    );
}

