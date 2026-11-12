using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Core.Settings;
using System.Collections.Generic;

namespace MuNTrade.Infrastructure.DataAccess.Selectors
{
    /// <summary>
    /// 一個簡單的選股器，從設定檔中讀取一個靜態的觀察名單。
    /// </summary>
    public class StaticListSelector : ISymbolSelector
    {
        private readonly BacktestSettings _backtestSettings;

        public StaticListSelector(IOptions<BacktestSettings> backtestOptions)
        {
            _backtestSettings = backtestOptions.Value;
        }

        public IEnumerable<string> GetSymbols()
        {
            return _backtestSettings.StockIds;
        }
    }
}
