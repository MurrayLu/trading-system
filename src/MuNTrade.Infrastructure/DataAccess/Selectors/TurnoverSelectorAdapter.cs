using MuNTrade.Abstractions.Contracts;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MuNTrade.Infrastructure.DataAccess.Selectors
{
    /// <summary>
    /// 將 ITurnoverSymbolSelector 適配為 ISymbolSelector 介面的轉接器。
    /// 讓舊有的、依賴 ISymbolSelector 的服務可以在新模式下無縫運作。
    /// </summary>
    internal sealed class TurnoverSelectorAdapter : ISymbolSelector
    {
        private readonly ITurnoverSymbolSelector _innerSelector;
        private readonly IClock _clock;

        public TurnoverSelectorAdapter(ITurnoverSymbolSelector innerSelector, IClock clock)
        {
            _innerSelector = innerSelector;
            _clock = clock;
        }

        /// <summary>
        /// 獲取標的列表。注意：此處為同步等待非同步結果，僅適用於此特定轉接場景。
        /// </summary>
        public IEnumerable<string> GetSymbols()
        {
            return _innerSelector.GetSnapshotAsync(DateOnly.FromDateTime(_clock.Now.Date))
                                 .GetAwaiter()
                                 .GetResult()
                                 .Symbols;
        }
    }
}

