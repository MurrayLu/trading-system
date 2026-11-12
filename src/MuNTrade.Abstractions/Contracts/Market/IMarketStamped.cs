using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Contracts.Market
{
    /// <summary>
    /// 定義資料物件必須攜帶市場編號與來源單一真相。
    /// 任何可傳遞的市場資料 (Tick, Quote, KLine) 皆須實作此介面。
    /// </summary>
    public interface IMarketStamped
    {
        /// <summary>
        /// 市場編號 (Listing, OTC, Futures, Options, Foreign...)
        /// </summary>
        MarketNo MarketNo { get; set; }

        /// <summary>
        /// 資料來源 (Domestic, Overseas)
        /// </summary>
        MarketSource Source { get; set; }
    }
}

