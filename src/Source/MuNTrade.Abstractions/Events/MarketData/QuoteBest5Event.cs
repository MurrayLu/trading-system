// MuNTrade.Abstractions/Models/Events/QuoteBest5Event.cs
using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// 代表完整的最佳五檔深度快照，對應 OnNotifyBest5LONG 事件。
    /// </summary>
    public class QuoteBest5Event : MuNTrade.Abstractions.Contracts.Eventing.IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }

        public string Symbol { get; set; } = string.Empty;
        public ArrivalTs ArrivalTs { get; set; }

        public TickFeedKind FeedKind { get; set; } = TickFeedKind.RealTime;

        [Obsolete("Use ArrivalTs instead.")]
        public DateTime Timestamp { get => ArrivalTs.Value; set => ArrivalTs = new ArrivalTs(value); }

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 sMarketNo 參數。
        /// </summary>
        public MarketNo MarketNo { get; set; }

        public MarketSource Source { get; set; } = MarketSource.Domestic;

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nStockidx 參數。
        /// </summary>
        public int StockIndex { get; set; }

        public List<PriceLevel> Bids { get; set; } = new(5);
        public List<PriceLevel> Asks { get; set; } = new(5);

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nExtendBid 參數。
        /// </summary>
        public decimal ExtendBidPrice { get; set; }

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nExtendBidQty 參數。
        /// </summary>
        public int ExtendBidVolume { get; set; }

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nExtendAsk 參數。
        /// </summary>
        public decimal ExtendAskPrice { get; set; }

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nExtendAskQty 參數。
        /// </summary>
        public int ExtendAskVolume { get; set; }

        /// <summary>
        /// 對應 OnNotifyBest5LONG 的 nSimulate 參數 (0: 一般揭示, 1: 試算揭示)。
        /// </summary>
        public bool IsSimulated { get; set; }
    }
}

