// MuNTrade.Abstractions/Domain/Models/Channels/BroadcastTickHub.cs (★ 已修正為 Bounded 模式)

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
        private readonly BoundedChannelOptions _channelOptions; // ★ 1. 新增 Bounded 選項設定

        /// <summary>
        /// ★ 2. 修改建構函式，接收容量參數。
        /// </summary>
        /// <param name="capacity">通道的最大容量。</param>
        public BroadcastTickHub(int capacity)
        {
            // 確保容量至少為 1
            if (capacity <= 0) capacity = 1;

            _channelOptions = new BoundedChannelOptions(capacity)
            {
                // ★ 關鍵策略：當通道已滿時，丟棄最舊的項目以騰出空間給新項目。
                FullMode = BoundedChannelFullMode.DropOldest
            };
        }

        public ChannelReader<TickData> RegisterReader()
        {
            // ★ 3. 從建立「無界」通道，改為建立「有界」通道。
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
