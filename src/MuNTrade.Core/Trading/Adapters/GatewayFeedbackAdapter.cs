using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Time;
using System;
using System.Globalization;
using System.Linq;

namespace MuNTrade.Core.Trading.Adapters
{
    /// <summary>
    /// 統一回報轉接器 (GatewayFeedbackAdapter)
    /// 功能：將來自 API (Live) 或 模擬器 (Paper) 的原始字串，翻譯成 UI 看得懂的標準格式 (BrokerReportDto)。
    /// </summary>
    public class GatewayFeedbackAdapter
    {
        private readonly IEventBus _eventBus;
        private readonly ILogger<GatewayFeedbackAdapter> _logger;
        private readonly IClock _clock;
        private readonly MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider _marketInfoProvider;

        public GatewayFeedbackAdapter(
            IEventBus eventBus,
            ILogger<GatewayFeedbackAdapter> logger,
            IClock clock,
            MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider marketInfoProvider)
        {
            _eventBus = eventBus;
            _logger = logger;
            _clock = clock;
            _marketInfoProvider = marketInfoProvider;

            // [Fix] 註冊 CodePages 以支援 Big5 (CodePage 950)
            // 這是 .NET Core / .NET 5+ 必須的
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // 訂閱模擬交易回報
            _eventBus.Subscribe<BrokerReportEvent>(OnBrokerReportReceived);
            // 訂閱真實交易回報 (SKCOM)
            _eventBus.Subscribe<OrderReportEvent>(HandleOrderReportEvent);

            _logger.LogInformation("GatewayFeedbackAdapter 已啟動，準備處理 Live 與 Paper 回報。");
        }

        #region 真實交易處理 (Live Trading - SKCOM)

