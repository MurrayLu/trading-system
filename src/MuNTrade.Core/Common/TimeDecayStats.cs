using System;

namespace MuNTrade.Core.Common
{
    /// <summary>
    /// 時間衰減統計器 (Time-Decay Stats)
    /// 用途：處理不規則時間間隔的 EMA 計算，解決 Tick 密度不均導致的指標失真。
    /// </summary>
    public class TimeDecayStats
    {
        private readonly double _decayLambda;
        private double _value;
        private bool _isInitialized;

        /// <summary>
        /// @param halfLifeSeconds 半衰期(秒)：權重衰減至 50% 所需時間
        /// </summary>
        public TimeDecayStats(double halfLifeSeconds)
        {
            if (halfLifeSeconds <= 0) throw new ArgumentException("Half-life must be > 0");
            // Lambda = ln(2) / half_life
            _decayLambda = 0.69314718056 / halfLifeSeconds;
        }

        public void Update(double newValue, double deltaSeconds)
        {
            if (deltaSeconds < 0) deltaSeconds = 0; // 防禦時間倒流

            if (!_isInitialized)
            {
                _value = newValue;
                _isInitialized = true;
                return;
            }

            // Alpha = 1 - e^(-lambda * dt)
            // 數學意義：根據時間經過的長短，決定要「忘記」多少舊資料
            double alpha = 1.0 - Math.Exp(-_decayLambda * deltaSeconds);

            // EMA 更新公式
            _value += alpha * (newValue - _value);
        }

        public double Value => _isInitialized ? _value : 0;

        public void Reset()
        {
            _value = 0;
            _isInitialized = false;
        }
    }
}

