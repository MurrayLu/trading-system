using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Domain.Enums;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 一個簡單的服務，用於從 Channel 中讀取並打印聚合後的 Bar，以進行視覺化驗證。
    /// </summary>
    public class BarValidatorService : BackgroundService
    {
        private readonly ILogger<BarValidatorService> _logger;
        private readonly ChannelReader<DynamicBar> _barChannelReader;

        public BarValidatorService(
            ILogger<BarValidatorService> logger,
            ChannelReader<DynamicBar> barChannelReader)
        {
            _logger = logger;
            _barChannelReader = barChannelReader;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[Validator] K 線驗證器已啟動，等待接收聚合後的 Bar...");
            await foreach (var bar in _barChannelReader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogWarning("--- [VALIDATED BAR] | Stock: {StockId} | Type: {BarType}_{Period} | Time: {StartTime:HH:mm:ss} - {EndTime:HH:mm:ss} | O: {Open}, H: {High}, L: {Low}, C: {Close} | V: {Volume}, Ticks: {TickCount} ---",
                    bar.StockId,
                    bar.Config?.Type, bar.Config?.Period,
                    bar.StartTime,
                    bar.EndTime,
                    bar.Open, bar.High, bar.Low, bar.Close,
                    bar.Volume, bar.TickCount);
            }
            _logger.LogInformation("[Validator] K 線驗證器已接收完所有 Bar。");
        }
    }
}
