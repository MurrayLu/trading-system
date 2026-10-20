using MuNTrade.Demo;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
async Task Reject(Func<Task> action,string expected){try{await action();}catch(InvalidOperationException ex)when(ex.Message.Contains(expected)){return;}throw new Exception("Expected rejection: "+expected);}
using(var s=new PaperSession()){
 s.Tick(100,99.9m,100.1m);await s.Submit(s.Order(limit:99));Check(s.Fills.Count==0,"limit must wait");
 s.Tick(98.9m,98.8m,99m);Check(s.Fills.Count==1 && s.Position==1,"tick must fill queued paper order");
 Check(s.Events.Any(x=>x.StartsWith("N,")) && s.Events.Any(x=>x.StartsWith("D,")),"order state must report New then Filled");
 var fill=s.Fills.Single();s.ApplyFill(fill);Check(s.Position==1 && s.Fills.Count==1,"duplicate fill must be idempotent");
 s.Clock.AdvanceTo(s.Clock.Now.AddSeconds(10));
 s.ApplyFill(new OrderFillEvent{OrderId="synthetic-delayed",StockId=PaperSession.Symbol,Account="Paper",FillQuantity=1,FillPrice=99,Side=OrderSide.Buy,FillTime=fill.FillTime});
 Check(s.Position==2,"valid delayed report must not be silently dropped");
 s.ApplyFill(new OrderFillEvent{OrderId="synthetic-invalid",StockId=PaperSession.Symbol,Account="Paper",FillQuantity=-1,FillPrice=99});Check(s.Position==2,"invalid fill must not alter state");
 Console.WriteLine("PASS paper order lifecycle, delayed/duplicate/invalid reports");
}
using(var s=new PaperSession()){
 s.Tick(100,99.9m,100.1m);
 await Reject(()=>s.Submit(s.Order(live:true)),"LIVE_DISABLED");
 await Reject(async()=>{await s.Gateway.PlaceStockOrderAsync(s.Order(live:true));},"LIVE_DISABLED");
 Check(!typeof(PaperSession).Assembly.GetReferencedAssemblies().Any(x=>x.Name!.Contains("Broker") || x.Name.Contains("SKCOM")),"broker assembly must be absent");
 Check(s.Fills.Count==0,"LIVE rejection cannot produce fill");Console.WriteLine("PASS LIVE rejected at composition and gateway; no broker assembly");
 await Reject(()=>s.Submit(s.Order(quantity:4)),"SIZE_LIMIT");
 s.Clock.AdvanceTo(s.Clock.Now.AddSeconds(6));await Reject(()=>s.Submit(s.Order()),"STALE_QUOTE");
 s.Disconnect();await Reject(()=>s.Submit(s.Order()),"FEED_DISCONNECTED");Console.WriteLine("PASS size, stale quote and disconnected feed fail closed");
}
using(var s=new PaperSession()){
 s.Clock.AdvanceTo(new DateTime(2026,10,20,10,30,0));s.Tick(100,99.9m,100.1m);
 await Reject(()=>s.Submit(s.Order()),"不符開新倉時間窗");Check(s.Fills.Count==0,"window rejection cannot place order");Console.WriteLine("PASS original entry-window rule rejects opening outside window");
}
using(var s=new PaperSession()){
 s.Tick(100,99.9m,100.1m);var ids=new HashSet<string>();for(var i=0;i<12;i++)ids.Add(await s.Submit(s.Order()));Check(ids.Count==12,"paper order IDs must not collide in same second");
 Console.WriteLine("PASS same-second paper ID collision regression");
}
Console.WriteLine("MuNTrade acceptance complete; no network or real broker used.");
