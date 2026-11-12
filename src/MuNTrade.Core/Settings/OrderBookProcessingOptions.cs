using System;

namespace MuNTrade.Core.Settings
{
    /// <summary>
    /// Options controlling trade/quote joining and microstructure analytics.
    /// </summary>
    public sealed class OrderBookProcessingOptions
    {
        public JoinWindowOptions Join { get; set; } = new();
        public AnalyticsWindowOptions Analytics { get; set; } = new();
        public OrderBookEngineOptions Engine { get; set; } = new();
        public int logging_interval_sec { get; set; } = 60;
    }

    public sealed class JoinWindowOptions
    {
        /// <summary>Primary matching window (core) in milliseconds.</summary>
        public double core_window_ms { get; set; } = 60.0;

        /// <summary>Secondary matching window (tail) in milliseconds.</summary>
        public double tail_window_ms { get; set; } = 150.0;

        /// <summary>Delayed matching buffer allowing L2 to catch up.</summary>
        public double lookahead_buffer_ms { get; set; } = 25.0;

        /// <summary>Adaptive tuning switches.</summary>
        public bool adaptive_enabled { get; set; } = true;
        public double core_offset_ms { get; set; } = 10.0;
        public double tail_offset_ms { get; set; } = 10.0;
        public double core_min_ms { get; set; } = 40.0;
        public double core_max_ms { get; set; } = 90.0;
        public double tail_min_ms { get; set; } = 90.0;
        public double tail_max_ms { get; set; } = 160.0;
        public double lookahead_min_ms { get; set; } = 10.0;
        public double lookahead_max_ms { get; set; } = 30.0;
        public double orphan_ratio_threshold_pct { get; set; } = 5.0;
        public double orphan_adjust_ms { get; set; } = 10.0;

        /// <summary>How long to keep Best5 deltas in memory (seconds).</summary>
        public double history_seconds { get; set; } = 3.0;

        /// <summary>Maximum age of the carry-forward Best5 snapshot.</summary>
        public double snapshot_max_age_ms { get; set; } = 300.0;
    }

    public sealed class AnalyticsWindowOptions
    {
        /// <summary>Max number of joined trades kept per symbol.</summary>
        public int window_count { get; set; } = 48;

        /// <summary>Minimum trade count required to compute analytics.</summary>
        public int min_count { get; set; } = 12;

        /// <summary>Fallback minimum window duration in seconds.</summary>
        public double min_seconds { get; set; } = 1.5;
    }

    public class OrderBookEngineOptions
    {
        public double learning_duration_minutes { get; set; } = 15.0;

        //Large Trade ---
        public double large_trade_z_score { get; set; } = 3.0;
        public double large_trade_dynamic_mul { get; set; } = 5.0;
        public double large_trade_min_floor { get; set; } = 10.0;
        public double stat_min_opening_vol { get; set; } = 50.0;
        public double stat_fallback_threshold { get; set; } = 50.0;
        public double stat_fallback_mean { get; set; } = 5.0;

        //Wall / Dynamic ---
        public int institutional_size { get; set; } = 500;
        public double wall_size_mul { get; set; } = 10.0;
        public int min_wall_size { get; set; } = 50;

        //Change ---
        public double change_relative_mul { get; set; } = 5.0;
        public int change_min_size { get; set; } = 50;
        public int alert_change_size { get; set; } = 50;

        //Logic ---
        public int iceberg_min_trade { get; set; } = 5;

        //Sub-Detectors ---
        public IcebergDetectorOptions Iceberg { get; set; } = new();
        public LiquidityFlowDetectorOptions Liquidity { get; set; } = new();
        public IcebergTrackerOptions IcebergTracker { get; set; } = new();
    }

    public sealed class IcebergDetectorOptions
    {
        public int rolling_window { get; set; } = 400;
        public int min_samples_suspect { get; set; } = 12;
        public int min_samples_confirm { get; set; } = 40;
        public double matched_mad_mul { get; set; } = 3.0;
        public double replenish_mad_mul { get; set; } = 3.0;
        public double touch_mad_mul { get; set; } = 2.5;
        public double ratio_mad_mul { get; set; } = 2.0;
        public double matched_floor { get; set; } = 5.0;
        public double replenish_floor { get; set; } = 5.0;

        // [Tuning] Raised from 2.0 to 3.0 to require more persistent iceberg behavior
        public double touch_floor { get; set; } = 3.0;

        public double ratio_floor { get; set; } = 0.3;
        public double ratio_cap { get; set; } = 3.0;
        public double score_tau_fast_sec { get; set; } = 60.0;
        public double score_tau_mid_sec { get; set; } = 300.0;
        public double score_tau_slow_sec { get; set; } = 1200.0;
        public double decay_tau_sec { get; set; } = 90.0;
        public double suspect_min_gap_sec { get; set; } = 5.0;
        public double confirmed_min_gap_sec { get; set; } = 15.0;
        public double ttl_sec { get; set; } = 1200.0;
        public int max_levels_per_side { get; set; } = 400;
        public decimal tick_size { get; set; } = 1.0m;
    }

    public sealed class LiquidityFlowDetectorOptions
    {
        public int rolling_window { get; set; } = 400;
        public int min_samples { get; set; } = 30;
        public double add_mad_mul { get; set; } = 3.0;
        public double pull_mad_mul { get; set; } = 3.0;
        public double replenish_mad_mul { get; set; } = 2.5;
        public double big_change_mad_mul { get; set; } = 4.0;
        public double add_floor { get; set; } = 20.0;
        public double pull_floor { get; set; } = 20.0;
        public double replenish_floor { get; set; } = 10.0;
        public double big_change_floor { get; set; } = 50.0;
        public double decay_tau_sec { get; set; } = 90.0;
        public double event_min_gap_sec { get; set; } = 5.0;

        // [7-Rule Timing Guards]
        public double min_expose_ms { get; set; } = 150;
        public double shift_guard_ms { get; set; } = 150;
        public double rest_life_ms { get; set; } = 100;
        public double backtrack_window_ms { get; set; } = 150;
    }
}

