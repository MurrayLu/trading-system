using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    // ===== 健康度守門員（資料/時鐘）=====
    public interface IDataHealthGuard
    {
        DataHealth Check(MarketSnapshot snapshot, DateTime? nowOverride = null);
    }

    // ===== 訊號產生（Slice#1：5m 趨勢 → A級回檔 → 量能逃逸）=====
    public interface ISignalGenerator
    {
        IEnumerable<TradeCandidate> TryGenerate(MarketSnapshot snapshot);
    }

    // ===== 成交品質守門員 =====
    public interface IExecutionQualityGuard
    {
        ExecutionGuardSnapshot Evaluate(OrderSide side, decimal intendedPrice, MarketSnapshot snapshot, DateTime sessionOpenLocal);
    }

    // ===== 機會驗證/打分（整合 Guard + 風險/空間檢查）=====
    public interface IOpportunityValidator
    {
        ValidatedOpportunity Validate(
            TradeCandidate candidate,
            MarketSnapshot snapshot,
            Func<decimal> atrGetter,
            DateTime sessionOpenLocal
        );
    }

    // ===== 機會成本紀錄器 =====
    public interface IOpportunityCostLogger
    {
        Task LogAsync(MissedOpportunity item, CancellationToken ct = default);
        Task<IReadOnlyList<MissedOpportunity>> QueryAsync(string symbol, DateTime from, DateTime to, CancellationToken ct = default);
    }

    // ===== 下單執行器（可用你現有 IOrderExecutionService 做 Adapter 實作）=====
    public interface ITradeExecutionManager
    {
        Task<OrderResult> SubmitAsync(OrderIntent intent, CancellationToken ct = default);
        Task CancelAsync(string orderId, CancellationToken ct = default);
    }

    // ===== 風控：初始停損/追蹤/保本/時間停損 =====
    public interface IActiveRiskManager
    {
        void Activate(object position, RiskParams risk, TrailingSpec trailingSpec);

        /// <summary>
        /// 返回型別從 IEnumerable<object> 改為 IEnumerable<ITradingAction>
        /// </summary>
        IEnumerable<ITradingAction> OnMarket(MarketSnapshot snapshot, object position);
    }

    // ===== 出場：靜態目標/動態訊號/分批出場 =====
    public interface IProfitCaptureManager
    {
        void Arm(object position, IEnumerable<ProfitTarget> targets);

        /// <summary>
        /// 返回型別從 IEnumerable<object> 改為 IEnumerable<ITradingAction>
        /// </summary>
        IEnumerable<ITradingAction> OnMarket(MarketSnapshot snapshot, object position);
    }
}

