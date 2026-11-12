using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Events.Capital
{
    public class PositionSnapshotEvent
    {
        public List<PositionDto> Positions { get; set; } = new();
    }
}

