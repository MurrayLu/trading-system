using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.Broker
{
    /// <summary>
    /// 期貨權益回報（對應 SKOrderLib_OnFutureRights）。
    /// </summary>
    public sealed record FutureRightsEvent : IMuEvent
    {
        public ArrivalTs ArrivalTs { get; init; }
        public string RawCsv { get; init; } = "";
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

        public decimal AccountBalance { get; init; }         // 0
        public decimal FloatingPnL { get; init; }            // 1
        public decimal RealizedFee { get; init; }            // 2
        public decimal TradeTax { get; init; }               // 3
        public decimal ReservedPremium { get; init; }        // 4
        public decimal PremiumCashflow { get; init; }        // 5
        public decimal Equity { get; init; }                 // 6
        public decimal ExcessMargin { get; init; }           // 7
        public decimal CashTransfer { get; init; }           // 8
        public decimal BuyerMarketValue { get; init; }       // 9
        public decimal SellerMarketValue { get; init; }      // 10
        public decimal FutureClosePnL { get; init; }         // 11
        public decimal IntradayUnrealized { get; init; }     // 12
        public decimal InitialMargin { get; init; }          // 13
        public decimal MaintenanceMargin { get; init; }      // 14
        public decimal PositionInitialMargin { get; init; }  // 15
        public decimal PositionMaintenanceMargin { get; init; } //16
        public decimal OrderMargin { get; init; }            // 17
        public decimal BestExcessMargin { get; init; }       // 18
        public decimal TotalPremium { get; init; }           // 19
        public decimal ReservedFee { get; init; }            // 20
        public decimal OriginalMargin { get; init; }         // 21
        public decimal YesterdayBalance { get; init; }       // 22
        public string OptionComboFlag { get; init; } = "";   // 23
        public decimal MaintenanceRate { get; init; }        // 24
        public string Currency { get; init; } = "";          // 25
        public decimal SufficientInitialMargin { get; init; } // 26
        public decimal SufficientMaintenanceMargin { get; init; } //27
        public decimal SufficientAvailable { get; init; }    // 28
        public decimal CollateralAmount { get; init; }       // 29
        public decimal CollateralAvailable { get; init; }    // 30
        public decimal AvailableBalance { get; init; }       // 31
        public decimal SufficientCashAvailable { get; init; } // 32
        public decimal CollateralValue { get; init; }        // 33
        public decimal RiskIndicator { get; init; }          // 34
        public decimal OptionExpiryDiff { get; init; }       // 35
        public decimal OptionExpiryLoss { get; init; }       // 36
        public decimal FutureExpiryPnL { get; init; }        // 37
        public decimal AdditionalMargin { get; init; }       // 38
        public string LoginId { get; init; } = "";           // 39
        public string AccountNo { get; init; } = "";         // 40

        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
