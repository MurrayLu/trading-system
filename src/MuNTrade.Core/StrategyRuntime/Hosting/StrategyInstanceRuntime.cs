using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Core.StrategyRuntime.Hosting
{
    public sealed class StrategyInstanceRuntime
    {
        private readonly IStrategy _strategy;
        private readonly IClock _clock;
        private readonly ILogger _logger;
        private readonly object _sync = new();
        private StrategyRuntimeState _state = StrategyRuntimeState.Initializing;
        private DateTimeOffset _lastSeenAt = DateTimeOffset.MinValue;

        // Hard Stop Infrastructure
        private CancellationTokenSource? _runCts;
        private int _inFlight;
        private long _runGeneration;

        public long RunGeneration
        {
            get { lock (_sync) return _runGeneration; }
        }

        public StrategyInstanceRuntime(IStrategy strategy, IClock clock, ILogger logger, string accountKey)
        {
            _strategy = strategy;
            _clock = clock;
            _logger = logger;
            _accountKey = accountKey;
        }

        public StrategyInstanceId InstanceId => _strategy.InstanceId;
        public IStrategy Strategy => _strategy;

        private readonly string _accountKey;
        public string AccountKey => _accountKey; // [10C] Pinned Key

        // Strict Lifecycle State
        public StrategyRuntimeState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        public DateTimeOffset LastSeenAt
        {
            get
            {
                lock (_sync)
                {
                    return _lastSeenAt;
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_state == StrategyRuntimeState.Running)
                {
                    return;
                }

                _state = StrategyRuntimeState.Initializing;

                // Reset Run Cycle
                _runGeneration++;
                _runCts?.Dispose();
                _runCts = new CancellationTokenSource();
            }

            await _strategy.StartAsync(cancellationToken);

            lock (_sync)
            {
                if (_state == StrategyRuntimeState.Initializing)
                {
                    _state = StrategyRuntimeState.Running;
                }
                _lastSeenAt = new DateTimeOffset(_clock.Now);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            bool shouldStop = false;
            CancellationTokenSource? localCts = null;

            lock (_sync)
            {
                // If already stopped or stopping, idempotent return
                if (_state == StrategyRuntimeState.Stopped || _state == StrategyRuntimeState.Stopping)
                {
                    return;
                }

                _state = StrategyRuntimeState.Stopping;
                shouldStop = true;
                localCts = _runCts;
            }

            if (shouldStop)
            {
                // 1. Hard Cancel ASAP
                localCts?.Cancel();

                // 2. Wait for In-Flight (with timeout)
                int waitMs = 2000;
                int elapsed = 0;
                while (_inFlight > 0 && elapsed < waitMs)
                {
                    await Task.Delay(50);
                    elapsed += 50;
                }

                if (_inFlight > 0)
                {
                    _logger.LogWarning("Strategy {InstanceId} stop timed out waiting for {Count} in-flight events.", InstanceId.Value, _inFlight);
                }

                // 3. Graceful Stop Call
                try
                {
                    await _strategy.StopAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Strategy.StopAsync for {InstanceId}", InstanceId.Value);
                }
                finally
                {
                    lock (_sync)
                    {
                        _state = StrategyRuntimeState.Stopped;
                        _runCts?.Dispose();
                        _runCts = null;
                    }
                }
            }
        }

        public async ValueTask DeliverAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt == null)
            {
                return;
            }

            CancellationToken runToken;

            lock (_sync)
            {
                if (_state != StrategyRuntimeState.Running)
                {
                    return;
                }
                _lastSeenAt = new DateTimeOffset(_clock.Now);
                runToken = _runCts?.Token ?? CancellationToken.None;
            }

            // Track In-Flight
            Interlocked.Increment(ref _inFlight);
            try
            {
                // Link tokens (Caller token + Run token)
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken))
                {
                    await _strategy.OnEventAsync(evt, linkedCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on Hard Stop
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void MarkFaulted()
        {
            lock (_sync)
            {
                _state = StrategyRuntimeState.Faulted;
                _runCts?.Cancel(); // Also stop execution if faulted
            }

            _logger.LogWarning("Strategy instance {InstanceId} marked as faulted", InstanceId.Value);
        }
    }
}

