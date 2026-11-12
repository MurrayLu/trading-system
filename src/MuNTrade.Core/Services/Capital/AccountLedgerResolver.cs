using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Common.Enums;
using System;

namespace MuNTrade.Core.Services.Capital
{
    public sealed class AccountLedgerResolver
    {
        private readonly IMarketInfoProvider _market;

        public AccountLedgerResolver(IMarketInfoProvider market)
        {
            _market = market;
        }

        public LedgerKey ResolveOrThrow(string accountKey, string symbol, MarketInfoPurpose purpose)
        {
            if (string.IsNullOrWhiteSpace(accountKey))
                throw new InvalidOperationException("AccountKey is required.");

            var info = _market.GetMarketInfoOrThrow(symbol, purpose); // Risk/Execution 最高安全
            return new LedgerKey(accountKey, info.Kind);
        }
    }
}

