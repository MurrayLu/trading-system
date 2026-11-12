using System;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Dtos.Broker;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    public interface IOrderIdentityMap
    {
        OrderKey UpdateFromReport(BrokerReportDto dto);
        bool TryGetByKey(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out OrderKey orderKey);
        bool TryGetLastUpdateUtc(string key, out DateTime lastUpdateUtc);
    }
}

