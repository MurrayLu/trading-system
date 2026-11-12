using System;
using System.Linq;
using MuNTrade.Abstractions.Contracts.Capital;
using MuNTrade.Abstractions.Domain.Capital;

namespace MuNTrade.Core.Capital
{
    public class SimpleCapitalProvider : ICapitalProvider
    {
        private readonly ICapitalStateRepository _repository;

        public SimpleCapitalProvider(ICapitalStateRepository repository)
        {
            _repository = repository;
        }

        public CapitalSnapshot GetSnapshot(LedgerKey ledgerKey)
        {
            // The provider interface is synchronous, so the repository load is synchronously awaited.
            var state = _repository.LoadAsync().Result;

            var account = state.Accounts.FirstOrDefault(a => a.Id == ledgerKey.AccountKey)
                       ?? state.Accounts.FirstOrDefault(a => a.Name == ledgerKey.AccountKey);

            if (account == null)
            {
                return new CapitalSnapshot(ledgerKey.AccountKey, 0, 0, 0);
            }

            // Calculation based on what we have
            decimal equity = account.InitialEquity + account.RealizedPnL;
            // BuyingPower logic? Fallback to Equity.
            return new CapitalSnapshot(account.Id, equity, equity, 0);
        }
    }
}

