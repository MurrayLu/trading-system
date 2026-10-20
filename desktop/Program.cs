using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MuNTrade.Demo;
namespace MuNTrade.Demo.Desktop;
public static class Program {
 [STAThread]public static void Main(){var app=new Application();app.Run(new PaperWindow());}
}
// New, explicitly offline monitoring shell over the actual source paper gateway.
public sealed class PaperWindow : Window {
 readonly PaperSession session=new();
 readonly ListBox events=new(){Background=new SolidColorBrush(Color.FromRgb(12,22,35)),Foreground=Brushes.LightSteelBlue,BorderThickness=new Thickness(0),FontFamily=new FontFamily("Consolas"),FontSize=15};
 readonly TextBlock status=new(){FontSize=25,Foreground=Brushes.White,Margin=new Thickness(0,12,0,20)};
 public PaperWindow(){
  Title="Trading System — Isolated Demo (Synthetic / Paper)";Width=1240;Height=780;Background=new SolidColorBrush(Color.FromRgb(8,16,28));Foreground=Brushes.White;WindowStartupLocation=WindowStartupLocation.CenterScreen;
  var root=new DockPanel{Margin=new Thickness(32)};Content=root;
  var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
  header.Children.Add(new TextBlock{Text="Trading System | Isolated Demo",FontSize=38,FontWeight=FontWeights.Bold});
  header.Children.Add(new TextBlock{Text="SYNTHETIC DATA / PAPER TRADING    •    LIVE permanently disabled    •    No broker SDK",Foreground=Brushes.Turquoise,Margin=new Thickness(0,8,0,10),FontSize=16});
  header.Children.Add(new TextBlock{Text="Mock Broker → Tick → State → Risk Gate → Paper Order → Mock Fill",Foreground=Brushes.LightSteelBlue,FontSize=17});
  header.Children.Add(status);
  var buttons=new WrapPanel{Margin=new Thickness(0,0,0,18)};header.Children.Add(buttons);
  Add(buttons,"1. 合成行情",()=>{session.Tick(100,99.9m,100.1m);return Task.CompletedTask;});
  Add(buttons,"2. 模擬委託",async()=>{await session.Submit(session.Order(limit:99));});
  Add(buttons,"3. 觸價成交",()=>{session.Tick(98.9m,98.8m,99);return Task.CompletedTask;});
  Add(buttons,"驗證 LIVE 拒絕",async()=>{await session.Submit(session.Order(live:true));});
  Add(buttons,"行情中斷",()=>{session.Disconnect();return Task.CompletedTask;});
  var footer=new TextBlock{Text="Isolated Demo - newly built UI, not original desktop • Original PaperTradingGateway / EventBus / EntryWindowRule • New safety host and synthetic adapters",Foreground=Brushes.SlateGray,FontSize=13,Margin=new Thickness(0,15,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
  root.Children.Add(events);session.Events.Add("READY — no account, secret, real trade or network required");Refresh();Closed+=(_,_)=>session.Dispose();
 }
 void Add(Panel parent,string label,Func<Task> action){var b=new Button{Content=label,Padding=new Thickness(17,11,17,11),Margin=new Thickness(0,0,10,8),FontSize=16};b.Click+=async(_,_)=>{try{await action();}catch(Exception ex){session.Events.Add("REJECTED: "+ex.Message);}Refresh();};parent.Children.Add(b);}
 void Refresh(){status.Text=$"SYNTH-A    Last {session.LastPrice:0.00}    Paper Position {session.Position}    Fills {session.Fills.Count}    Feed {(session.Connected?"CONNECTED":"DISCONNECTED")}";events.ItemsSource=null;events.ItemsSource=session.Events;if(events.Items.Count>0)events.ScrollIntoView(events.Items[events.Items.Count-1]);}
}
