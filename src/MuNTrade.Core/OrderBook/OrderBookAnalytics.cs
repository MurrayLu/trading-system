using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Settings;
using System;
using System.Linq;

namespace MuNTrade.Core.OrderBook
{
    public interface IOrderBookAnalytics
    {
        MicrostructureScores Score(ReadOnlySpan<JoinedTradeQuote> window);
    }

    internal sealed class OrderBookAnalytics : IOrderBookAnalytics
    {
        private readonly IOptionsMonitor<OrderBookProcessingOptions> _options;
        private readonly ILogger<OrderBookAnalytics> _logger;

        public OrderBookAnalytics(IOptionsMonitor<OrderBookProcessingOptions> options, ILogger<OrderBookAnalytics> logger)
        {
            _options = options;
            _logger = logger;
        }

        public MicrostructureScores Score(ReadOnlySpan<JoinedTradeQuote> window)
        {
            if (window.IsEmpty)
                throw new ArgumentException("Window must contain at least one sample.", nameof(window));

            var start = window[0].ArrivalTs;
            var end = window[^1].ArrivalTs;
            var symbol = window[^1].Symbol;

            double absorptionScore = 0;
            int refillCount = 0;
            int refillVol = 0;
            double sweepScore = 0;
            int levelsTraversed = 0;
            double depletionAccumulator = 0;
            double buyAggressor = 0;
            double sellAggressor = 0;
            int maxFrontWall = 0;
            double imbalanceAccumulator = 0;
            int imbalanceSamples = 0;
            ArrivalTs? lastTs = null;

            for (int i = 0; i < window.Length; i++)
            {
                ref readonly var jtq = ref window[i];

                if (lastTs.HasValue && jtq.ArrivalTs < lastTs.Value)
                {
                    _logger.LogWarning("[Invariant Violation] ArrivalTs regression detected in window! Symbol={Symbol} Prev={Prev} Curr={Curr}",
                        symbol, lastTs.Value, jtq.ArrivalTs);
                }

                lastTs = jtq.ArrivalTs;
                bool isBuy = jtq.PreBestAsk > 0 && jtq.TradePrice >= jtq.PreBestAsk;
                bool isSell = jtq.PreBestBid > 0 && jtq.TradePrice <= jtq.PreBestBid;

                if (isBuy) buyAggressor++;
                else if (isSell) sellAggressor++;

                if (isBuy || isSell)
                {
                    var preDepth = isBuy ? jtq.PreAsk1 : jtq.PreBid1;
                    var postDepth = isBuy ? jtq.PostAsk1 : jtq.PostBid1;
                    var consumed = Math.Max(0, preDepth - postDepth);
                    if (preDepth > 0)
                    {
                        var normalized = (double)Math.Min(jtq.TradeQty, preDepth) / preDepth;
                        absorptionScore += normalized;
                        depletionAccumulator += (double)Math.Min(consumed, preDepth) / preDepth;
                    }

                    if (postDepth > preDepth)
                    {
                        refillCount++;
                        refillVol += postDepth - preDepth;
                    }

                    if (preDepth > maxFrontWall)
                    {
                        maxFrontWall = preDepth;
                    }
                }

                if (isBuy && jtq.PostBestAsk > jtq.PreBestAsk && jtq.PreBestAsk > 0)
                {
                    sweepScore += 1;
                    levelsTraversed++;
                }
                else if (isSell && jtq.PostBestBid < jtq.PreBestBid && jtq.PreBestBid > 0)
                {
                    sweepScore += 1;
                    levelsTraversed++;
                }
                else if (jtq.PostBestBid != jtq.PreBestBid || jtq.PostBestAsk != jtq.PreBestAsk)
                {
                    levelsTraversed++;
                }

                var bidSum = jtq.PreBidDepth?.Sum() ?? 0;
                var askSum = jtq.PreAskDepth?.Sum() ?? 0;
                var totalDepth = bidSum + askSum;
                if (totalDepth > 0)
                {
                    imbalanceAccumulator += (double)(bidSum - askSum) / totalDepth;
                    imbalanceSamples++;
                }
            }

            var totalAgg = buyAggressor + sellAggressor;
            var buyProb = totalAgg > 0 ? buyAggressor / totalAgg : 0.5;
            var sellProb = totalAgg > 0 ? sellAggressor / totalAgg : 0.5;
            var length = window.Length;

            absorptionScore /= length;
            sweepScore /= length;
            depletionAccumulator /= length;
            var avgImbalance = imbalanceSamples > 0 ? imbalanceAccumulator / imbalanceSamples : 0;

            return new MicrostructureScores(
                symbol,
                start,
                end,
                absorptionScore,
                refillCount,
                refillVol,
                sweepScore,
                levelsTraversed,
                depletionAccumulator,
                buyProb,
                sellProb,
                maxFrontWall,
                avgImbalance);
        }
    }
}

