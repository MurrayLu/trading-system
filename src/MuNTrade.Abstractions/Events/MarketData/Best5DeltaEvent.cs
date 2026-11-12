using System;
using MuNTrade.Abstractions.Time;
using System.Collections.Generic;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Domain.Common.Enums; // Fix Namespace
using MuNTrade.Abstractions.Domain.MarketData.Enums; // Keep if needed, but remove if unused or wrong

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// Best5 Delta Event with ArrivalTs.
    /// </summary>
    public sealed record Best5DeltaEvent(
        string Symbol,
        ArrivalTs ArrivalTs,
        IReadOnlyList<PriceLevel> Bids,
        IReadOnlyList<PriceLevel> Asks,
        MarketNo MarketNo,
        MarketSource Source
    )
    {
        public TickFeedKind FeedKind { get; init; } = TickFeedKind.RealTime;

        public decimal BestBid => Bids.Count > 0 ? Bids[0].Price : 0;
        public decimal BestAsk => Asks.Count > 0 ? Asks[0].Price : 0;

        public int[] BidDepth => GetDepthArray(Bids);
        public int[] AskDepth => GetDepthArray(Asks);

        private static int[] GetDepthArray(IReadOnlyList<PriceLevel> levels)
        {
            var depth = new int[5];
            int count = Math.Min(5, levels.Count);
            for (int i = 0; i < count; i++)
            {
                depth[i] = levels[i].Volume;
            }
            return depth;
        }
    }
}

