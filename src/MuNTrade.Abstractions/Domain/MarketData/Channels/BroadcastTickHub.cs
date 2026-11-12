
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Domain.MarketData.Channels
{
    public class BroadcastTickHub
    {
        private readonly ConcurrentBag<ChannelWriter<TickData>> _writers = new();
        private readonly BoundedChannelOptions _channelOptions; // Bounded channel options.

        /// <summary>
        /// 建構函式接收通道容量。
        /// </summary>
        /// <param name="capacity">通道的最大容量。</param>
        public BroadcastTickHub(int capacity)
        {
            // 確保容量至少為 1
            if (capacity <= 0) capacity = 1;

            _channelOptions = new BoundedChannelOptions(capacity)
            {
                // 丟棄最舊項目以便在通道已滿時接收新項目。
                FullMode = BoundedChannelFullMode.DropOldest
            };
        }

        public ChannelReader<TickData> RegisterReader()
        {
            // 每位訂閱者使用有界通道。
            var ch = Channel.CreateBounded<TickData>(_channelOptions);
            _writers.Add(ch.Writer);
            return ch.Reader;
        }

        public void Publish(TickData tick)
        {
            foreach (var w in _writers)
            {
                // Bounded 模式下，如果 Channel 已滿，TryWrite 會根據 FullMode (DropOldest) 策略執行。
                // 它會先丟棄最舊的一筆，再寫入新的一筆，因此寫入總會成功。
                w.TryWrite(tick);
            }
        }

        public void Complete()
        {
            foreach (var w in _writers)
            {
                w.TryComplete();
            }
        }
    }
}
