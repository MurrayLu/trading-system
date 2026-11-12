using System.Threading.Channels;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;

namespace MuNTrade.Core.Hubs
{
    public class KLineHub : IKLineHub
    {
        private readonly Channel<KLine> _channel;

        public KLineHub()
        {
            // 有界通道+DropOldest，確保 UI/策略消費不會拖垮生產
            var opts = new BoundedChannelOptions(50_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<KLine>(opts);
        }

        public ChannelWriter<KLine> Writer => _channel.Writer;

        public ChannelReader<KLine> CreateReader() => _channel.Reader;
    }
}

