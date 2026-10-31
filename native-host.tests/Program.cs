using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using FocusApp.BrowserBridge;
using FocusApp.Contracts;

if (args.Contains("--probe-service"))
{
    await using var provider = new ServiceSnapshotProvider();
    try
    {
        var s = await provider.ReadAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { s.ServiceInstanceId, s.Focus.State,
            s.Focus.RemainingSeconds, s.WebsitePolicy.ShouldBlock, domainCount = s.WebsitePolicy.Rules.Count,
            s.DesktopProtection, s.Consistency, s.EffectiveTheme }, BridgeJson.Options));
        return 0;
    }
    catch (BridgeException e) { Console.Error.WriteLine($"{e.Error.Code}: {e.Error.Message}"); return 2; }
}

var tests = new List<(string, Func<Task>)>();
var now = DateTimeOffset.UtcNow;
var ping = new PingResponse("test-service", now.AddMinutes(-1), 2);
var access = new AccessControlStatusDto(AccessControlRuntimeState.Active, true, false, 1, now.AddMinutes(20), null);
var idle = new FocusRuntimeStatusDto(FocusRuntimeState.Idle, null, null, null);
var website = new LocalWebsiteRuleDto(Guid.NewGuid(), "示例网站", "https://WWW.Example.com/path", true, 0);
LocalFocusSessionDto Session(bool forced = false) => new(Guid.NewGuid(), LocalFocusSessionStatusDto.Focusing, forced,
    1800, 0, now.AddSeconds(-5), now, now.AddMinutes(30), null, null, null, null, false, null, null, []);
LocalDataSnapshotDto Data(LocalFocusSessionDto? s, params LocalWebsiteRuleDto[] rules) => new(42,
    s is null ? [] : [s], [], [], rules, [], [], new(false, false, false, false, false, false, null, now), [], []);
BrowserSnapshot Project(LocalDataSnapshotDto d, FocusRuntimeStatusDto? runtime = null, AccessControlStatusDto? protection = null) =>
    Projection.Create(ping, d, protection ?? access, runtime ?? idle, now, "epoch");
void Check(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
void Error(string code, Action action)
{
    try { action(); } catch (BridgeException e) when (e.Error.Code == code) { return; }
    throw new Exception($"Expected {code}");
}
void Add(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));

