using System.Net;

namespace FocusApp.Core;

/// <summary>
/// Evaluates in-memory access rules. It deliberately has no process, network,
/// proxy, persistence, or notification responsibilities.
/// </summary>
public sealed class AccessControlService
{
    public BlockedAccessResult Evaluate(
        AccessRequest request,
        IEnumerable<WebsiteAccessRule> websites,
        IEnumerable<ApplicationAccessRule> applications)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Type switch
        {
            AccessRequestType.Website => EvaluateWebsite(request.Value, websites),
            AccessRequestType.Application => EvaluateApplication(request.Value, applications),
            _ => BlockedAccessResult.Allowed()
        };
    }

    public BlockedAccessResult EvaluateWebsite(string address, IEnumerable<WebsiteAccessRule> rules)
    {
        var host = NormalizeWebsiteHost(address);
        if (host is null)
        {
            return BlockedAccessResult.Allowed();
        }

        var match = (rules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => new { Rule = rule, Host = NormalizeWebsiteHost(rule.Address) })
            .Where(item => item.Host is not null && IsWebsiteMatch(host, item.Host!))
            .OrderByDescending(item => item.Host!.Length)
            .ThenBy(item => item.Rule.Id)
            .FirstOrDefault();

        return match is null
            ? BlockedAccessResult.Allowed()
            : BlockedAccessResult.Blocked(
                AccessRuleType.Website,
                match.Rule.Id,
                match.Rule.Name,
                host);
    }

    public BlockedAccessResult EvaluateApplication(string path, IEnumerable<ApplicationAccessRule> rules)
    {
        var normalizedPath = NormalizeApplicationPath(path);
        if (normalizedPath.Length == 0)
        {
            return BlockedAccessResult.Allowed();
        }

        var match = (rules ?? [])
            .Where(rule => rule.IsEnabled)
            .Select(rule => new { Rule = rule, Path = NormalizeApplicationPath(rule.Path) })
            .Where(item => item.Path.Length > 0 && string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Rule.Id)
            .FirstOrDefault();

        return match is null
            ? BlockedAccessResult.Allowed()
            : BlockedAccessResult.Blocked(
                AccessRuleType.Application,
                match.Rule.Id,
                match.Rule.Name,
                normalizedPath);
    }

    public static string? NormalizeWebsiteHost(string? address)
    {
        var host = NormalizeWebsiteInput(address);
        return host is not null && IsValidDomain(host) ? host : null;
    }

    public static string? NormalizeWebsiteInput(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var value = address.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4
            ? host[4..]
            : host;
    }

    public static bool IsValidDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 253 ||
            host[0] == '.' ||
            host[^1] == '.' ||
            host.Any(char.IsWhiteSpace) ||
            Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return false;
        }

        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 ||
                label[0] == '-' ||
                label[^1] == '-' ||
                label.Any(character =>
                    character != '-' &&
                    (character < 'a' || character > 'z') &&
                    (character < 'A' || character > 'Z') &&
                    (character < '0' || character > '9')))
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizeApplicationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('/', '\\');
        while (normalized.Length > 3 && normalized.EndsWith('\\'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static bool IsWebsiteMatch(string requestedHost, string ruleHost)
    {
        if (string.Equals(requestedHost, ruleHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(requestedHost, out _) || IPAddress.TryParse(ruleHost, out _))
        {
            return false;
        }

        return requestedHost.EndsWith($".{ruleHost}", StringComparison.OrdinalIgnoreCase);
    }
}
