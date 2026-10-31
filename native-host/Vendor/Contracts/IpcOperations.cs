namespace FocusApp.Contracts;

public static class IpcOperations
{
    public const string Ping = "system.ping";
    public const string GetState = "state.get";
    public const string SaveTarget = "targets.save";
    public const string DeleteTarget = "targets.delete";
    public const string DeleteFocusRecord = "focus-records.delete";
    public const string ReplaceWebsiteRules = "rules.websites.replace";
    public const string ReplaceApplicationRules = "rules.applications.replace";
    public const string ReplaceAutomaticRules = "rules.automatic.replace";
    public const string ReplaceDurationPresets = "duration-presets.replace";
    public const string SaveSettings = "settings.save";
    public const string SetLaunchAtStartup = "settings.launch-at-startup.set";
    public const string SetWindowsNotifications = "settings.windows-notifications.set";
    public const string ActivateAccessControl = "access-control.activate";
    public const string DeactivateAccessControl = "access-control.deactivate";
    public const string GetAccessControlStatus = "access-control.status.get";
    public const string StartForcedFocus = "focus.forced.start";
    public const string CancelForcedFocusStarting = "focus.forced.cancel-starting";
#if DEBUG
    public const string EndForcedFocusForDebug = "focus.forced.end-debug";
#endif
    public const string StartNormalFocus = "focus.normal.start";
    public const string DiscardNormalFocus = "focus.normal.discard";
    public const string UpdateFocusTasks = "focus.tasks.update";
    public const string UpdateForcedFocusTasks = "focus.forced.tasks.update";
    public const string RecordCompletedFocus = "focus.completed.record";
    public const string GetFocusRuntimeStatus = "focus.runtime-status.get";
    public const string AgentProxyActionResult = "agent.proxy-action.result";
    public const string AgentStartupRegistrationActionResult = "agent.startup-registration.result";
    public const string AgentStartupRegistrationActionRequested = "agent.startup-registration.requested";
    public const string AgentProxyReconciliationResult = "agent.proxy-reconciliation.result";
    public const string UpdateAccessControlUpstream = "access-control.upstream.update";
    public const string StateChanged = "state.changed";
    public const string FocusRuntimeStateChanged = "focus.runtime-state-changed";
    public const string AccessControlStateChanged = "access-control.state-changed";
    public const string AccessBlocked = "access-control.blocked";
    public const string AgentProxyActionRequested = "agent.proxy-action.requested";
}

public sealed record EmptyPayload;

public sealed record PingResponse(
    string ServiceInstanceId,
    DateTimeOffset ServiceStartedAtUtc,
    int ProtocolVersion);

public sealed record StateChangedEvent(long Revision, LocalDataSnapshotDto State);

public sealed record SaveTargetCommand(LocalTargetDto Target, IReadOnlyList<LocalTaskDto> Tasks);

public sealed record DeleteTargetCommand(string TargetId);
public sealed record DeleteFocusRecordCommand(Guid SessionId);

public sealed record ReplaceWebsiteRulesCommand(IReadOnlyList<LocalWebsiteRuleDto> Rules);

public sealed record ReplaceApplicationRulesCommand(IReadOnlyList<LocalApplicationRuleDto> Rules);

public sealed record ReplaceAutomaticRulesCommand(IReadOnlyList<LocalAutomaticRuleDto> Rules);

public sealed record ReplaceDurationPresetsCommand(IReadOnlyList<LocalDurationPresetDto> Presets);

public sealed record SaveSettingsCommand(
    LocalAppSettingsDto Settings,
    IReadOnlyList<LocalDurationPresetDto> DurationPresets,
    IReadOnlyList<LocalMonthlyFocusTargetDto> MonthlyFocusTargets);

public sealed record MutationResult(long Revision, LocalDataSnapshotDto State);

public sealed record SetLaunchAtStartupCommand(bool Enabled);

public sealed record SetWindowsNotificationsCommand(bool Enabled);

public sealed record AgentStartupRegistrationActionRequestedEvent(
    Guid ActionId,
    bool Enabled);

