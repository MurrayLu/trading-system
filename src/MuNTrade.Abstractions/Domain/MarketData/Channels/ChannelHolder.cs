
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System.Threading.Channels;

namespace MuNTrade.Abstractions.Domain.MarketData.Channels
{
    /// <summary>
    /// </summary>
    public class RawTickChannel
    {
        private readonly Channel<TickData> _ch = Channel.CreateBounded<TickData>(
            new BoundedChannelOptions(262_144)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        public ChannelReader<TickData> Reader => _ch.Reader;
        public ChannelWriter<TickData> Writer => _ch.Writer;
    }
    public class FilteredTickChannel
    {
        private readonly Channel<TickData> _ch = Channel.CreateBounded<TickData>(
            new BoundedChannelOptions(262_144)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        public ChannelReader<TickData> Reader => _ch.Reader;
        public ChannelWriter<TickData> Writer => _ch.Writer;
    }
    // 其他 Channel Holder 維持不變
    public class BarChannel
    {
        private readonly Channel<DynamicBar> _channel = Channel.CreateUnbounded<DynamicBar>();
        public ChannelReader<DynamicBar> Reader => _channel.Reader;
        public ChannelWriter<DynamicBar> Writer => _channel.Writer;
    }
}

