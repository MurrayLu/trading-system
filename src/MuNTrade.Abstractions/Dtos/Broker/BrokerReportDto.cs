using System;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Dtos.Broker
{
    /// <summary>
    /// 統一的回報資料 (由 GatewayFeedbackAdapter 解析出來)。
    /// </summary>
    public sealed class BrokerReportDto
    {
        public ExchangeTs ExchangeTs { get; init; }
        public ArrivalTs ArrivalTs { get; init; }
        public DateTime Time => ExchangeTs.Value; // Alias for UI binding

        // Core fields required by Blotter and Dock
        public string Action { get; init; } = "Unknown"; // e.g. New, Cancel, Trade
        public string Market { get; init; } = "Stock";   // Stock/Future
        public string Side { get; init; } = "";          // Buy/Sell
        public decimal Price { get; init; }              // Order Price or Trade Price
        public int Quantity { get; init; }               // Order Qty or Trade Qty
        public string Status { get; init; } = "";        // New, Partial, Filled, Canceled, Rejected

        // Original fields
        public string ReportType { get; init; } = "";  // Ack/Partial/Filled/Rejected/...
        public string Message { get; init; } = "";     // 原始訊息(可折疊顯示)
        public string Level { get; init; } = "Info";   // Info/Warn/Error

        public string? ClientKey { get; init; }
        public string CorrelationId { get; init; } = "";
        public string Symbol { get; init; } = "";
        public decimal? DecisionPrice { get; init; }
        public decimal? FillPrice { get; init; }
        public int? LatencyMs { get; init; }      // Intent → 這筆回報
        public int? AccLatencyMs { get; init; }   // Intent → FullFill
        public decimal? Slippage { get; init; }   // Fill - Decision

        // 帳戶區分 (Paper/Live)
        public string Account { get; init; } = "";

        // 委託書號 (BookNo)
        public string BookNo { get; init; } = "";

        // 券商序號 (SeqNo)
        public string SeqNo { get; init; } = "";

        // 原始數量 (API Raw Quantity, e.g. shares for stocks) - 用於改量計算
        public int RawQuantity { get; init; }
    }
}

