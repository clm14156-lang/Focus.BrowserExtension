namespace FocusApp.Core;

public enum AccessRuleType
{
    Website,
    Application
}

public enum AccessRequestType
{
    Website,
    Application
}

public sealed record WebsiteAccessRule(Guid Id, string Name, string Address, bool IsEnabled = true);

public sealed record ApplicationAccessRule(Guid Id, string Name, string Path, bool IsEnabled = true);

public sealed record AccessRequest(AccessRequestType Type, string Value)
{
    public static AccessRequest Website(string address) => new(AccessRequestType.Website, address);

    public static AccessRequest Application(string path) => new(AccessRequestType.Application, path);
}

public sealed record BlockedAccessResult(
    bool IsBlocked,
    AccessRuleType? RuleType = null,
    Guid? RuleId = null,
    string? RuleName = null,
    string? MatchedValue = null)
{
    public bool IsAllowed => !IsBlocked;

    public static BlockedAccessResult Allowed() => new(false);

    public static BlockedAccessResult Blocked(
        AccessRuleType ruleType,
        Guid ruleId,
        string ruleName,
        string matchedValue)
        => new(true, ruleType, ruleId, ruleName, matchedValue);
}
