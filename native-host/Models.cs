using System.Text.Json;

namespace FocusApp.BrowserBridge;

public sealed record BridgeError(string Code, string Message, bool Retryable = true);
public sealed class BridgeException(string code, string message, bool retryable = true) : Exception(message)
{
    public BridgeError Error { get; } = new(code, message, retryable);
}
public sealed record BrowserRule(string Id, string Name, string Domain, bool IncludeSubdomains = true);
public sealed record BrowserFocus(string State, Guid? SessionId, bool IsForcedMode, bool IsAutomatic,
    int ConfiguredSeconds, DateTimeOffset? PlannedEndAtUtc, int? RemainingSeconds, string RemainingAccuracy = "estimatedUtc");
public sealed record WebsitePolicy(bool ShouldBlock, string Reason, string RuleSource, IReadOnlyList<BrowserRule> Rules,
    string EnforcementMode = "mirrorReportedProtection", string Scope = "mainFrame");
public sealed record DesktopProtection(string State, bool WebsiteProtectionActive, DateTimeOffset? ExpiresAtUtc, string? ErrorCode);
public sealed record BrowserSnapshot(string Epoch, long Sequence, string ServiceInstanceId, string SourceRevision,
    DateTimeOffset ObservedAtUtc, DateTimeOffset LeaseExpiresAtUtc, BrowserFocus Focus, WebsitePolicy WebsitePolicy,
    DesktopProtection DesktopProtection, string Consistency = "sampled", string Source = "service",
    string? EffectiveTheme = null);

public interface ISnapshotProvider : IAsyncDisposable
{
    Task<BrowserSnapshot> ReadAsync(CancellationToken ct);
    Task WaitForChangeAsync(CancellationToken ct);
}

public static class BridgeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
}
