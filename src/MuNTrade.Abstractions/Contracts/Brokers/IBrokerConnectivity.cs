using System;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;

namespace MuNTrade.Abstractions.Contracts.Brokers
{
    /// <summary>
    /// 券商連線狀態的極簡抽象。Core/Infrastructure 只需要知道「是否連上」及「狀態變更」。
    /// </summary>
    public interface IBrokerConnectivity
    {
        ConnectionState CurrentState { get; }
        /// <summary>狀態變更事件（例如 Connected/Disconnected）。</summary>
        event Action<ConnectionState, string>? StateChanged;

        string? StockAccount { get; }
        string? FuturesAccount { get; }
        string? AccountName { get; }
    }
}

