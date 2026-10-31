using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;

namespace FocusApp.Contracts;

public sealed class NamedPipeIpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IpcClientRole _role;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private bool _disposed;

    public NamedPipeIpcClient(IpcClientRole role, string? pipeName = null)
    {
        _role = role;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? IpcProtocol.DefaultPipeName : pipeName;
    }

    public event EventHandler<IpcEnvelope>? EventReceived;

    public event EventHandler? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true && _readerTask is { IsCompleted: false };

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await DisconnectCoreAsync();
            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            try
            {
                await pipe.ConnectAsync(timeoutCancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException)
            {
                pipe.Dispose();
                throw new IpcConnectionException("无法连接 FocusApp 后台服务。", exception);
            }

            _pipe = pipe;
            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReadLoopAsync(pipe, _readerCancellation.Token);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Guid? requestId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pipe = _pipe;
        if (pipe is null || !IsConnected)
        {
            throw new IpcConnectionException("FocusApp 后台服务未连接。");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var envelope = IpcEnvelope.CreateRequest(requestId ?? Guid.NewGuid(), _role, operation, request);
        var completion = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(envelope.RequestId, completion))
        {
            throw new InvalidOperationException("请求关联 ID 已在使用中。");
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await IpcFrameCodec.WriteAsync(pipe, envelope, cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            IpcEnvelope response;
            try
            {
                response = await completion.Task.WaitAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IpcRemoteException(new IpcErrorResult(
                    IpcErrorCode.TimedOut,
                    "等待后台服务响应超时。",
                    true));
            }

            if (response.Error is not null)
            {
                throw new IpcRemoteException(response.Error);
            }

            return response.ReadPayload<TResponse>();
        }
        catch (IOException exception)
        {
            throw new IpcConnectionException("与 FocusApp 后台服务的连接已断开。", exception);
        }
        finally
        {
            _pending.TryRemove(envelope.RequestId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await IpcFrameCodec.ReadAsync(pipe, cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.ProtocolVersion != IpcProtocol.CurrentVersion)
                {
                    throw new IpcProtocolException("后台服务返回了不兼容的协议版本。");
                }

                if (envelope.Kind == IpcMessageKind.Event)
                {
                    EventReceived?.Invoke(this, envelope);
                    continue;
                }

                if (envelope.Kind == IpcMessageKind.Response &&
                    _pending.TryGetValue(envelope.RequestId, out var completion))
                {
                    completion.TrySetResult(envelope);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or IpcProtocolException)
        {
            failure = exception;
        }
        finally
        {
            var disconnected = new IpcConnectionException("与 FocusApp 后台服务的连接已断开。", failure);
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(disconnected);
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        var cancellation = _readerCancellation;
        var readerTask = _readerTask;
        var pipe = _pipe;
        _readerCancellation = null;
        _readerTask = null;
        _pipe = null;

        cancellation?.Cancel();
        pipe?.Dispose();
        if (readerTask is not null)
        {
            try
            {
                await readerTask;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
        }

        cancellation?.Dispose();
    }
}
