using MuNTrade.Abstractions.Domain;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Text;

namespace MuNTrade.Core.Extensions
{
    public static class BarExtensions
    {
        public static string DisplayLabel(this DynamicBar bar)
        {
            if (bar.Config == null) return "N/A";

            if (bar.Config.Type == "Time")
            {
                return $"{bar.StartTime:HH:mm:ss} - {bar.EndTime:HH:mm:ss} [{bar.Config.Period}min]";
            }
            else // Tick
            {
                return $"{bar.EndTime:HH:mm:ss} [{bar.Config.Period}T]";
            }
        }

        public static string DisplayDetail(this DynamicBar bar, bool withLabel = true)
        {
            var sb = new StringBuilder();

            if (withLabel) sb.Append(bar.DisplayLabel()).Append(" | ");

            sb.AppendFormat("O:{0}  H:{1}  L:{2}  C:{3}  V:{4:n0}",
                            bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            if (bar.Config?.Type == "Tick")
                sb.AppendFormat("  Ticks:{0}", bar.TickCount);

            return sb.ToString();
        }
    }
}
