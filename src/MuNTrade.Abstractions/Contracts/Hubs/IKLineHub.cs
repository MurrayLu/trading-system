using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System.Threading.Channels;

namespace MuNTrade.Abstractions.Contracts.Hubs
{
    public interface IKLineHub
    {
        /// <summary>
        /// 發佈一根新的 K 線到 Hub（由 KLineBuilderService 呼叫）
        /// </summary>
        ChannelWriter<KLine> Writer { get; }

        /// <summary>
        /// 每個訂閱者呼叫此方法取得自己的專屬 Reader
        /// </summary>
        ChannelReader<KLine> CreateReader();
    }
}

