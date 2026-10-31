using System.Text.Json;

namespace FocusApp.BrowserBridge;

public sealed class NativeServer(ISnapshotProvider provider, Stream input, Stream output)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _subscribed;
    private bool _hello;
    private long _sequence;
    private string? _epoch;
    private readonly Queue<DateTimeOffset> _rate = new();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = lifetime.Token;
        var pump = PumpAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var doc = await NativeFrames.ReadAsync(input, ct);
                if (doc is null) break;
                await _gate.WaitAsync(ct);
                try { await HandleAsync(doc.RootElement, ct); }
                finally { _gate.Release(); }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await pump; } catch (OperationCanceledException) { }
        }
    }

    private async Task HandleAsync(JsonElement request, CancellationToken ct)
    {
        string? id = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        try
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !request.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var versionNumber) || versionNumber != 1)
                throw new BridgeException("PROTOCOL_UNSUPPORTED", "需要浏览器桥接协议 v1 和有效请求 ID", false);
            if (!request.TryGetProperty("type", out var type) || type.GetString() != "request"
                || !request.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String
                || !request.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                || parameters.EnumerateObject().Any())
                throw new BridgeException("INVALID_MESSAGE", "请求格式无效", false);
            var method = methodElement.GetString();
            if (method is not ("hello" or "snapshot.get" or "snapshot.subscribe" or "snapshot.unsubscribe"))
                throw new BridgeException("METHOD_NOT_ALLOWED", "仅允许读取和订阅快照", false);
            var now = DateTimeOffset.UtcNow;
            while (_rate.TryPeek(out var time) && now - time >= TimeSpan.FromSeconds(1)) _rate.Dequeue();
            if (_rate.Count >= 10) throw new BridgeException("RATE_LIMITED", "请求过于频繁");
            _rate.Enqueue(now);
            if (method == "hello")
            {
                _hello = true;
                await Respond(id, new { protocolVersion = 1, snapshotSubscription = true, consistency = "sampled", remainingAccuracy = "estimatedUtc" }, null, ct);
                return;
            }
            if (!_hello) throw new BridgeException("INVALID_MESSAGE", "必须先进行 hello 协商", false);
            if (method == "snapshot.unsubscribe")
            {
                _subscribed = false;
                await Respond(id, new { subscribed = false }, null, ct);
                return;
            }
            var snapshot = Next(await provider.ReadAsync(ct));
            await Respond(id, snapshot, null, ct);
            if (method == "snapshot.subscribe") _subscribed = true;
        }
        catch (BridgeException e) { await Respond(id, null, e.Error, ct); }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        { await Respond(id, null, new BridgeError("INVALID_MESSAGE", "JSON 字段类型无效", false), ct); }
    }

    private BrowserSnapshot Next(BrowserSnapshot snapshot)
    {
        if (_epoch != snapshot.Epoch) { _sequence = 0; _epoch = snapshot.Epoch; }
        return snapshot with { Sequence = ++_sequence };
    }

    private Task Respond(string? id, object? result, BridgeError? error, CancellationToken ct) =>
        NativeFrames.WriteAsync(output, new { version = 1, type = "response", id, result, error }, ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await provider.WaitForChangeAsync(ct);
            await _gate.WaitAsync(ct);
            try
            {
                if (!_subscribed) continue;
                var snapshot = Next(await provider.ReadAsync(ct));
                await NativeFrames.WriteAsync(output, new { version = 1, type = "event", @event = "snapshot.changed", data = snapshot }, ct);
            }
            catch (BridgeException e)
            {
                await NativeFrames.WriteAsync(output, new { version = 1, type = "event", @event = "bridge.error", data = e.Error }, ct);
                _subscribed = false;
            }
            finally { _gate.Release(); }
        }
    }
}