Add("ordinary Focusing + runtime Idle + BlockingEnabled false uses current rules", () =>
{
    var s = Project(Data(Session(), website));
    Check(s.Focus.State == "focusing" && s.WebsitePolicy.ShouldBlock);
    Check(s.WebsitePolicy.Rules.Single().Domain == "example.com");
    Check(s.WebsitePolicy.Rules.Single().Name == "示例网站" && s.Focus.RemainingSeconds == 1800);
});
Add("focus theme uses the app's preference and local greeting boundaries, never OS theme", () =>
{
    foreach (var (hour, minute, expected) in new[] {
        (4, 59, "dark"), (5, 0, "light"), (11, 59, "light"), (12, 0, "light"),
        (17, 59, "light"), (18, 0, "dark"), (22, 59, "dark"), (23, 0, "dark") })
    {
        var local = new DateTimeOffset(2026, 10, 29, hour, minute, 0, TimeSpan.FromHours(8));
        Check(FocusThemeResolver.Resolve("System", local) == expected);
        Check(FocusThemeResolver.Resolve("Light", local) == "light");
        Check(FocusThemeResolver.Resolve("Dark", local) == "dark");
        Check(FocusThemeResolver.Resolve("invalid", local) == expected);
    }
});
Add("effective theme follows settings during ordinary and forced focus without changing policy", () =>
{
    foreach (var forced in new[] { false, true })
    {
        var session = Session(forced) with { WebsiteRuleSnapshots = [website] };
        var runtime = forced ? new FocusRuntimeStatusDto(FocusRuntimeState.Focusing, session.SessionId, session.PlannedEndAtUtc, null) : idle;
        var data = Data(session, website);
        foreach (var preference in new[] { "Dark", "Light", "Dark", "System" })
        {
            var result = Project(data with { Settings = data.Settings with { FocusTheme = preference } }, runtime);
            Check(result.EffectiveTheme == FocusThemeResolver.Resolve(preference, now.ToLocalTime()));
            Check(result.WebsitePolicy.ShouldBlock && result.Focus.RemainingSeconds == 1800);
            Check(result.WebsitePolicy.Rules.Single().Domain == "example.com");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.Options));
            Check(json.RootElement.GetProperty("effectiveTheme").GetString() == result.EffectiveTheme);
        }
    }
});
Add("forced focus uses snapshot and never current rules", () =>
{
    var session = Session(true) with { BlockingEnabled = true, WebsiteRuleSnapshots = [website] };
    var runtime = new FocusRuntimeStatusDto(FocusRuntimeState.Focusing, session.SessionId, session.PlannedEndAtUtc, null);
    var s = Project(Data(session, website with { Address = "wrong.test" }), runtime);
    Check(s.WebsitePolicy.RuleSource == "sessionSnapshot" && s.WebsitePolicy.Rules.Single().Domain == "example.com");
    var broken = session with { WebsiteRuleSnapshots = [] };
    Error("INVALID_RULE_SNAPSHOT", () => Project(Data(broken, website), runtime));
});
Add("forced empty website snapshot with application protection stays empty", () =>
{
    var session = Session(true) with { BlockingEnabled = true, ApplicationRuleSnapshots = [new(Guid.NewGuid(), "App", "app.exe", true, 0)] };
    var runtime = new FocusRuntimeStatusDto(FocusRuntimeState.Focusing, session.SessionId, session.PlannedEndAtUtc, null);
    var s = Project(Data(session, website), runtime);
    Check(!s.WebsitePolicy.ShouldBlock && s.WebsitePolicy.Rules.Count == 0);
});
Add("forced runtime Faulted preserves focus state and reports health", () =>
{
    var session = Session(true) with { WebsiteRuleSnapshots = [website] };
    var runtime = new FocusRuntimeStatusDto(FocusRuntimeState.Faulted, session.SessionId, session.PlannedEndAtUtc, "failure");
    var s = Project(Data(session), runtime);
    Check(s.Focus.State == "focusing" && s.DesktopProtection.ErrorCode == "FOCUS_RUNTIME_FAULTED");
});
Add("preparing has full configured duration and no blocking", () =>
{
    var session = Session() with { Status = LocalFocusSessionStatusDto.Preparing };
    var s = Project(Data(session, website));
    Check(!s.WebsitePolicy.ShouldBlock && s.Focus.State == "preparing" && s.Focus.RemainingSeconds == 1800);
});
Add("expired ordinary session cannot reactivate from residual protection", () =>
{
    var s = Project(Data(Session() with { PreparationStartedAtUtc = now.AddSeconds(-65), FocusStartedAtUtc = now.AddSeconds(-60), PlannedEndAtUtc = now.AddSeconds(-1) }, website));
    Check(!s.WebsitePolicy.ShouldBlock && s.Focus.State == "idle" && s.Focus.RemainingSeconds == 0);
});
Add("no session with explicit protection has unknown focus remaining", () =>
{
    var s = Project(Data(null, website));
    Check(s.WebsitePolicy.ShouldBlock && s.Focus.State == "unknown" && s.Focus.RemainingSeconds is null);
});
Add("empty/disabled rules and inactive/conflict/expired protection do not block", () =>
{
    Check(!Project(Data(Session(), website with { IsEnabled = false })).WebsitePolicy.ShouldBlock);
    foreach (var state in new[] { AccessControlRuntimeState.Inactive, AccessControlRuntimeState.ProxyConflict, AccessControlRuntimeState.Faulted })
        Check(!Project(Data(Session(), website), protection: access with { State = state }).WebsitePolicy.ShouldBlock);
    Check(!Project(Data(Session(), website), protection: access with { ExpiresAtUtc = now }).WebsitePolicy.ShouldBlock);
});
Add("Core normalization handles IDN and rejects IP/localhost/trailing dot", () =>
{
    Check(Project(Data(Session(), website with { Address = "https://例子.测试:123/a" })).WebsitePolicy.Rules.Single().Domain == "xn--fsqu00a.xn--0zwm56d");
    foreach (var domain in new[] { "127.0.0.1", "localhost", "example.com.", "*.example.com", "file:///foo" })
        Error("INVALID_RULE", () => Project(Data(Session(), website with { Address = domain })));
});
Add("duplicate domains merge deterministically; >1000 fails without truncation", () =>
{
    Check(Project(Data(Session(), website, website with { Id = Guid.NewGuid() })).WebsitePolicy.Rules.Count == 1);
    var many = Enumerable.Range(0, 1001).Select(i => website with { Id = Guid.NewGuid(), Address = $"d{i}.example.com" }).ToArray();
    Error("RULESET_TOO_LARGE", () => Project(Data(Session(), many)));
});
Add("duplicate active sessions or forced runtime mismatch are errors", () =>
{
    var d = Data(Session(), website) with { FocusSessions = [Session(), Session()] };
    Error("SNAPSHOT_INCONSISTENT", () => Project(d));
    Error("SNAPSHOT_INCONSISTENT", () => Project(Data(Session(true), website)));
    var forced = Session(true) with { WebsiteRuleSnapshots = [website] };
    var preparing = new FocusRuntimeStatusDto(FocusRuntimeState.Preparing, forced.SessionId, forced.PlannedEndAtUtc, null);
    Error("SNAPSHOT_INCONSISTENT", () => Project(Data(forced), preparing));
    Error("SNAPSHOT_INCONSISTENT", () => Project(Data(Session() with { FocusStartedAtUtc = null }, website)));
});

