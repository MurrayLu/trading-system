// MuNTrade.Abstractions/Domain/Models/OptimizationJob.cs

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
        /// 【v7.5 修正】新增此屬性，用於覆寫 StrategyParameters 的參數。
        /// </summary>
        public Dictionary<string, object> StrategyParamOverrides { get; set; } = new();
    }
}
