namespace MuNTrade.Core.Settings
{
    public sealed class IcebergTrackerOptions
    {
        // Windows / touch / liveness
        public double touch_window_sec { get; set; } = 5.0;
        public double rebound_window_sec { get; set; } = 3.0;
        public int rebound_min_ticks { get; set; } = 2;
        public double defend_debounce_sec { get; set; } = 5.0;   // Raised for safety
        public double breach_debounce_sec { get; set; } = 5.0;   // Raised for safety
        public double event_throttle_sec { get; set; } = 3.0;    // Global per-event throttle window
        public double retreat_explain_window_sec { get; set; } = 2.0;
        public double migrate_window_sec { get; set; } = 3.0;
        public bool migrated_enabled { get; set; } = false;      // Default disabled for Phase 0
        public double persist_tiers_sec_L1 { get; set; } = 60.0;
        public double persist_tiers_sec_L2 { get; set; } = 300.0;
        public double persist_tiers_sec_L3 { get; set; } = 900.0;
        public double escalate_min_gap_sec { get; set; } = 10.0;
        public double decay_ttl_sec { get; set; } = 15.0;
        public double breach_cleanup_delay_sec { get; set; } = 3.0;
        public double migrate_hold_sec { get; set; } = 3.0;

        // Microstructure thresholds
        public decimal tick_size { get; set; } = 1.0m;
        public int min_touch_aggr_vol { get; set; } = 100;
        public int breach_min_aggr_vol { get; set; } = 500;
        public int retreat_min_abs_delta { get; set; } = 200;
        public int migrate_min_abs_delta { get; set; } = 200;
        public int replenish_confirm_min { get; set; } = 50;
        public int visible_topN_limit { get; set; } = 50;

        // Resurrection options (kept OFF by default)
        public bool resurrect_enabled { get; set; } = false;
        public double resurrect_window_sec { get; set; } = 0.5;
        public int resurrect_min_abs { get; set; } = 20;
        public double resurrect_max_carry_ratio { get; set; } = 0.30;
        public double price_cooldown_after_terminal_sec { get; set; } = 0.8;
    }
}

