using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Synapse.Networking.Models;

namespace Synapse.Managers;

[UsedImplicitly]
internal class TimeSyncManager : IDisposable
{
    private const int MAX_TIMEOUT = 10000;
    private const int PING_INTERVAL = 10000;

    private readonly CancellationTokenManager _cancellationTokenManager;
    private readonly RollingAverage _latency = new(30);
    private readonly NetworkManager _networkManager;
    private readonly RollingAverage _serverOffset = new(30);
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource _cancelPingTimeout = new();

    private bool? _didSyncSucceed;
    private bool _disposed;
    private TaskCompletionSource<object?>? _pingTask;

    private TimeSyncManager(NetworkManager networkManager, CancellationTokenManager cancellationTokenManager)
    {
        _networkManager = networkManager;
        _cancellationTokenManager = cancellationTokenManager;
        networkManager.PreparePong += CapturePong;
        networkManager.Connecting += OnConnecting;
        networkManager.Disconnected += OnDisconnected;
    }

    internal event Action<bool>? Synced
    {
        add
        {
            if (_didSyncSucceed != null)
            {
                value?.Invoke(_didSyncSucceed.Value);
                return;
            }

            SyncedBacking += value;
        }

        remove => SyncedBacking -= value;
    }

    private event Action<bool>? SyncedBacking;

    internal float ElapsedSeconds => _stopwatch.ElapsedMilliseconds * 0.001f;

    internal float Latency => _latency.currentAverage;

    internal float Offset => _serverOffset.currentAverage + Latency;

    internal float SyncTime => ElapsedSeconds + Offset;

    public void Dispose()
    {
        _disposed = true;
        _networkManager.PreparePong -= CapturePong;
        _networkManager.Connecting -= OnConnecting;
        _networkManager.Disconnected -= OnDisconnected;
        _cancellationTokenManager.Dispose();
    }

    internal void OnConnecting(ConnectingStage connectingStage, int retries)
    {
        if (connectingStage == ConnectingStage.ReceivingData)
        {
            _ = StartSync();
        }
    }

    internal void OnDisconnected(string _)
    {
        _cancellationTokenManager.Cancel();
        _cancelPingTimeout.Cancel();
        _stopwatch.Stop();
        _pingTask?.TrySetCanceled();
        _pingTask = null;
        _didSyncSucceed = null;
    }

    internal async Task Ping()
    {
        _cancelPingTimeout.Cancel();
        _cancelPingTimeout = new CancellationTokenSource();
        _pingTask?.SetResult(null);
        _pingTask = new TaskCompletionSource<object?>();
        _ = _networkManager.Send(ServerOpcode.Ping, ElapsedSeconds);
        _ = Timeout(MAX_TIMEOUT, _cancelPingTimeout.Token);
        await _pingTask.Task;
    }

    internal async Task StartSync()
    {
        _latency.Reset();
        _serverOffset.Reset();
        _stopwatch.Restart();

        await Ping();
        bool success = Latency < MAX_TIMEOUT;
        _didSyncSucceed = success;
        SyncedBacking?.Invoke(success);
        SyncedBacking = null;

        if (success)
        {
            _ = PingLoop(PING_INTERVAL, _cancellationTokenManager.Reset());
        }
    }

    private async Task PingLoop(int millisecondLoop, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Ping();
            await Task.Delay(millisecondLoop, cancellationToken);
        }
    }

    private Action<float, float> CapturePong()
    {
        float localTime = ElapsedSeconds;
        float offsetTime = ElapsedSeconds;
        TaskCompletionSource<object?>? ping = _pingTask;
        CancellationTokenSource timeout = _cancelPingTimeout;
        return (sentLocalTime, serverTime) =>
        {
            if (_disposed || !ReferenceEquals(_pingTask, ping) || !ReferenceEquals(_cancelPingTimeout, timeout))
            {
                return;
            }

            float roundTrip = localTime - sentLocalTime;
            _serverOffset.Update(serverTime - offsetTime);
            UpdateLatency(roundTrip * 0.5f);
        };
    }

    private async Task Timeout(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        UpdateLatency(milliseconds * 0.001f);
    }

    private void UpdateLatency(float value)
    {
        _cancelPingTimeout.Cancel();
        if (_pingTask == null)
        {
            return;
        }

        _latency.Update(value);
        TaskCompletionSource<object?>? task = _pingTask;
        _pingTask = null;
        task.SetResult(null);
    }
}
