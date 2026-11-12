using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Trading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Logging
{
    public class InMemoryOpportunityCostLogger : IOpportunityCostLogger
    {
        private readonly ConcurrentBag<MissedOpportunity> _missedOpportunities = new();

        public Task LogAsync(MissedOpportunity item, CancellationToken ct = default)
        {
            _missedOpportunities.Add(item);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MissedOpportunity>> QueryAsync(string symbol, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var result = _missedOpportunities
                .Where(mo => mo.Opportunity.Candidate.Symbol == symbol &&
                             mo.LoggedAt.Value >= from &&
                             mo.LoggedAt.Value <= to)
                .ToList()
                .AsReadOnly();

            return Task.FromResult<IReadOnlyList<MissedOpportunity>>(result);
        }
    }
}

