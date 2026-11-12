using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Hosting
{
    public class StrategyRegistry : IStrategyRegistry
    {
        private readonly IEnumerable<IStrategyTemplate> _templates;

        public StrategyRegistry(IEnumerable<IStrategyTemplate> templates)
        {
            _templates = templates;
        }

        public IEnumerable<IStrategyTemplate> Templates => _templates;

        public IStrategyTemplate? FindTemplate(string templateIdValue)
        {
             return _templates.FirstOrDefault(t => t.TemplateId.Value == templateIdValue);
        }
    }
}
