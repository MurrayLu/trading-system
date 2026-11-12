using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Data;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 監聽券商連線狀態；只有在 Connected 後，才呼叫 Pipeline 啟用各商品。
    /// 這可避免 RC=1095（尚未連線就發請求）。
    /// </summary>
    public sealed class TradingSymbolActivator : IHostedService
    {
        private readonly ILogger<TradingSymbolActivator> _log;
        private readonly IBrokerConnectivity _conn;
        private readonly IMarketDataPipeline _pipeline;

        // 你可以改成從設定檔讀
        private readonly string[] _symbols = new[] { "TX00" };

        public TradingSymbolActivator(
            ILogger<TradingSymbolActivator> log,
            IBrokerConnectivity conn,
            IMarketDataPipeline pipeline)
        {
            _log = log;
            _conn = conn;
            _pipeline = pipeline;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("[Activator] 啟動，當前連線狀態：{State}", _conn.CurrentState);
            _conn.StateChanged += OnStateChanged;

            // 若啟動當下就已連線，直接啟用
            if (_conn.CurrentState == ConnectionState.Connected)
                ActivateSymbols();

            return Task.CompletedTask;
        }

        private void OnStateChanged(ConnectionState state, string message)
        {
            _log.LogInformation("[Activator] 券商狀態 = {State} / {Msg}", state, message);
            if (state == ConnectionState.Connected)
            {
                ActivateSymbols();
                _conn.StateChanged -= OnStateChanged; // 避免重複啟用
            }
        }

        private void ActivateSymbols()
        {
            foreach (var s in _symbols)
            {
                _log.LogInformation("[Activator] 啟用 {Symbol}（ticks + kline）", s);
                _pipeline.EnableSymbol(s, ticksEnabled: true, klineEnabled: true);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _conn.StateChanged -= OnStateChanged;
            return Task.CompletedTask;
        }
    }
}

