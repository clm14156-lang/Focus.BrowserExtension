using System.Globalization;
using FocusApp.Contracts;
using FocusApp.Core;

namespace FocusApp.BrowserBridge;

public static class Projection
{
    public static BrowserSnapshot Create(PingResponse ping, LocalDataSnapshotDto data, AccessControlStatusDto access,
        FocusRuntimeStatusDto runtime, DateTimeOffset now, string epoch)
    {
        if (ping.ProtocolVersion != IpcProtocol.CurrentVersion)
            throw new BridgeException("PROTOCOL_UNSUPPORTED", "Service IPC 版本不兼容", false);
        if (data.FocusSessions is null || data.WebsiteRules is null)
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "Service 数据不完整");
        var active = data.FocusSessions.Where(s => s.Status is LocalFocusSessionStatusDto.Preparing or LocalFocusSessionStatusDto.Focusing).ToArray();
        if (active.Length > 1)
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "存在多个活动专注会话");
        var session = active.SingleOrDefault();
        if (session is not null && (session.ConfiguredSeconds <= 0 || session.PlannedEndAtUtc is null
            || session.FocusStartedAtUtc is null || session.CompletedAtUtc is not null
            || session.PreparationStartedAtUtc > session.FocusStartedAtUtc
            || session.FocusStartedAtUtc >= session.PlannedEndAtUtc))
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "活动会话时间字段无效");
        if (session?.IsForcedMode == true && runtime.SessionId != session.SessionId)
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "强制会话与运行状态不一致");
        if (session?.IsForcedMode == true && (runtime.State == FocusRuntimeState.Idle
            || (session.Status == LocalFocusSessionStatusDto.Preparing && runtime.State is FocusRuntimeState.Focusing or FocusRuntimeState.Completing)
            || (session.Status == LocalFocusSessionStatusDto.Focusing && runtime.State == FocusRuntimeState.Preparing)
            || (runtime.PlannedEndAtUtc is not null && runtime.PlannedEndAtUtc != session.PlannedEndAtUtc)))
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "强制专注阶段或计划结束时间矛盾");
        if (runtime.State is FocusRuntimeState.Preparing or FocusRuntimeState.Focusing or FocusRuntimeState.Completing
            && session is null)
            throw new BridgeException("SNAPSHOT_INCONSISTENT", "运行状态与持久化会话不一致");

        var forced = session?.IsForcedMode == true;
        var source = forced ? "sessionSnapshot" : "current";
        IReadOnlyList<LocalWebsiteRuleDto> rawRules = forced ? session!.WebsiteRuleSnapshots : data.WebsiteRules;
        if (rawRules is null || (forced && session!.BlockingEnabled && rawRules.Count == 0
            && (session.ApplicationRuleSnapshots is null || session.ApplicationRuleSnapshots.Count == 0)))
            throw new BridgeException("INVALID_RULE_SNAPSHOT", "强制会话缺少必需规则快照");
        // Never substitute current rules for an empty or damaged forced snapshot.
        var rules = new List<BrowserRule>();
        foreach (var rule in rawRules.Where(r => r.IsEnabled).OrderBy(r => r.Id))
        {
            string? domain;
            try { domain = AccessControlService.NormalizeWebsiteHost(rule.Address); }
            catch (UriFormatException) { domain = null; }
            if (domain is null) throw new BridgeException("INVALID_RULE", "存在无法规范化的启用网站规则");
            var name = string.IsNullOrWhiteSpace(rule.Name) ? domain : rule.Name;
            if (name.Length > 200) throw new BridgeException("INVALID_RULE", "网站名称超过 200 字符");
            if (rules.All(r => r.Domain != domain)) rules.Add(new BrowserRule(rule.Id.ToString(), name, domain));
        }
        if (rules.Count > 1000) throw new BridgeException("RULESET_TOO_LARGE", "最多支持 1000 个规范化域名");

        var expired = session?.PlannedEndAtUtc <= now;
        var state = session is null ? "idle" : session.Status == LocalFocusSessionStatusDto.Preparing ? "preparing"
            : expired ? forced ? "completing" : "idle" : "focusing";
        if (forced && runtime.State == FocusRuntimeState.Completing) state = "completing";
        var healthyAccess = access.State is AccessControlRuntimeState.Active or AccessControlRuntimeState.PartiallyActive;
        var accessActive = healthyAccess && access.WebsiteProtectionActive && access.ExpiresAtUtc > now;
        // No current session but explicit active protection is allowed by the integration contract.
        if (session is null && accessActive) state = "unknown";
        var remaining = state switch
        {
            "idle" or "completing" => (int?)0,
            "preparing" => session!.ConfiguredSeconds,
            "focusing" => (int)Math.Clamp(Math.Ceiling((session!.PlannedEndAtUtc!.Value - now).TotalSeconds), 0, session.ConfiguredSeconds),
            _ => null
        };
        var shouldBlock = accessActive && rules.Count > 0 && state is "focusing" or "unknown";
        var reason = shouldBlock ? session is null ? "accessControlOnly" : "reportedProtectionActive"
            : rules.Count == 0 ? "emptyRules" : state == "preparing" ? "preparing" : "protectionInactive";
        var protectionError = access.LastError is not null ? "ACCESS_CONTROL_ERROR"
            : runtime.State == FocusRuntimeState.Faulted ? "FOCUS_RUNTIME_FAULTED" : null;
        return new BrowserSnapshot(epoch, 0, ping.ServiceInstanceId, data.Revision.ToString(CultureInfo.InvariantCulture),
            now, now.AddSeconds(30),
            new BrowserFocus(state, session?.SessionId, forced, session?.AutomaticRuleId is not null,
                session?.ConfiguredSeconds ?? 0, session?.PlannedEndAtUtc, remaining),
            new WebsitePolicy(shouldBlock, reason, source, rules),
            new DesktopProtection(JsonNamingPolicyName(access.State), access.WebsiteProtectionActive, access.ExpiresAtUtc, protectionError),
            EffectiveTheme: FocusThemeResolver.Resolve(data.Settings?.FocusTheme, now.ToLocalTime()));
    }

    private static string JsonNamingPolicyName(AccessControlRuntimeState state) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}
