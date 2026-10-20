using Microsoft.Extensions.Logging.Abstractions;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Core.Hubs;
using MuNTrade.Core.Trading.Gateways;
using MuNTrade.Core.Trading.Rules;
using MuNTrade.Core.Trading.Adapters;
namespace MuNTrade.Demo;
// New safety composition root. No broker SDK, credentials, sockets or LIVE DI binding.
public sealed class PaperSession : IDisposable {
 public const string Symbol="SYNTH-A";
 public DemoClock Clock {get;}=new();
 public InMemoryEventBus Bus {get;}=new();
 public PaperTradingGateway Gateway {get;}
 public EntryWindowRule Window {get;}=new(TimeSpan.FromHours(9),TimeSpan.FromHours(10));
 public List<string> Events {get;}=[];
 public List<OrderFillEvent> Fills {get;}=[];
 public decimal LastPrice {get;private set;}
 public long Position {get;private set;}
 public bool Connected {get;private set;}=true;
 public DateTime? LastTickAt {get;private set;}
 readonly HashSet<string> seenFills=new(StringComparer.Ordinal);
 readonly List<IDisposable> subscriptions=[];
 public PaperSession(){
  Gateway=new(Bus,NullLogger<PaperTradingGateway>.Instance,Clock,new MarketSnapshotBuilder());
  subscriptions.Add(Bus.Subscribe<BrokerReportEvent>(r=>Events.Add(r.RawReport)));
  subscriptions.Add(Bus.Subscribe<OrderFillEvent>(ApplyFill));
 }
 public void Tick(decimal price,decimal bid,decimal ask,int seconds=1){
  if(!Connected)throw new InvalidOperationException("FEED_DISCONNECTED");
  if(price<=0 || bid<=0 || ask<bid)throw new InvalidOperationException("INVALID_QUOTE");
  Clock.AdvanceTo(Clock.Now.AddSeconds(seconds));LastPrice=price;LastTickAt=Clock.Now;
  Events.Add($"TICK {Symbol} {price}");
  // The actual paper gateway consumes both quote snapshots and tick events.
  Bus.Publish(new QuoteSnapshotEvent{Symbol=Symbol,Timestamp=Clock.Now,BidPrice=bid,AskPrice=ask,IsSimulated=true});
  Bus.Publish(new TradeTickEvent{Symbol=Symbol,Price=price,BidAtTrade=bid,AskAtTrade=ask,IsSimulated=true});
 }
 public OrderRequest Order(long quantity=1,bool live=false,decimal limit=99)=>new(){
  OriginalSignal=new(){StockId=Symbol,Price=limit,SignalTime=Clock.Now,Reason="Synthetic teaching order"},
  Side=OrderSide.Buy,Quantity=quantity,IsLive=live,OrderType=OrderType.Limit,
  Trace=new(){CorrelationId=Guid.NewGuid().ToString("N"),IntentTimestamp=Clock.Now,AccountKey="Synthetic:Paper:Demo",StrategyId="SyntheticExample"}
 };
 public async Task<string> Submit(OrderRequest order){
  if(order.IsLive)throw new InvalidOperationException("LIVE_DISABLED");
  if(!Connected)throw new InvalidOperationException("FEED_DISCONNECTED");
  if(LastTickAt is null || Clock.Now-LastTickAt>TimeSpan.FromSeconds(5))throw new InvalidOperationException("STALE_QUOTE");
  if(order.Quantity<=0 || order.Quantity>3)throw new InvalidOperationException("SIZE_LIMIT");
  var risk=Window.Evaluate(order,new Position{StockId=Symbol,Quantity=Position});
  if(!risk.IsApproved)throw new InvalidOperationException(risk.Reason);
  Events.Add("RISK_APPROVED → PAPER_ORDER");
  return await Gateway.PlaceStockOrderAsync(order);
 }
 public void Disconnect(){Connected=false;Events.Add("FEED_DISCONNECTED");}
 public void ApplyFill(OrderFillEvent fill){
  if(fill.Account!="Paper" || fill.StockId!=Symbol || fill.FillQuantity<=0 || fill.FillPrice<=0 || string.IsNullOrWhiteSpace(fill.OrderId)){
   Events.Add("REJECTED_INVALID_FILL");return;
  }
  if(!seenFills.Add(fill.OrderId)){Events.Add("IGNORED_DUPLICATE_FILL");return;}
  // A delayed but valid paper report remains part of the immutable order history.
  Fills.Add(fill);Position+=fill.Side==OrderSide.Buy?fill.FillQuantity:-fill.FillQuantity;
  Events.Add($"MOCK_FILL {fill.OrderId} → POSITION {Position}");
 }
 public void Dispose(){Gateway.Dispose();foreach(var x in subscriptions)x.Dispose();}
}
