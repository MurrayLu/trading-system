using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Contracts.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Indicators
{
    public sealed class IndicatorCalculatorActivator : IHostedService
    {
        private readonly IIndicatorCalculator _calc;

        public IndicatorCalculatorActivator(IIndicatorCalculator calc)
        {
            _calc = calc; // constructor injection 會保證實例建立
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // nothing else needed — 只是確保實例被 build 且 constructor (與 event-subscribe) 被執行
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

