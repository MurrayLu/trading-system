using MuNTrade.Abstractions.Domain.Capital;

namespace MuNTrade.Abstractions.Contracts.Capital
{
    public interface ICapitalProvider
    {
        CapitalSnapshot GetSnapshot(LedgerKey ledgerKey);
    }
}

