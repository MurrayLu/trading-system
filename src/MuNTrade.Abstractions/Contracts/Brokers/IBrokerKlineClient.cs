using System;

namespace MuNTrade.Abstractions.Contracts.Brokers
{
    /// <summary>
    /// 券商 K 線下載的極簡抽象：事件 + 請求。
    /// 由 Broker 專案實作（例如 CapitalKlineClient），Infrastructure 只依賴此介面。
    /// </summary>
    public interface IBrokerKlineClient
    {
        /// <summary>券商回推一行 K 線 CSV 字串時觸發。</summary>
        event Action<string /*symbol*/, string /*line*/>? KLineDataReceived;

        /// <summary>
        /// 依日期區間請求 K 線。
        /// klineType: 分=0, 日=4, 週=5, 月=6；minuteNumber 僅在分K有效（1/5/60）。
        /// tradeSession/outType 視券商 SDK 規格（預設 0/1）。
        /// </summary>
        int RequestKLineByDate(string symbol, short klineType,
                               DateOnly start, DateOnly end,
                               short minuteNumber = 1,
                               short tradeSession = 0,
                               short outType = 1);
    }
}

