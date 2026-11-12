using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    public sealed class FactorStrategyOptions
    {
        public string Mode { get; set; } = "CIS"; // CIS or Original

        public DashboardOptions Dashboard { get; set; } = new();
        public OrbOptions ORB { get; set; } = new();
        public VwapOptions VWAP { get; set; } = new();
        public DominantOptions Dominant { get; set; } = new();
        public EntryGateOptions Entry { get; set; } = new();
        public ExitOptions Exit { get; set; } = new();
        public ValidatorOptions Validator { get; set; } = new();
        public DataHealthOptions Health { get; set; } = new();
    }

    public sealed class DashboardOptions
    {
        public int window_sec { get; set; } = 2;
        public int lookback_min { get; set; } = 15;
        public int percentile { get; set; } = 95;
        public List<double> weights { get; set; } = new() { 0.4, 0.4, 0.2 };
        public double flameout_sec { get; set; } = 1.2;
        public int attack_window_sec { get; set; } = 60;
        public int catchup_suppress_sec { get; set; } = 600;          // suppress UI updates while ticks lag behind wall clock
        public int catchup_resume_slack_sec { get; set; } = 2;        // lag tolerance to resume UI after catch-up
        public int catchup_sample_sec { get; set; } = 2;              // allow a sample publish every N seconds even during catch-up
        public int min_publish_interval_ms { get; set; } = 200;       // throttle UI publish frequency per symbol
    }

    public sealed class OrbOptions
    {
        public string start { get; set; } = "09:00";
        public string end { get; set; } = "09:15";
        public string night_start { get; set; } = "15:00";
        public string night_end { get; set; } = "15:15";
    }

    public sealed class VwapOptions
    {
        public string type { get; set; } = "VWAP"; // future: AVWAP
    }

    public sealed class DominantOptions
    {
        public int ema { get; set; } = 13;
        public decimal touch_atr { get; set; } = 0.2m;
        public double pullback_vol_ratio { get; set; } = 0.7;
    }

    public sealed class EntryGateOptions
    {
        public double min_buy_ratio { get; set; } = 0.6;
        public double min_density_ratio { get; set; } = 0.5;
        public double max_spread_bp { get; set; } = 30.0;
        public double front_wall_mul { get; set; } = 20.0;
    }

    public sealed class DataHealthOptions
    {
        public double warmup_sec { get; set; } = 5.0;
        public double stale_yellow_sec { get; set; } = 15.0;
        public double stale_red_sec { get; set; } = 60.0;
        public double max_tick_without_l2_ms { get; set; } = 5000.0;
        public double max_l2_without_tick_ms { get; set; } = 5000.0;
        public double max_clock_skew_ms { get; set; } = 5000.0;
        public double log_throttle_sec { get; set; } = 2.0;
    }

    public sealed class ExitOptions
    {
        public int empty_spin_lookback_sec { get; set; } = 45;
        public bool force_exit_on_vwap_break { get; set; } = true;
        public decimal hard_stop_pct { get; set; } = 0.01m;
    }

    public sealed class ValidatorOptions
    {
        public decimal min_rrr { get; set; } = 2.0m;
    }
}




