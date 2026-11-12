using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Settings.Capital
{
    /// <summary>
    /// 定義基於不同價格級距的專屬參數覆蓋。
    /// </summary>
    public class PriceTierSetting
    {
        public decimal MaxPrice { get; set; } // 此規則適用的最高價格
        public int? Ticks { get; set; }        // (舊功能) 對應舊的 PriceMoveOverrides
        public double? Pct { get; set; }       // (舊功能) 對應舊的 PriceMoveOverrides
        public int? MinTicks { get; set; }     // Lower-bound override for the dynamic threshold.
    }
}

