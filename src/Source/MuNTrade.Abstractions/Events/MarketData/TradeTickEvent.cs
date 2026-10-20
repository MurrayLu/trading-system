using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using System;

namespace MuNTrade.Abstractions.Events.MarketData
{
    public class TradeTickEvent : MuNTrade.Abstractions.Contracts.Eventing.IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }

        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Tick 來源：History 回補 / RealTime 即時
        /// 預設 RealTime，避免未設定時誤判為 History。
        /// </summary>
        public TickFeedKind FeedKind { get; set; } = TickFeedKind.RealTime;

        /// <summary>
        /// 交易所撮合時間 (Exchange Time)。
        /// 用途：K 線圖 (OHLC)、策略判斷盤中/盤後、成交量統計。
        /// 特性：這是神聖不可修改的交易所官方時間。
        /// </summary>
        public ExchangeTs ExchangeTs { get; set; }

        /// <summary>
        /// [Obsolete] 請改用 ExchangeTs。
        /// </summary>
        [Obsolete("Use ExchangeTs instead.")]
        public DateTime Timestamp { get => ExchangeTs.Value; set => ExchangeTs = new ExchangeTs(value); }

        /// <summary>
        /// [新增] 本機接收時間 (Arrival Time / Broker Time)。
        /// 用途：與 Best5 (五檔) 進行時間配對 (Joining)。
        /// 特性：這是你的電腦收到資料的時間，與 Best5 的時間基準一致。
        /// </summary>
        public ArrivalTs ArrivalTs { get; set; }

        /// <summary>
        /// [Obsolete] 請改用 ArrivalTs。
        /// </summary>
        [Obsolete("Use ArrivalTs instead.")]
        public DateTime ArrivalTimestamp { get => ArrivalTs.Value; set => ArrivalTs = new ArrivalTs(value); }

        public MarketNo MarketNo { get; set; }
        public MarketSource Source { get; set; } = MarketSource.Domestic; // 預設國內，避免舊程式碼未初始化出錯

        public int StockIndex { get; set; }
        public int TickPtr { get; set; }
        public int Date { get; set; }
        public int TimeHms { get; set; }
        public int TimeMillisMicros { get; set; }

        public decimal Price { get; set; }
        public int Volume { get; set; }  // ★ 單筆量 int

        public bool IsSimulated { get; set; }
        public decimal BidAtTrade { get; set; }
        public decimal AskAtTrade { get; set; }
        public AggressorSide Side { get; set; } = AggressorSide.Unknown;
    }
}

