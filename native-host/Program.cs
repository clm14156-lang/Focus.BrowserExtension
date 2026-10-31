using System.Text.Json;
using System.Text.RegularExpressions;

namespace FocusApp.BrowserBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "com.focusapp.browser_bridge.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            var origins = manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(v => v.GetString()).ToArray();
            var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal));
            if (origin is null || !Regex.IsMatch(origin, "^chrome-extension://[a-p]{32}/?$")
                || !origins.Contains(origin.TrimEnd('/') + "/", StringComparer.Ordinal))
                throw new BridgeException("ORIGIN_NOT_ALLOWED", "Host 调用来源不在允许列表中", false);
            // Demo requires a separate, explicit installation flag. Never silently fall back to demo.
            var demoPath = Path.Combine(AppContext.BaseDirectory, "demo.enabled");
            await using ISnapshotProvider provider = File.Exists(demoPath) ? new DemoSnapshotProvider() : new ServiceSnapshotProvider();
            await new NativeServer(provider, Console.OpenStandardInput(), Console.OpenStandardOutput()).RunAsync();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} FocusApp BrowserBridge {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
