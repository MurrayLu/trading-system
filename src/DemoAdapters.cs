using MuNTrade.Abstractions.Contracts.Time;
namespace MuNTrade.Core.Trading.Adapters {
// New offline adapter: only the quote shape consumed by the original paper gateway.
public sealed class MarketSnapshotBuilder {
 public DemoQuote? GetLatestSnapshot(string symbol)=>null;
}
public sealed record DemoQuote(DemoBest5? Best5);
public sealed record DemoBest5(List<MuNTrade.Abstractions.Dtos.Trading.PriceLevel> Bids,List<MuNTrade.Abstractions.Dtos.Trading.PriceLevel> Asks);
}
namespace MuNTrade.Demo {
public sealed class DemoClock : IClock {
 public DateTime Now {get;private set;}=new(2026,10,20,9,15,0,DateTimeKind.Unspecified);
 public void AdvanceTo(DateTime value){if(value<Now)throw new InvalidOperationException("NON_MONOTONIC_CLOCK");Now=value;}
}
}