public sealed record AgentStartupRegistrationActionResultCommand(
    Guid ActionId,
    bool Succeeded,
    string? ErrorMessage);

public sealed record ActivateAccessControlCommand(DateTimeOffset ExpiresAtUtc);

public sealed record DeactivateAccessControlCommand;

public sealed record StartForcedFocusCommand(
    int ConfiguredSeconds,
    string? TargetId,
    string? TargetNameSnapshot,
    IReadOnlyList<LocalFocusSessionTaskSnapshotDto> CompletedTasks,
    Guid? AutomaticRuleId = null,
    DateTimeOffset? AutomaticOccurrenceStartedAtUtc = null);

public sealed record CancelForcedFocusStartingCommand(Guid SessionId);

public sealed record StartNormalFocusCommand(LocalFocusSessionDto Session);

public sealed record DiscardNormalFocusCommand(Guid SessionId);

public sealed record UpdateFocusTasksCommand(
    Guid SessionId,
    IReadOnlyList<LocalFocusSessionTaskSnapshotDto> CompletedTasks);

public sealed record UpdateForcedFocusTasksCommand(
    Guid SessionId,
    IReadOnlyList<LocalFocusSessionTaskSnapshotDto> CompletedTasks);

public sealed record RecordCompletedFocusCommand(LocalFocusSessionDto Session);

public enum FocusRuntimeState
{
    Idle,
    Preparing,
    Focusing,
    Completing,
    Faulted
}

public sealed record FocusRuntimeStatusDto(
    FocusRuntimeState State,
    Guid? SessionId,
    DateTimeOffset? PlannedEndAtUtc,
    string? LastError);

public sealed record FocusRuntimeStateChangedEvent(FocusRuntimeStatusDto Status);

public sealed record FocusSessionMutationResult(
    long Revision,
    LocalDataSnapshotDto State,
    FocusRuntimeStatusDto FocusStatus,
    AccessControlStatusDto AccessControlStatus);

public enum AccessControlRuntimeState
{
    Inactive,
    Activating,
    Active,
    PartiallyActive,
    ProxyConflict,
    Deactivating,
    Faulted
}

public sealed record AccessControlStatusDto(
    AccessControlRuntimeState State,
    bool WebsiteProtectionActive,
    bool ApplicationProtectionActive,
    int? LocalProxyPort,
    DateTimeOffset? ExpiresAtUtc,
    string? LastError);

public sealed record AccessControlStateChangedEvent(AccessControlStatusDto Status);

public enum BlockedTargetKind
{
    Website,
    Application
}

public sealed record AccessBlockedEvent(
    BlockedTargetKind Kind,
    Guid RuleId,
    string RuleName,
    string Target,
    DateTimeOffset ObservedAtUtc);

public enum AgentProxyActionKind
{
    Prepare,
    Apply,
    Restore
}

public enum UpstreamProxyKind
{
    Http,
    Socks5
}

public sealed record UpstreamProxyEndpointDto(
    UpstreamProxyKind Kind,
    string Host,
    int Port);

public sealed record UpstreamProxyConfigurationDto(
    UpstreamProxyEndpointDto? Http,
    UpstreamProxyEndpointDto? Https);

public sealed record AgentProxyActionRequestedEvent(
    Guid ActionId,
    AgentProxyActionKind Kind,
    int? LocalProxyPort);

public sealed record AgentProxyActionResultCommand(
    Guid ActionId,
    bool Succeeded,
    string? ErrorMessage,
    bool ConflictDetected = false,
    UpstreamProxyConfigurationDto? UpstreamProxy = null);

public sealed record AgentProxyReconciliationResultCommand(
    AgentProxyActionKind Kind,
    bool Succeeded,
    string? ErrorMessage,
    bool ConflictDetected = false);

public sealed record UpdateAccessControlUpstreamCommand(
    UpstreamProxyConfigurationDto? UpstreamProxy);

