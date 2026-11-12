
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Domain.Optimization
{
    public class OptimizationJob
    {
        public string? JobId { get; set; }
        public string? Description { get; set; }
        public List<DataSegment> TrainingSegments { get; set; } = new();
        public List<DataSegment> ValidationSegments { get; set; } = new();

        /// <summary>
        /// Option overrides for an optimization run.
        /// </summary>
        public Dictionary<string, object> OptionOverrides { get; set; } = new();

        /// <summary>
        /// Strategy parameter overrides applied to this job.
        /// </summary>
        public Dictionary<string, object> StrategyParamOverrides { get; set; } = new();
    }
}
