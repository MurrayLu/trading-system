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
            // Sync load for now, assuming Repository caches state or fast IO
            // In real world, this should be cached service
            var state = _repository.LoadAsync().Result; // Caution: Sync over Async for interface compatibility if GetSnapshot is sync.
            // Warning: GetSnapshot is defined as synchronous in ICapitalProvider because Risk check is usually sync.
            // If repository is async, we should cache it. 
            // For MVP 10C, we assume risk is low or we'll refactor to Async later.
            // Or better: Use CapitalStateService directly if available, or assume CapitalAllocationService holds state.

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

