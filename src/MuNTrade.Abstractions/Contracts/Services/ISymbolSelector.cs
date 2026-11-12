using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>
    /// 契約：定義「選股器」的職責。
    /// 它只回答一個問題：「我們現在應該關注哪些商品？」
    /// </summary>
    public interface ISymbolSelector
    {
        IEnumerable<string> GetSymbols();
    }
}
