using MuNTrade.Abstractions.Domain.Trading;

namespace MuNTrade.Abstractions.Events.Capital
{
    public sealed class RealizedLegCreatedEvent
    {
        public RealizedLeg Leg { get; set; } = new();
    }
}

