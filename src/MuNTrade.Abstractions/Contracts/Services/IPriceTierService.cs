using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>
    /// 提供根據價格查詢最小跳動單位 (Tick Price) 的服務。
    /// </summary>
    public interface IPriceTierService
    {
        /// <summary>
        /// 根據給定的價格，獲取其對應的最小跳動單位。
        /// </summary>
        /// <param name="price">商品價格。</param>
        /// <returns>最小跳動單位 (Tick Price)。</returns>
        /// <summary>
        /// 根據給定的價格，獲取其對應的最小跳動單位 (Tick Size)。
        /// </summary>
        decimal GetTickSizeAtPrice(decimal price);

        /// <summary>
        /// [兼容舊版] 等同於 GetTickSizeAtPrice。
        /// </summary>
        decimal GetTickPrice(decimal price);

        /// <summary>
        /// 取得往上跳一檔的價格 (Handle boundary change)。
        /// </summary>
        decimal GetNextUpPrice(decimal price);

        /// <summary>
        /// 取得往下跳一檔的價格 (Handle boundary change)。
        /// </summary>
        decimal GetNextDownPrice(decimal price);

        /// <summary>
        /// 往上或往下移動 N 檔。正數往上，負數往下。
        /// </summary>
        decimal MoveByTicks(decimal price, int ticks);
    }
}

