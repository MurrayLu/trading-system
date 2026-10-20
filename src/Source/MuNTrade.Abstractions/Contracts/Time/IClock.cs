using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Time
{
    /// <summary>
    /// 提供時間的抽象介面，用於解耦對系統時間的直接依賴。
    /// </summary>
    public interface IClock
    {
        /// <summary>
        /// 取得目前的時間。
        /// </summary>
        DateTime Now { get; }

        /// <summary>
        /// (主要用於模擬) 將時間推進到指定的時刻。
        /// </summary>
        void AdvanceTo(DateTime newTimestamp);
    }
}

