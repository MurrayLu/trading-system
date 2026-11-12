using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Domain.MarketData
{
    /// <summary>
    /// 代表從周轉率 CSV 檔案讀取的一筆記錄。
    /// 這是一個資料傳輸物件 (DTO)，定義了外部資料的結構。
    /// </summary>
    public record TurnoverRecord
    {
        public string Symbol { get; init; } = default!;   // ← 對應 CSV 的第一欄
        public decimal Turnover { get; init; }              // ← 對應 CSV 的第二欄
    }
}

