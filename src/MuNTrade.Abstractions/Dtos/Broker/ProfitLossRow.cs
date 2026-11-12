using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Dtos.Broker
{
    public sealed record ProfitLossRow
    {
        public string RawCsv { get; init; } = "";
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
    }
}
