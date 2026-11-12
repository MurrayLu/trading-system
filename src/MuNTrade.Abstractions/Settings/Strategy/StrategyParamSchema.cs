using System.Collections.Generic;

namespace MuNTrade.Abstractions.Settings.Strategy
{
    public enum StrategyParamFieldType
    {
        Integer,
        Decimal,
        Boolean,
        Enum,
        String
    }

    public sealed record StrategyParamField(string Name, StrategyParamFieldType FieldType, string? EnumType = null);

    public sealed record StrategyParamSchema(IReadOnlyList<StrategyParamField> Fields);
}

