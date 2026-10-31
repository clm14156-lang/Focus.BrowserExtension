namespace FocusApp.BrowserBridge;

// Explicit development fixture: no Service access, no WPF claims. Timer starts with Host process.
public sealed class DemoSnapshotProvider : ISnapshotProvider
{
    private readonly DateTimeOffset _end = DateTimeOffset.UtcNow.AddSeconds(60);
    private readonly string _epoch = Guid.NewGuid().ToString();
    public Task<BrowserSnapshot> ReadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = Math.Max(0, (int)Math.Ceiling((_end - now).TotalSeconds));
        return Task.FromResult(new BrowserSnapshot(_epoch, 0, "DEMO-NOT-WPF", "0", now, now.AddSeconds(30),
            new BrowserFocus(seconds > 0 ? "focusing" : "idle", Guid.Empty, false, false, 60, _end, seconds),
            new WebsitePolicy(seconds > 0, "demo", "current", [new BrowserRule("demo-example", "示例网站（演示）", "example.com")]),
            new DesktopProtection(seconds > 0 ? "active" : "inactive", seconds > 0, _end, null), Source: "demo", EffectiveTheme: "light"));
    }
    public Task WaitForChangeAsync(CancellationToken ct) => Task.Delay(1000, ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
