using System;

namespace MuNTrade.Abstractions.Domain.Trading
{
    public sealed record OrderKey
    {
        public string Key { get; init; } = Guid.NewGuid().ToString("N");
        public bool IsPaper { get; init; }
        public bool IsFutures { get; init; }
        public string? ClientKey { get; init; }
        public string? BrokerBookNo { get; init; }
        public string? BrokerSeqNo { get; init; }
        public string? PaperOrderId { get; init; }
        public string? CorrelationId { get; init; }
        public string Symbol { get; init; } = "";
    }
}

