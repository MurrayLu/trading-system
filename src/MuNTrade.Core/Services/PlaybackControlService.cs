
using System;
using System.Threading.Tasks;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 一個輕量級的 Singleton 服務，作為 UI 和後端管線之間的橋樑，
    /// 用於從 UI 觸發後端的回測播放任務。
    /// </summary>
    public class PlaybackControlService
    {
        /// <summary>
        /// 當 UI 請求播放時觸發此事件。
        /// </summary>
        public event Func<Task>? PlaybackRequested;

        /// <summary>
        /// 由 UI (例如 MainViewModel 中的按鈕命令) 呼叫，以發出播放請求。
        /// </summary>
        public void RequestPlayback()
        {
            // 觸發事件，通知所有訂閱者
            PlaybackRequested?.Invoke();
        }
    }
}

