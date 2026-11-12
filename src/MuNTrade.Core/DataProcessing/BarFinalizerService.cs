// BarFinalizerService.cs (最終微調版)
using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Time;
using System.Threading;
using System.Threading.Tasks;

public class BarFinalizerService : BackgroundService
{
    private readonly IKLineBuilder _kLineBuilder;
    private readonly IClock _clock;

    public BarFinalizerService(
        IKLineBuilder kLineBuilder,
        IClock clock)
    {
        _kLineBuilder = kLineBuilder;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                var now = _clock.Now;
                _kLineBuilder.FlushBarsExpiredBefore(new ExchangeTs(now));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception)
        {
            // Unexpected exceptions stop the service; this class has no logger.
        }
        finally
        {
            // 最後且只要一次的清理 (服務關閉時)
            var now = _clock.Now;
            _kLineBuilder.FlushBarsExpiredBefore(new ExchangeTs(now));
        }
    }
}