public sealed record LocalDataSnapshotDto(
    long Revision,
    IReadOnlyList<LocalFocusSessionDto> FocusSessions,
    IReadOnlyList<LocalTargetDto> Targets,
    IReadOnlyList<LocalTaskDto> Tasks,
    IReadOnlyList<LocalWebsiteRuleDto> WebsiteRules,
    IReadOnlyList<LocalApplicationRuleDto> ApplicationRules,
    IReadOnlyList<LocalAutomaticRuleDto> AutomaticRules,
    LocalAppSettingsDto Settings,
    IReadOnlyList<LocalDurationPresetDto> DurationPresets,
    IReadOnlyList<LocalMonthlyFocusTargetDto> MonthlyFocusTargets);

public enum LocalFocusSessionStatusDto
{
    Preparing,
    Focusing,
    Completed
}

public enum FocusCompletionKindDto
{
    Natural,
    EarlyEnd
}

public sealed record LocalFocusSessionDto(
    Guid SessionId,
    LocalFocusSessionStatusDto Status,
    bool IsForcedMode,
    int ConfiguredSeconds,
    int ActualSeconds,
    DateTimeOffset PreparationStartedAtUtc,
    DateTimeOffset? FocusStartedAtUtc,
    DateTimeOffset? PlannedEndAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FocusCompletionKindDto? CompletionKind,
    string? TargetId,
    string? TargetNameSnapshot,
    bool BlockingEnabled,
    Guid? AutomaticRuleId,
    DateTimeOffset? AutomaticOccurrenceStartedAtUtc,
    IReadOnlyList<LocalFocusSessionTaskSnapshotDto> CompletedTasks)
{
    public IReadOnlyList<LocalWebsiteRuleDto> WebsiteRuleSnapshots { get; init; } = [];

    public IReadOnlyList<LocalApplicationRuleDto> ApplicationRuleSnapshots { get; init; } = [];
}

public sealed record LocalFocusSessionTaskSnapshotDto(string TaskId, string TaskNameSnapshot, int SortOrder)
{
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public LocalTaskDetailsSnapshotDto? Details { get; init; }
}

public sealed record LocalTaskDetailsSnapshotDto(string Description, IReadOnlyList<LocalSubTaskSnapshotDto> SubTasks);
public sealed record LocalSubTaskSnapshotDto(string Title, bool IsCompleted);

public sealed record LocalTargetDto(
    string TargetId,
    string Name,
    bool IsArchived,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public DateTimeOffset? ArchivedAtUtc { get; init; }

    public string? IconFileName { get; init; }
    public string? IconColorHex { get; init; }

    public string? Remark { get; init; }

    public int? TargetDurationMinutes { get; init; }
}

public sealed record LocalTaskDto(
    string TaskId,
    string TargetId,
    string Name,
    bool IsCompleted,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<LocalSubTaskDto> SubTasks { get; init; } = [];
}

public sealed record LocalSubTaskDto(
    string Id,
    string TaskId,
    string Title,
    bool IsCompleted,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalWebsiteRuleDto(Guid Id, string Name, string Address, bool IsEnabled, int SortOrder);

public sealed record LocalApplicationRuleDto(Guid Id, string Name, string Path, bool IsEnabled, int SortOrder);

public sealed record LocalAutomaticRuleDto(
    Guid Id,
    IReadOnlyList<DayOfWeek> ActiveDays,
    int StartMinutes,
    int EndMinutes,
    bool IsEnabled,
    int SortOrder,
    bool IsCustom = false,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? TargetId = null);

public sealed record LocalAppSettingsDto(
    bool LaunchAtStartup,
    bool FloatingWindowEnabled,
    bool WindowsNotificationsEnabled,
    bool FocusSoundEnabled,
    bool AutomaticBlockingEnabled,
    bool ForcedModeRequested,
    string? SelectedTargetId,
    DateTimeOffset UpdatedAtUtc)
{
    public string RecentTargetIconsJson { get; init; } = "[]";
    public string FocusTheme { get; init; } = "System";
    public string FocusCharacterId { get; init; } = "white_boy01";
}

public sealed record LocalDurationPresetDto(Guid Id, int Minutes, bool IsVisible, bool IsCurrent, int SortOrder);

public sealed record LocalMonthlyFocusTargetDto(DateOnly Month, int TargetMinutes);
