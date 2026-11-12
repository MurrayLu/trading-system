using System;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Capital
{
    /// <summary>
    /// 帳戶資金快照模型 (UI 重構用)。
    /// 統一證券與期貨的顯示欄位，但資料來源口徑不同。
    /// </summary>
    public class AccountCapitalSnapshot
    {
        /// <summary>
        /// 市場類型 (Stock/Futures)。
        /// </summary>
        public MarketKind MarketKind { get; set; }

        /// <summary>
        /// 內部帳號 ID (例如 BrokerID + Account)。
        /// </summary>
        public string AccountId { get; set; } = string.Empty;

        /// <summary>
        /// UI 顯示用帳號名稱 (例如 "王小明 • 9876")。
        /// </summary>
        public string DisplayAccount { get; set; } = string.Empty;

        /// <summary>
        /// 總資產/權益主數字。
        /// 證券：一戶通餘額 (不含市值)。
        /// 期貨：權益總值/淨值權益數。
        /// </summary>
        public decimal TotalAsset { get; set; }

        /// <summary>
        /// 可下單額度。
        /// 證券：當日可買 (GetBalance#4)。
        /// 期貨：可動用保證金。
        /// </summary>
        public decimal AvailableToTrade { get; set; }

        /// <summary>
        /// 可出金/可動用金額。
        /// 證券：一戶通可動用 (GetBalance#3)。
        /// 期貨：可出金。
        /// </summary>
        /// <summary>
        /// 可出金/可動用金額。
        /// 證券：一戶通可動用 (GetBalance#3)。
        /// 期貨：可出金。
        /// </summary>
        public decimal Withdrawable { get; set; }

        /// <summary>
        /// 總交易額度 (證券)。
        /// 用於顯示當日買賣總額度上限。
        /// </summary>
        public decimal TotalTradingQuota { get; set; }

        /// <summary>
        /// 今日總損益 (已實現+未實現)。
        /// </summary>
        public decimal TodayPnLTotal { get; set; }

        /// <summary>
        /// 今日已實現損益。
        /// </summary>
        public decimal TodayPnLRealized { get; set; }

        /// <summary>
        /// 今日未實現損益。
        /// </summary>
        public decimal TodayPnLUnrealized { get; set; }

        /// <summary>
        /// 今日預估手續費。
        /// </summary>
        public decimal TodayFeeEstimate { get; set; }

        /// <summary>
        /// 今日預估交易稅。
        /// </summary>
        public decimal TodayTaxEstimate { get; set; }

        /// <summary>
        /// 日風控：最大允許虧損金額 (正數)。
        /// </summary>
        public decimal DailyLossLimit { get; set; }

        /// <summary>
        /// 日風控：目前已虧損金額 (正數)。
        /// </summary>
        public decimal DailyLossUsed { get; set; }

        /// <summary>
        /// 日風控：亏损使用率 (0.0 ~ 1.0+)。
        /// </summary>
        public double DailyLossUsedPct { get; set; }
    }
}

