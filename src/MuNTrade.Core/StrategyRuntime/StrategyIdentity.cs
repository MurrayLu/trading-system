using System;

namespace MuNTrade.Core.StrategyRuntime
{
    public static class StrategyIdentity
    {
        /// <summary>
        /// Builds a consistent StrategyId string from TemplateId and InstanceId.
        /// Format: "{templateId}#{instanceId}"
        /// </summary>
        public static string BuildStrategyId(string templateId, string instanceId)
        {
            if (string.IsNullOrEmpty(templateId)) throw new ArgumentNullException(nameof(templateId));
            if (string.IsNullOrEmpty(instanceId)) throw new ArgumentNullException(nameof(instanceId));
            return $"{templateId}#{instanceId}";
        }
    }
}

