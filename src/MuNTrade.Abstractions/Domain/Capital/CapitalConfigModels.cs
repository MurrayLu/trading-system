using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MuNTrade.Abstractions.Domain.Capital
{
    // [Refactor] Base class for manual INotifyPropertyChanged implementation
    public class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    // [Refactor] 移除 MarketType，改為單一帳戶包含證券與期貨雙帳戶
    public class SimAccountModel : ObservableModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _name = "新模擬帳戶";
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private bool _isActive = false;
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        // 證券帳戶
        private decimal _stockInitial = 100000;
        public decimal StockInitial
        {
            get => _stockInitial;
            set => SetProperty(ref _stockInitial, value);
        }

        private decimal _stockBalance = 100000;
        public decimal StockBalance
        {
            get => _stockBalance;
            set => SetProperty(ref _stockBalance, value);
        }

        // 期貨帳戶
        private decimal _futuresInitial = 100000;
        public decimal FuturesInitial
        {
            get => _futuresInitial;
            set => SetProperty(ref _futuresInitial, value);
        }

        private decimal _futuresBalance = 100000;
        public decimal FuturesBalance
        {
            get => _futuresBalance;
            set => SetProperty(ref _futuresBalance, value);
        }

        // === Daily Loss Freeze (Stock) ===
        public decimal StockDailyLossLimitFrozen { get; set; }      // 當日最大虧損上限（凍結值）
        public decimal StockDailyLossBaseEquity { get; set; }       // 凍結基準 Equity（Balance+Unrealized）
        public DateOnly StockDailyLossTradeDate { get; set; }       // 對應的交易日（tradeDate）
        public decimal StockDailyLossUsedFrozen { get; set; }       // 當日已用虧損額度（只扣虧損成交）

        // === Daily Loss Freeze (Futures) ===
        public decimal FuturesDailyLossLimitFrozen { get; set; }
        public decimal FuturesDailyLossBaseEquity { get; set; }
        public DateOnly FuturesDailyLossTradeDate { get; set; }
        public decimal FuturesDailyLossUsedFrozen { get; set; }

        private int _currentPhaseIndex = 0;
        public int CurrentPhaseIndex
        {
            get => _currentPhaseIndex;
            set => SetProperty(ref _currentPhaseIndex, value);
        }

        public bool IsFirstSetup { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }

        // Persistent Daily PnL Tracking
        // This solves the problem where PnL resets on app restart or fails to update due to race conditions.
        private decimal _dayRealizedPnL;
        public decimal DayRealizedPnL
        {
            get => _dayRealizedPnL;
            set => SetProperty(ref _dayRealizedPnL, value);
        }
        public DateTime DayPnLDate { get; set; }

        // Market-specific realized PnL (avoid cross-contamination between stock/futures)
        public decimal StockDayRealizedPnL { get; set; }
        public decimal FuturesDayRealizedPnL { get; set; }
        public DateOnly StockDayPnLDate { get; set; }
        public DateOnly FuturesDayPnLDate { get; set; }

        [JsonIgnore]
        public string ShortId => Id.Length > 8 ? Id.Substring(0, 8) : Id;
    }

    // TradeRulesConfig
    public class TradeRulesConfig : ObservableModel
    {
        public StockRuleConfig Stock { get; set; } = new();
        public Dictionary<string, FutureContractSpec> Futures { get; set; } = new()
        {
            { "TX00", new FutureContractSpec { Name = "大台指", InitialMargin = 167000, MaintMargin = 128000, ClearingMargin = 122000, FeePerOrder = 50, PointValue = 200 } },
            { "MTX00", new FutureContractSpec { Name = "小台指", InitialMargin = 41750, MaintMargin = 32000, ClearingMargin = 30500, FeePerOrder = 25, PointValue = 50 } },
            { "TM00", new FutureContractSpec { Name = "微台指", InitialMargin = 10500, MaintMargin = 8000, ClearingMargin = 7500, FeePerOrder = 15, PointValue = 10 } }
        };
    }

    public class StockRuleConfig : ObservableModel
    {
        private decimal _dailyTradeCap = 4990000;
        public decimal DailyTradeCap
        {
            get => _dailyTradeCap;
            set => SetProperty(ref _dailyTradeCap, value);
        }

        private decimal _feeDiscount = 0.6m;
        public decimal FeeDiscount
        {
            get => _feeDiscount;
            set => SetProperty(ref _feeDiscount, value);
        }

        private decimal _taxRate = 0.003m;
        public decimal TaxRate
        {
            get => _taxRate;
            set => SetProperty(ref _taxRate, value);
        }

        private decimal _dayTradeTaxRate = 0.0015m;
        public decimal DayTradeTaxRate
        {
            get => _dayTradeTaxRate;
            set => SetProperty(ref _dayTradeTaxRate, value);
        }
    }

    public class FutureContractSpec : ObservableModel
    {
        public string Name { get; set; } = "";

        private decimal _initialMargin;
        public decimal InitialMargin
        {
            get => _initialMargin;
            set => SetProperty(ref _initialMargin, value);
        }

        private decimal _maintMargin;
        public decimal MaintMargin
        {
            get => _maintMargin;
            set => SetProperty(ref _maintMargin, value);
        }

        private decimal _clearingMargin;
        public decimal ClearingMargin
        {
            get => _clearingMargin;
            set => SetProperty(ref _clearingMargin, value);
        }

        private decimal _feePerOrder;
        public decimal FeePerOrder
        {
            get => _feePerOrder;
            set => SetProperty(ref _feePerOrder, value);
        }

        private decimal _pointValue;
        public decimal PointValue
        {
            get => _pointValue;
            set => SetProperty(ref _pointValue, value);
        }

        private decimal _taxRate = 0.00002m;
        public decimal TaxRate
        {
            get => _taxRate;
            set => SetProperty(ref _taxRate, value);
        }
    }
}