tests.Add(("Native frames handle fragmented UTF8, adjacent messages, EOF and bad lengths", async () =>
{
    using var stream = new MemoryStream();
    await NativeFrames.WriteAsync(stream, new { message = "你好 🌿" }, default);
    await NativeFrames.WriteAsync(stream, new { number = 2 }, default);
    stream.Position = 0;
    using var chunks = new ByteStream(stream.ToArray());
    using var a = await NativeFrames.ReadAsync(chunks, default);
    using var b = await NativeFrames.ReadAsync(chunks, default);
    Check(a!.RootElement.GetProperty("message").GetString() == "你好 🌿" && b!.RootElement.GetProperty("number").GetInt32() == 2);
    Check(await NativeFrames.ReadAsync(chunks, default) is null);
    foreach (var length in new[] { -1, 0, NativeFrames.MaximumBytes + 1 })
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        try { await NativeFrames.ReadAsync(new MemoryStream(bytes), default); throw new Exception("Expected bad length"); }
        catch (BridgeException e) when (e.Error.Code == "PAYLOAD_TOO_LARGE") { }
    }
    try { await NativeFrames.ReadAsync(new MemoryStream([1, 0]), default); throw new Exception("Expected partial header failure"); }
    catch (EndOfStreamException) { }
    try { await NativeFrames.ReadAsync(new MemoryStream([10, 0, 0, 0, 1]), default); throw new Exception("Expected partial body failure"); }
    catch (EndOfStreamException) { }
}));

tests.Add(("Native protocol rejects writes/unknown versions, correlates IDs and increments sequence", async () =>
{
    using var input = new MemoryStream(); using var output = new MemoryStream();
    async Task Request(string id, string method, int version = 1) =>
        await NativeFrames.WriteAsync(input, new { version, type = "request", id, method, @params = new { } }, default);
    await Request("hello", "hello"); await Request("no-write", "focus.normal.start");
    await Request("first", "snapshot.subscribe"); await Request("second", "snapshot.get");
    await Request("bad-version", "hello", 2); await Request("stop", "snapshot.unsubscribe");
    input.Position = 0;
    await using var provider = new DemoSnapshotProvider();
    await new NativeServer(provider, input, output).RunAsync();
    output.Position = 0;
    var responses = new List<JsonElement>();
    while (await NativeFrames.ReadAsync(output, default) is { } doc) { using (doc) responses.Add(doc.RootElement.Clone()); }
    Check(responses.Count == 6);
    Check(responses[1].GetProperty("id").GetString() == "no-write" && responses[1].GetProperty("error").GetProperty("code").GetString() == "METHOD_NOT_ALLOWED");
    Check(responses[2].GetProperty("result").GetProperty("sequence").GetInt32() == 1);
    Check(responses[3].GetProperty("result").GetProperty("sequence").GetInt32() == 2);
    Check(responses[4].GetProperty("error").GetProperty("code").GetString() == "PROTOCOL_UNSUPPORTED");
}));

tests.Add(("real NamedPipe IPC uses four readonly queries; initialization event triggers resample", async () =>
{
    var pipeName = "FocusBrowserTest-" + Guid.NewGuid().ToString("N");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var operations = new List<string>();
    var session = Session();
    var data = Data(session, website);
    var sendEvent = true;
    var server = Task.Run(async () =>
    {
        await pipe.WaitForConnectionAsync(timeout.Token);
        try
        {
            while (await IpcFrameCodec.ReadAsync(pipe, timeout.Token) is { } request)
            {
                Check(request.ClientRole == IpcClientRole.Desktop);
                operations.Add(request.Operation);
                object payload = request.Operation switch
                {
                    IpcOperations.Ping => ping,
                    IpcOperations.GetState => data,
                    IpcOperations.GetAccessControlStatus => access,
                    IpcOperations.GetFocusRuntimeStatus => idle,
                    _ => throw new Exception("Write or unsupported IPC operation detected")
                };
                if (request.Operation == IpcOperations.GetState && sendEvent)
                {
                    sendEvent = false;
                    await IpcFrameCodec.WriteAsync(pipe, IpcEnvelope.CreateEvent(IpcOperations.StateChanged, 1, new StateChangedEvent(42, data)), timeout.Token);
                }
                await IpcFrameCodec.WriteAsync(pipe, IpcEnvelope.CreateSuccess(request, payload), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    });
    await using (var provider = new ServiceSnapshotProvider(pipeName))
    {
        var snapshot = await provider.ReadAsync(timeout.Token);
        Check(snapshot.WebsitePolicy.ShouldBlock && snapshot.Focus.State == "focusing");
        Check(operations.Count >= 8, "Initialization event was ignored");
        Check(operations.Distinct().Count() == 4);
    }
    await server;
}));

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

sealed class ByteStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}
