using System.Threading.Channels;
using FocusApp.Contracts;

namespace FocusApp.BrowserBridge;

// NativeServer serializes reads. IPC event handlers only signal; they never wait on the reader loop.
public sealed class ServiceSnapshotProvider(string? pipeName = null) : ISnapshotProvider
{
    private NamedPipeIpcClient? _client;
    private string _epoch = Guid.NewGuid().ToString();
    private string? _serviceId;
    private long _generation;
    private long _eventSequence;
    private int _connectionLost;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite });

    public async Task<BrowserSnapshot> ReadAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            if (Interlocked.Exchange(ref _connectionLost, 0) != 0)
                throw new BridgeException("SERVICE_UNAVAILABLE", "本机 Service 连接已断开");
            if (_client is null || !_client.IsConnected)
            {
                if (_client is not null) await _client.DisposeAsync();
                _client = new NamedPipeIpcClient(IpcClientRole.Desktop, pipeName);
                _client.EventReceived += (_, envelope) =>
                {
                    if (envelope.Sequence > 0)
                    {
                        var last = Interlocked.Exchange(ref _eventSequence, envelope.Sequence);
                        if (last > 0 && envelope.Sequence != last + 1) Signal();
                    }
                    if (envelope.Operation is IpcOperations.StateChanged or IpcOperations.FocusRuntimeStateChanged or IpcOperations.AccessControlStateChanged)
                        Signal();
                };
                _client.Disconnected += (_, _) => { Interlocked.Exchange(ref _connectionLost, 1); Signal(); };
                await _client.ConnectAsync(TimeSpan.FromSeconds(3), budget.Token);
                _epoch = Guid.NewGuid().ToString();
                _serviceId = null;
                _eventSequence = 0;
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var generation = Interlocked.Read(ref _generation);
                var ping = await Query<PingResponse>(IpcOperations.Ping, budget.Token);
                if (_serviceId != ping.ServiceInstanceId)
                {
                    _epoch = Guid.NewGuid().ToString();
                    _serviceId = ping.ServiceInstanceId;
                }
                var data = await Query<LocalDataSnapshotDto>(IpcOperations.GetState, budget.Token);
                var access = await Query<AccessControlStatusDto>(IpcOperations.GetAccessControlStatus, budget.Token);
                var runtime = await Query<FocusRuntimeStatusDto>(IpcOperations.GetFocusRuntimeStatus, budget.Token);
                if (generation != Interlocked.Read(ref _generation)) continue;
                return Projection.Create(ping, data, access, runtime, DateTimeOffset.UtcNow, _epoch);
            }
            throw new BridgeException("SYNC_IN_PROGRESS", "Service 状态持续变化，请等待重新同步");
        }
        catch (BridgeException) { throw; }
        catch (IpcRemoteException e)
        {
            throw new BridgeException(e.Error.Code == IpcErrorCode.ProtocolVersionMismatch ? "PROTOCOL_UNSUPPORTED"
                : e.Error.Code == IpcErrorCode.TimedOut ? "TIMEOUT" : "SERVICE_UNAVAILABLE", "Service 查询失败", e.Error.Code != IpcErrorCode.ProtocolVersionMismatch);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new BridgeException("TIMEOUT", "Service 读取超过总时限"); }
        catch (IpcConnectionException e) when (e.InnerException is IpcProtocolException)
        { throw new BridgeException("PROTOCOL_UNSUPPORTED", "Service IPC 版本或帧格式不兼容", false); }
        catch (Exception e) when (e is IpcConnectionException or IOException or IpcProtocolException or System.Text.Json.JsonException)
        { throw new BridgeException("SERVICE_UNAVAILABLE", "暂时无法读取本机 Service；请检查服务及 IPC 版本"); }
    }

    // Hardcoded read-only whitelist; browser input is never an IPC operation.
    private Task<T> Query<T>(string operation, CancellationToken ct)
    {
        if (operation is not (IpcOperations.Ping or IpcOperations.GetState or IpcOperations.GetAccessControlStatus or IpcOperations.GetFocusRuntimeStatus))
            throw new BridgeException("METHOD_NOT_ALLOWED", "不允许此 IPC 操作", false);
        return _client!.SendAsync<EmptyPayload, T>(operation, new EmptyPayload(), TimeSpan.FromSeconds(5), ct);
    }

    private void Signal()
    {
        Interlocked.Increment(ref _generation);
        _changes.Writer.TryWrite(true);
    }

    public async Task WaitForChangeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await _changes.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        await Task.Delay(100, ct);
        while (_changes.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
