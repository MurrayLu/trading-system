using MuNTrade.Abstractions.Domain.Trading;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    /// <summary>
    /// 定義通用的下單閘道介面，區分不同商品類型。
    /// </summary>
    public interface IOrderGateway
    {
        /// <summary>
        /// 送出期貨訂單。
        /// </summary>
        Task<string> PlaceFutureOrderAsync(OrderRequest orderRequest);

        /// <summary>
        /// 送出證券訂單。
        /// </summary>
        Task<string> PlaceStockOrderAsync(OrderRequest orderRequest);

        /// <summary>
        /// 查詢購買力 (期貨=權益數, 股票=可用額度)
        /// </summary>
        Task<decimal> GetBuyingPowerAsync(string symbol);

        // --- 掛單管理 (Order Management) ---

        /// <summary>
        /// 依序號刪單 (Cancel Order By SeqNo)
        /// </summary>
        Task<string> CancelOrderBySeqNoAsync(string seqNo);

        /// <summary>
        /// 依書號刪單 (Cancel Order By BookNo)
        /// </summary>
        Task<string> CancelOrderByBookNoAsync(string bookNo);

        /// <summary>
        /// 依商品代號刪單 (Cancel Order By Symbol, 視為全撤)
        /// </summary>
        Task<string> CancelOrderByStockNoAsync(string symbol);

        /// <summary>
        /// 依條件刪單 (Cancel Order By Symbol + Side + Price)
        /// </summary>
        Task<string> CancelOrderByStockNoAdvanceAsync(string symbol, MuNTrade.Abstractions.Domain.Trading.Enums.OrderSide side, decimal? price);

        /// <summary>
        /// 依序號改價 (Correct Price By SeqNo). 注意：僅限限價單。
        /// </summary>
        Task<string> CorrectPriceBySeqNoAsync(string seqNo, decimal newPrice);

        /// <summary>
        /// 依書號改價 (Correct Price By BookNo). 注意：僅限限價單。
        /// </summary>
        Task<string> CorrectPriceByBookNoAsync(string bookNo, decimal newPrice);

        /// <summary>
        /// 依序號改量 (Decrease Quantity By SeqNo).
        /// </summary>
        Task<string> DecreaseOrderBySeqNoAsync(string seqNo, int reduceQty);

        // --- 海期管理 (Overseas Future) ---

        Task<string> OverSeaCancelOrderBySeqNoAsync(string seqNo);
        Task<string> OverSeaCancelOrderByBookNoAsync(string bookNo);
        Task<string> OverSeaCorrectPriceByBookNoAsync(string bookNo, decimal newPrice);
        Task<string> OverSeaDecreaseOrderBySeqNoAsync(string seqNo, int reduceQty);
    }
}