        /// <summary>
        /// 處理 SKCOM OnNewData 的原始回報
        /// </summary>
        public void HandleOrderReportEvent(OrderReportEvent evt)
        {
            // 基礎檢查：欄位數量不足則忽略 (避免 IndexOutOfRange)
            // 根據文件，完整欄位約有 48 個以上，取 40 當安全值
            if (evt.Fields.Count < 40) return;

            var f = evt.Fields; // 縮寫引用，方便對照 Index

            try
            {
                // 1. 商品與市場判斷
                string symbol = f[8].Trim();
                // [SOP-Strict] UI/Feedback 使用 TryGet，避免非關鍵路徑崩潰
                bool isDerivatives = _marketInfoProvider.TryGetMarketInfo(symbol, out var info) && info.IsDerivatives;
                string market = !isDerivatives ? "證" : "期";

                // 2. 數量處理 (Qty)
                // 注意：API 文件定義：
                // N(新單)=委託量, D(成交)=成交量, U(改量)=減量數, C(取消)=剩餘量(但我們要視為全銷)
                decimal.TryParse(f[20], out decimal rawQty);
                decimal displayQty = rawQty;

                // 證券且數量 >= 1000，轉為張數
                if (!isDerivatives && rawQty >= 1000)
                {
                    displayQty = rawQty / 1000m;
                }

                // 3. 價格與其他
                decimal.TryParse(f[11], out decimal price);
                string rawSide = f[6].Trim();
                string side = rawSide.StartsWith("B", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell";

                DateTime eventTime = evt.ArrivalTs.Value;
                string timeStr = f[24].Trim();
                if (!string.IsNullOrEmpty(timeStr))
                {
                    if (DateTime.TryParseExact(timeStr, new[] { "HHmmss", "HH:mm:ss", "HHmmssfff" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime tOnly))
                    {
                        eventTime = _clock.Now.Date.Add(tOnly.TimeOfDay);
                    }
                    else if (DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime tParsed))
                    {
                        eventTime = _clock.Now.Date.Add(tParsed.TimeOfDay);
                    }
                }

                // 4. 狀態與類別判定 (Action)
                string typeCode = f[2].Trim().ToUpperInvariant();
                string errCode = f[3].Trim().ToUpperInvariant();

                string status;
                string actionType;

                if (errCode == "Y")
                {
                    status = "失敗";
                    actionType = "錯誤";
                }
                else
                {
                    switch (typeCode)
                    {
                        case "N":
                            status = "新單";
                            actionType = "委託";
                            break;
                        case "D": // Deal / Filled
                        case "0E":
                            status = "完全成交";
                            actionType = "成交";
                            break;
                        case "P": // Partial Filled
                            status = "部分成交";
                            actionType = "成交";
                            break;
                        case "C": // Cancel
                        case "04":
                            status = "已取消";
                            actionType = "取消";
                            break;
                        case "U": // Reduce / Update Qty
                            status = "已改量";
                            actionType = "改單";
                            break;
                        case "R": // Reject
                            status = "被拒絕";
                            actionType = "錯誤";
                            break;
                        default:
                            status = typeCode;
                            actionType = "其他";
                            break;
                    }
                }

                // 5. 訊息與編號
                string rawMsg = f.Count > 44 ? f[44].Trim() : "";
                rawMsg = RepairBig5(rawMsg);
                string displayMsg = string.IsNullOrEmpty(rawMsg) ? "正常" : rawMsg;

                string seqNo = (f.Count > 47 && !string.IsNullOrEmpty(f[47])) ? f[47].Trim() : "";
                string bookNo = f.Count > 10 ? f[10].Trim() : "";
                if (!OrderKeyHelpers.IsValidBookNo(bookNo)) bookNo = "";
                string corrId = !string.IsNullOrEmpty(seqNo) ? seqNo : "";

                // 6. 發布 DTO
                var dto = new BrokerReportDto
                {
                    ExchangeTs = new ExchangeTs(eventTime),
                    ArrivalTs = evt.ArrivalTs,
                    Market = market,
                    Symbol = symbol,
                    Side = side,
                    Price = price,
                    Quantity = (int)displayQty, // 這裡傳遞的是「變動量」
                    RawQuantity = (int)rawQty,  // [New] 傳遞原始量，供改量使用
                    Status = status,
                    Action = actionType,
                    Message = displayMsg,
                    CorrelationId = corrId,
                    BookNo = bookNo,
                    SeqNo = seqNo,
                    ReportType = "Real",
                    Account = "Real", // [Fix] Live 預設為 Real
                    Level = "Info"
                };

                _eventBus.Publish(dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Live] 解析回報異常");
            }
        }

        #endregion

        #region 模擬交易處理 (Paper Trading)

        /// <summary>
        /// 處理 PaperTradingGateway 發出的簡單 CSV 回報
        /// 格式: D,Symbol,Side,FillPrice,Qty,SeqNo,TimeISO8601,CorrelationId
        /// </summary>
        private void OnBrokerReportReceived(BrokerReportEvent ev)
        {
            var raw = ev.RawReport ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5) return;

            try
            {
                // [0] Type: D=Filled, N=New...
                string type = parts[0];

                // [1] Symbol
                string symbol = parts[1];

                // [2] Side
                string side = (parts.Length > 2 && parts[2] == "S") ? "Sell" : "Buy";

                // [3] Price
                decimal.TryParse(parts.Length > 3 ? parts[3] : "0", out decimal price);

                // [4] Qty
                decimal.TryParse(parts.Length > 4 ? parts[4] : "0", out decimal rawQty);

                // [5] SeqNo (BookNo)
                string bookNo = parts.Length > 5 ? parts[5] : "";

                // [6] Time
                DateTime time = _clock.Now;
                if (parts.Length > 6)
                    DateTime.TryParse(parts[6], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

                // [7] CorrelationId
                string corrId = parts.Length > 7 ? parts[7] : "";

                // --- 統一轉換邏輯 (讓模擬單也顯示與 Live 一致的中文) ---

                // 1. 市場推測
                // [SOP-Strict]
                bool isDerivatives = _marketInfoProvider.TryGetMarketInfo(symbol, out var info) && info.IsDerivatives;
                string market = !isDerivatives ? "證" : "期";

                // 2. 數量換算
                decimal displayQty = rawQty;
                if (!isDerivatives && rawQty >= 1000)
                {
                    displayQty = rawQty / 1000m;
                }

                // 3. 狀態與類別中文化
                string status;
                string action;

                if (type == "D" || type == "Filled")
                {
                    status = "完全成交";
                    action = "成交";
                }
                else if (type == "C" || type == "Cancelled")
                {
                    status = "已取消";
                    action = "取消";
                }
                else if (type == "R" || type == "Rejected")
                {
                    status = "失敗";
                    action = "錯誤";
                }
                else if (type == "U" || type == "Update")
                {
                    status = "已改量";
                    action = "改單";
                }
                else
                {
                    status = "新單";
                    action = "委託";
                }

                // 4. 建立 DTO
                var dto = new BrokerReportDto
                {
                    ExchangeTs = new ExchangeTs(time),
                    ArrivalTs = new ArrivalTs(_clock.Now),
                    Market = market,
                    Symbol = symbol,
                    Side = side,
                    Price = price,
                    Quantity = (int)displayQty,
                    RawQuantity = (int)rawQty,
                    Status = status,
                    Action = action,
                    Message = "模擬交易正常", // Paper Trading 預設訊息
                    CorrelationId = corrId,
                    BookNo = bookNo, // [Fix] Include Parsed BookNo
                    SeqNo = "",
                    ReportType = "Paper",
                    Level = "Info"
                };

                _eventBus.Publish(dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Paper] 解析模擬回報失敗");
            }
        }

        #endregion

        // [Fix] 用來修復亂碼的工具方法 (使用 SKCOM 特有的還原邏輯)
        private string RepairBig5(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            try
            {
                // 核心邏輯：SKCOM 的 BSTR 傳輸時將每個 Byte 當作一個 Char (0x00 ~ 0xFF)
                // 因此不能依賴 Encoding.Default，必須強制取 Low Byte 還原回原始 Byte 陣列
                var raw = new byte[text.Length];
                for (int i = 0; i < text.Length; i++)
                {
                    raw[i] = (byte)(text[i] & 0xFF);
                }

                return System.Text.Encoding.GetEncoding(950).GetString(raw).Trim();
            }
            catch
            {
                return text;
            }
        }
    }
}

