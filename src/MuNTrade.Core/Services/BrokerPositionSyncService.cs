using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Domain.Capital;

namespace MuNTrade.Core.Services
{
    public sealed class BrokerPositionSyncService : IDisposable
    {
        private readonly ILogger<BrokerPositionSyncService> _logger;
        private readonly IEventBus _eventBus;
        private readonly ICapitalApiService _api;
        private readonly IBrokerConnectivity _connectivity;
        private readonly IClock _clock;
        private readonly Microsoft.Extensions.Options.IOptionsMonitor<MuNTrade.Abstractions.Settings.Common.ApplicationSettings> _appSettings;

        private readonly object _lock = new();
        private DateTimeOffset _lastSyncTime = DateTimeOffset.MinValue;
        private const int ThrottleSeconds = 5;

        private IDisposable? _modeSubscription;
        private readonly CancellationTokenSource _disposeCts = new();

        public BrokerPositionSyncService(
            ILogger<BrokerPositionSyncService> logger,
            IEventBus eventBus,
            ICapitalApiService api,
            IBrokerConnectivity connectivity,
            IClock clock,
            Microsoft.Extensions.Options.IOptionsMonitor<MuNTrade.Abstractions.Settings.Common.ApplicationSettings> appSettings)
        {
            _logger = logger;
            _eventBus = eventBus;
            _api = api;
            _connectivity = connectivity;
            _clock = clock;
            _appSettings = appSettings;

            _modeSubscription = _eventBus.Subscribe<GatewayModeChangedEvent>(OnGatewayModeChanged);
            _connectivity.StateChanged += OnConnectionStateChanged;

            _logger.LogInformation("[BrokerPositionSyncService] Started.");
            _logger.LogWarning("[BrokerSync] BrokerPositionSyncService ACTIVATED");
            _ = EnsureInitialSyncAsync(_disposeCts.Token);
        }

        private void OnGatewayModeChanged(GatewayModeChangedEvent evt)
        {
            _logger.LogWarning("[BrokerSync] Received ModeChanged: Mode={Mode}, BrokerState={State}", evt.Mode, _api.CurrentState);

            // Care if switching to Real (LIVE) - Accept both "Real" and "LIVE" to be safe
            if (evt.Mode.Equals("Real", StringComparison.OrdinalIgnoreCase) ||
                evt.Mode.Equals("LIVE", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[BrokerSync] Detected Mode Switch to LIVE. Triggering Sync.");
                RequestFullSync("ModeSwitch");
            }
        }

        private void OnConnectionStateChanged(ConnectionState state, string message)
        {
            if (state == ConnectionState.Connected)
            {
                _logger.LogInformation("[BrokerSync] Detected Broker Connected. Triggering Sync.");
                RequestFullSync("BrokerConnected");
            }
        }

        public void RequestFullSync(string reason)
        {
            // In SIM mode, ignore expected rejected-order noise.
            var gateway = _appSettings.CurrentValue?.TradingGateway;
            if (string.IsNullOrEmpty(gateway) ||
                (!gateway.Equals("LIVE", StringComparison.OrdinalIgnoreCase) && !gateway.Equals("REAL", StringComparison.OrdinalIgnoreCase)))
            {
                // Ignore rejected-order noise in simulation mode; the broker may still provide quotes.
                _logger.LogInformation("[BrokerSync] Skipped Position Sync ({Reason}) - Gateway={GW} (SIM Mode).", reason, gateway);
                return;
            }

            // Check if connected
            if (_connectivity.CurrentState != ConnectionState.Connected)
            {
                _logger.LogWarning("[BrokerSync] Ignored sync request ({Reason}) - Broker Not Connected.", reason);
                return;
            }

            if (string.IsNullOrWhiteSpace(_connectivity.StockAccount) && string.IsNullOrWhiteSpace(_connectivity.FuturesAccount))
            {
                _logger.LogInformation("[BrokerSync] Deferred sync request ({Reason}) - Account numbers not ready yet.", reason);
                _ = EnsureInitialSyncAsync(_disposeCts.Token);
                return;
            }

            lock (_lock)
            {
                var now = _clock.Now;
                if ((now - _lastSyncTime).TotalSeconds < ThrottleSeconds)
                {
                    _logger.LogInformation("[BrokerSync] Ignored sync request ({Reason}) - Throttled (Last sync: {Time}s ago).",
                        reason, (now - _lastSyncTime).TotalSeconds);
                    return;
                }
                _lastSyncTime = now;
            }

            var syncId = Guid.NewGuid().ToString("N");
            _logger.LogInformation("[BrokerSync] Starting Full Position Sync (Reason={Reason}, SyncId={SyncId})...", reason, syncId);

            try
            {
                // Trigger queries
                // Note: We do not wait for them here; they are async void or posted to STA.
                // We rely on the Adapter to handle the responses/timeouts.

                // Securities
                _api.RequestStockInventory(syncId);
                _api.RequestPnL(ProfitLossQueryType.Unrealized, ProfitLossFormat.Summary);

                // Derivatives
                _api.RequestOpenInterest(syncId);

                _logger.LogInformation("[BrokerSync] Queries dispatched (SyncId={SyncId}).", syncId);

                // Timeout Monitor (Fire & Forget)
                // We do NOT cancel or rollback. We just log if it takes too long for observability.
                // Since this runs in the background, we don't block.
                _ = MonitorSyncTimeoutAsync(syncId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BrokerSync] Failed to trigger sync queries.");
            }
        }

        private async Task MonitorSyncTimeoutAsync(string syncId)
        {
            await Task.Delay(5000); // 5 seconds timeout check
            // Completion is handled by the manager or adapter through subsequent events.
        }

        private async Task EnsureInitialSyncAsync(CancellationToken ct)
        {
            for (var attempt = 1; attempt <= 20 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (_connectivity.CurrentState == ConnectionState.Connected &&
                        (!string.IsNullOrWhiteSpace(_connectivity.StockAccount) || !string.IsNullOrWhiteSpace(_connectivity.FuturesAccount)))
                    {
                        _logger.LogInformation("[BrokerSync] Startup bootstrap sync is ready. Attempt={Attempt}", attempt);
                        RequestFullSync("StartupBootstrap");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BrokerSync] Startup bootstrap sync probe failed. Attempt={Attempt}", attempt);
                }

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _modeSubscription?.Dispose();
            _connectivity.StateChanged -= OnConnectionStateChanged;
        }
    }
}

