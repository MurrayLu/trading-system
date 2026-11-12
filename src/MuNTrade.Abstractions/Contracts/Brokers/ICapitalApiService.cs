using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using System;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Brokers
{
    public interface ICapitalApiService : MuNTrade.Abstractions.Contracts.Trading.IOrderGateway
    {
        // 這裡只保留 CapitalApi 專有的方法
        Task<bool> ConnectAndLoginAsync();

        string? StockAccount { get; }
        string? FuturesAccount { get; }
        int SubscribeTicks(string stockId);

        /// <summary>
        /// 查詢證券一戶通資金（同步呼叫：內部以 STA.Send 封送）。
        /// </summary>
        Task<OneAccountBalanceDto> GetOneAccountBalanceAsync();

        /// <summary>
        /// 查詢期貨權益（非同步：結果由 OnFutureRights 事件回傳）。
        /// </summary>
        void RequestFutureRights(string? correlationId = null);

        /// <summary>
        /// 查詢期貨未平倉（非同步：結果由 OnOpenInterest 事件回傳）。
        /// </summary>
        void RequestOpenInterest(string? correlationId = null);

        /// <summary>
        /// 查詢證券庫存（非同步：結果由 OnRealBalanceReport 事件回傳）。
        /// </summary>
        void RequestStockInventory(string? correlationId = null);

        /// <summary>
        /// 查詢證券損益（非同步：結果由 OnProfitLossGWReport 事件回傳）。
        /// </summary>
        void RequestPnL(Abstractions.Domain.Capital.ProfitLossQueryType type = Abstractions.Domain.Capital.ProfitLossQueryType.Unrealized, Abstractions.Domain.Capital.ProfitLossFormat format = Abstractions.Domain.Capital.ProfitLossFormat.Summary, DateTime? start = null, DateTime? end = null);

        /// <summary>
        /// 請求指定市場的所有商品清單。
        /// </summary>
        /// <param name="marketNo">市場代碼 (0:上市, 1:上櫃, 2:期貨, 3:選擇權)</param>
        /// <returns>API 回傳碼</returns>
        int RequestStockList(short marketNo);

        /// <summary>
        /// 商品清單回傳事件 (來自 SKQuote OnNotifyStockList)。
        /// </summary>
        event Action<short, string>? StockListDataReceived;

        /// <summary>
        /// 中斷行情監控連線（SKQuoteLib_LeaveMonitor）。
        /// </summary>
        void LeaveMonitor();

        /// <summary>
        /// 啟動行情監控連線（SKQuoteLib_EnterMonitorLONG）。
        /// </summary>
        void EnterMonitor();

        /// <summary>
        /// 暫停自動重連（例如 LeaveMonitor 後的保護窗）。
        /// </summary>
        void SuspendReconnect(TimeSpan duration, string? reason = null);

        /// <summary>
        /// 恢復自動重連。
        /// </summary>
        void ResumeReconnect(string? reason = null);

        /// <summary>
        /// 請求指定商品的歷史 K 線資料。
        /// </summary>
        /// <param name="symbol">商品代碼</param>
        /// <param name="klineType">K 線類型 (e.g., 4 for 日線)</param>
        /// <param name="startDate">開始日期 (YYYYMMDD)</param>
        /// <param name="endDate">結束日期 (YYYYMMDD)</param>
        /// <returns>API 回傳碼</returns>
        int RequestKLine(string symbol, short klineType, string startDate, string endDate);

        /// <summary>
        /// 請求指定股票的資券配額與交易狀態（含當沖、警示等旗標）。
        /// </summary>
        /// <param name="stockSymbol">股票代碼</param>
        /// <param name="marketNo">市場代碼 (必填)</param>
        /// <param name="source">市場來源 (預設 Domestic)</param>
        /// <returns>API 回傳碼</returns>
        int RequestMarginPurchaseAmountLimit(string stockSymbol, Abstractions.Domain.Common.Enums.MarketNo marketNo, Abstractions.Domain.Common.Enums.MarketSource source = Abstractions.Domain.Common.Enums.MarketSource.Domestic);



        ConnectionState CurrentState { get; }   // 當前連線狀態
        event Action<ConnectionState, string> OnStateChanged;   // 連線狀態變更事件

        Task SendLatencyTestFutureOrderAsync(string stockId);
        Task SendLatencyTestStockOrderAsync(string stockId);
    }
}

