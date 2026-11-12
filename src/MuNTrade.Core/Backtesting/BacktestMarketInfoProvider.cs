using System;
using System.Collections.Concurrent;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest 用簡易 MarketInfo Provider：無即時快照時以符號規則推斷。
    /// </summary>
    public sealed class BacktestMarketInfoProvider : IMarketInfoProvider
    {
        private readonly ConcurrentDictionary<string, MarketInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetMarketInfo(string symbol, out MarketInfo info)
        {
            info = MarketInfo.Unknown(symbol ?? string.Empty);
            if (string.IsNullOrWhiteSpace(symbol)) return false;

            if (_cache.TryGetValue(symbol, out var cached))
            {
                info = cached;
                return info.MarketNo != MarketNo.Unknown;
            }

            info = Infer(symbol);
            _cache[symbol] = info;
            return info.MarketNo != MarketNo.Unknown;
        }

        public MarketInfo GetMarketInfoOrThrow(string symbol, MarketInfoPurpose purpose)
        {
            if (TryGetMarketInfo(symbol, out var info) && info.MarketNo != MarketNo.Unknown)
                return info;

            if (purpose is MarketInfoPurpose.Risk or MarketInfoPurpose.Execution)
                throw new InvalidOperationException($"BacktestMarketInfoProvider: Unable to resolve MarketInfo for symbol={symbol}.");

            return MarketInfo.Unknown(symbol ?? string.Empty);
        }

        public void UpsertFromSnapshot(string symbol, MarketNo marketNo, MarketSource source)
        {
            if (string.IsNullOrWhiteSpace(symbol) || marketNo == MarketNo.Unknown) return;
            var kind = marketNo == MarketNo.Futures
                ? MarketKind.Derivatives
                : (marketNo == MarketNo.Unknown ? MarketKind.Unknown : MarketKind.Securities);
            _cache[symbol] = new MarketInfo(symbol, marketNo, kind, source);
        }

        private static MarketInfo Infer(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return MarketInfo.Unknown(symbol ?? string.Empty);

            // 簡易規則：TX 開頭視為期貨，其他視為證券
            if (symbol.StartsWith("TX", StringComparison.OrdinalIgnoreCase))
            {
                return new MarketInfo(symbol, MarketNo.Futures, MarketKind.Derivatives, MarketSource.Domestic);
            }

            return new MarketInfo(symbol, MarketNo.Listing, MarketKind.Securities, MarketSource.Domestic);
        }
    }
}

