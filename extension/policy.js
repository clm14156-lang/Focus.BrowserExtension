export const RULE_ID = 10001;
export const MAX_DOMAINS = 1000;
export const DISCONNECT_GRACE_MS = 10000;

export function validDomain(value) {
  return typeof value === 'string' && value.length <= 253 && value === value.toLowerCase()
    && value.includes('.') && !/^\d+(\.\d+){3}$/.test(value)
    && value.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label));
}

function requireValue(condition, message) {
  if (!condition) throw new Error(`INVALID_SNAPSHOT: ${message}`);
}

export function validateSnapshot(s, now = Date.now()) {
  requireValue(s && typeof s === 'object', '缺少快照');
  requireValue(typeof s.epoch === 'string' && s.epoch.length > 0 && s.epoch.length <= 100, 'epoch');
  requireValue(Number.isSafeInteger(s.sequence) && s.sequence > 0, 'sequence');
  requireValue(typeof s.serviceInstanceId === 'string' && s.serviceInstanceId.length > 0, 'Service ID');
  const observed = Date.parse(s.observedAtUtc);
  const lease = Date.parse(s.leaseExpiresAtUtc);
  requireValue(Number.isFinite(observed) && Number.isFinite(lease) && lease > now
    && observed <= now + 5000 && lease > observed && lease - observed <= 31000, '租期/本机时钟');
  requireValue(s.consistency === 'sampled', 'consistency');
  const f = s.focus;
  requireValue(f && ['idle', 'preparing', 'focusing', 'completing', 'unknown'].includes(f.state), 'focus.state');
  requireValue(f.remainingSeconds === null || (Number.isSafeInteger(f.remainingSeconds) && f.remainingSeconds >= 0), '剩余时间');
  requireValue(Number.isSafeInteger(f.configuredSeconds) && f.configuredSeconds >= 0, '配置时长');
  requireValue(f.remainingSeconds === null || f.remainingSeconds <= f.configuredSeconds, '剩余时间上限');
  requireValue(f.remainingAccuracy === 'estimatedUtc', '时间精度');
  requireValue(f.state !== 'focusing' || Number.isFinite(Date.parse(f.plannedEndAtUtc)), '会话期限');
  const p = s.websitePolicy;
  requireValue(p && p.enforcementMode === 'mirrorReportedProtection' && p.scope === 'mainFrame', '策略模式');
  requireValue(typeof p.shouldBlock === 'boolean' && ['current', 'sessionSnapshot'].includes(p.ruleSource), '策略字段');
  requireValue(Array.isArray(p.rules) && p.rules.length <= MAX_DOMAINS, '域名数量');
  const domains = new Set();
  for (const rule of p.rules) {
    requireValue(rule && typeof rule.id === 'string' && rule.id.length <= 100 && validDomain(rule.domain)
      && rule.includeSubdomains === true && typeof rule.name === 'string' && rule.name.length <= 200, '域名/名称');
    requireValue(!domains.has(rule.domain), '重复域名');
    domains.add(rule.domain);
  }
  const d = s.desktopProtection;
  requireValue(d && typeof d.websiteProtectionActive === 'boolean', '保护状态');
  if (p.shouldBlock) {
    requireValue(p.rules.length > 0 && !['idle', 'preparing', 'completing'].includes(f.state), '状态与策略矛盾');
    // A valid sample can cross its protection/session deadline while in transit.
    // Validate what the host observed; the controller enforces the deadline again
    // at application time and must never install already-expired blocking rules.
    requireValue(d.websiteProtectionActive && ['active', 'partiallyActive'].includes(d.state)
      && Date.parse(d.expiresAtUtc) > observed, '保护期限');
    requireValue(f.state !== 'focusing' || (f.remainingSeconds > 0 && Date.parse(f.plannedEndAtUtc) > observed), '会话过期');
  }
  return s;
}

export function policyDeadline(s) {
  let end = Date.parse(s.leaseExpiresAtUtc);
  if (s.websitePolicy.shouldBlock) {
    end = Math.min(end, Date.parse(s.desktopProtection.expiresAtUtc));
    if (s.focus.state === 'focusing') end = Math.min(end, Date.parse(s.focus.plannedEndAtUtc));
  }
  return end;
}

// One aggregate rule captures only the host, never the URL path, query, or credentials.
export function redirectRules(s, extensionBase) {
  if (!s.websitePolicy.shouldBlock) return [];
  return [{
    id: RULE_ID,
    priority: 1,
    action: { type: 'redirect', redirect: {
      regexSubstitution: `${extensionBase}blocked.html#host=\\1`
    } },
    condition: {
      regexFilter: '^https?://(?:[^/@]*@)?([^/:?#]+)(?::[0-9]+)?(?:[/?#].*)?$',
      requestDomains: s.websitePolicy.rules.map(r => r.domain).sort(),
      resourceTypes: ['main_frame'],
      isUrlFilterCaseSensitive: false
    }
  }];
}

export function matchRule(rules, host) {
  if (!validDomain(host)) return null;
  return rules.filter(r => host === r.domain || host.endsWith(`.${r.domain}`))
    .sort((a, b) => b.domain.length - a.domain.length || a.id.localeCompare(b.id))[0] ?? null;
}

export function remainingAt(s, now = Date.now()) {
  if (!s) return null;
  if (s.focus.state === 'idle') return 0;
  if (s.focus.state === 'preparing') return s.focus.configuredSeconds;
  if (s.focus.remainingSeconds === null) return null;
  return Math.max(0, Math.min(s.focus.remainingSeconds,
    s.focus.remainingSeconds - Math.floor(Math.max(0, now - Date.parse(s.observedAtUtc)) / 1000)));
}
