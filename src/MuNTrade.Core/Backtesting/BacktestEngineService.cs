using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using System.Globalization;
using System.Threading.Channels;

namespace MuNTrade.Core.Backtesting;

/// <summary>
/// 專責：依 <see cref="BacktestSettings"/> 驅動 <see cref="ITickSource"/>，  
/// 以「非同步串流」方式把 Tick 或 KLine 推到 Channel，供之後的 Data-Pipeline 使用。
/// </summary>
[Obsolete("Legacy. Replaced by BacktestHostService.")]
public sealed class BacktestEngineService : BackgroundService
{
    private readonly ILogger<BacktestEngineService> _log;
    private readonly ITickSource _tickSource;
    private readonly BacktestSettings _opt;
    private readonly ChannelWriter<TickData> _tickWriter;
    private readonly ChannelWriter<DynamicBar> _barWriter;

    public BacktestEngineService(
        ILogger<BacktestEngineService> log,
        ITickSource tickSource,
        IOptions<BacktestSettings> optAcc,
        ChannelWriter<TickData> tickWriter,   // ← 來自 RawTickChannel
        ChannelWriter<DynamicBar> barWriter)    // ← 來自 DynamicBar Channel
    {
        _log = log;
        _tickSource = tickSource;
        _opt = optAcc.Value;
        _tickWriter = tickWriter;
        _barWriter = barWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _log.LogInformation("[BacktestEngineService] 啟動，模式={Mode}…", _opt.DataSourceType);

        // === 解析設定檔日期（固定用 ISO-8601：yyyy-MM-dd）========================
        if (!DateOnly.TryParseExact(_opt.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var startDate) ||
            !DateOnly.TryParseExact(_opt.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var endDate))
        {
            _log.LogCritical("StartDate / EndDate 格式錯誤！請使用 yyyy-MM-dd。");
            return;
        }
        // =========================================================================

        try
        {
            foreach (var sid in _opt.StockIds)
            {
                if (stop.IsCancellationRequested) break;

                if (_opt.DataSourceType.Equals("KLine", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(_opt.TimeFrame, out int tfMin))
                    {
                        _log.LogError("TimeFrame='{TF}' 不是有效數字。", _opt.TimeFrame);
                        continue;
                    }

                    _log.LogInformation("▶ 讀取 {Sid} - {Tf}m KLine…", sid, tfMin);
                    await foreach (var bar in _tickSource.StreamKLinesAsync(
                                         sid,
                                         tfMin,
                                         startDate.ToDateTime(TimeOnly.MinValue), //  <-- 修改處
                                         stop))
                    {
                        if (stop.IsCancellationRequested) break;
                        await _barWriter.WriteAsync(bar, stop);
                    }
                }
                else // Tick mode
                {
                    _log.LogInformation("▶ 讀取 {Sid} Tick ({S}~{E})…",
                                         sid, startDate, endDate);
                    await foreach (var tick in _tickSource.StreamTicksAsync(
                                         sid,
                                         startDate.ToDateTime(TimeOnly.MinValue), // <-- 修改處
                                         endDate.ToDateTime(TimeOnly.MinValue),   // <-- 修改處
                                         stop))
                    {
                        if (stop.IsCancellationRequested) break;
                        await _tickWriter.WriteAsync(tick, stop);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "BacktestEngineService 執行時遭遇未處理例外。");
        }
        finally
        {
            _tickWriter.TryComplete();
            _barWriter.TryComplete();
            _log.LogInformation("✔ 數據注入完畢，BacktestEngineService 結束。");
        }
    }
}
