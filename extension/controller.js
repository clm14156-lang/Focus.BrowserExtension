import { validateSnapshot, policyDeadline, redirectRules, RULE_ID, matchRule, remainingAt, DISCONNECT_GRACE_MS, validDomain } from './policy.js';

// Callers serialize all mutations. API application succeeds before applied metadata changes.
export class PolicyController {
  constructor(api, now = Date.now) {
    this.api = api;
    this.now = now;
    this.snapshot = null;
    this.appliedSnapshot = null;
    this.deadline = 0;
    this.connected = false;
    this.error = null;
    this.applyError = null;
    this.ruleCount = 0;
  }

  async initialize() {
    const { policyCache } = await this.api.storage.session.get('policyCache');
    const rules = await this.api.declarativeNetRequest.getSessionRules();
    this.ruleCount = rules.some(r => r.id === RULE_ID) ? 1 : 0;
    // A new worker must confirm liveness. Recovery cannot extend a prior disconnect deadline.
    if (policyCache?.snapshot) {
      try {
        this.snapshot = validateSnapshot(policyCache.snapshot, this.now());
        this.appliedSnapshot = policyCache.appliedSnapshot ?? null;
        this.deadline = Math.min(policyCache.deadline, policyDeadline(this.snapshot), this.now() + DISCONNECT_GRACE_MS);
      } catch { this.snapshot = null; this.deadline = 0; }
    }
    await this.sweep();
    await this.persist();
  }

  async accept(snapshot) {
    const s = validateSnapshot(snapshot, this.now());
    if (this.snapshot?.epoch === s.epoch && s.sequence <= this.snapshot.sequence) return false;
    this.snapshot = s;
    this.connected = true;
    this.error = null;
    this.deadline = policyDeadline(s);
    if (this.now() >= this.deadline) {
      await this.sweep();
      return true;
    }
    try {
      await this.api.declarativeNetRequest.updateSessionRules({ removeRuleIds: [RULE_ID], addRules: redirectRules(s, this.api.runtime.getURL('')) });
      this.appliedSnapshot = s;
      this.ruleCount = s.websitePolicy.shouldBlock ? 1 : 0;
      this.applyError = null;
    } catch (e) {
      this.applyError = `DNR_APPLY_FAILED: ${e.message}`;
      // Do not grant a failed replacement a new lease for the old installed rules.
      this.deadline = Math.min(this.deadline, this.appliedSnapshot ? policyDeadline(this.appliedSnapshot) : this.now(), this.now() + DISCONNECT_GRACE_MS);
    }
    // The DNR API is asynchronous: a deadline may also pass while it is applying.
    if (this.now() >= this.deadline) await this.sweep();
    else await this.persist();
    return true;
  }

  async disconnect(message) {
    this.connected = false;
    this.error = message;
    this.deadline = Math.min(this.deadline, this.now() + DISCONNECT_GRACE_MS);
    await this.sweep();
    await this.persist();
  }

  async sweep() {
    if (this.snapshot && this.now() >= this.deadline) {
      this.connected = false;
      this.error ??= 'SNAPSHOT_EXPIRED: 等待重新同步';
    }
    if (this.now() < this.deadline) return;
    try {
      await this.api.declarativeNetRequest.updateSessionRules({ removeRuleIds: [RULE_ID] });
      this.ruleCount = 0;
      this.appliedSnapshot = null;
      this.applyError = null;
    } catch (e) { this.applyError = `DNR_CLEAR_FAILED: ${e.message}`; }
    await this.persist();
  }

  async persist() {
    await this.api.storage.session.set({ policyCache: {
      snapshot: this.snapshot, appliedSnapshot: this.appliedSnapshot, deadline: this.deadline
    } });
  }

  status(host = null) {
    const s = this.snapshot;
    const active = this.ruleCount > 0 && this.now() < this.deadline;
    const rule = host && this.appliedSnapshot ? matchRule(this.appliedSnapshot.websitePolicy.rules, host) : null;
    const common = {
      connected: this.connected, active: active && !this.applyError,
      remainingSeconds: remainingAt(s, this.now()), configuredSeconds: s?.focus.configuredSeconds ?? 0,
      focusState: s?.focus.state ?? 'unknown', remainingAccuracy: 'estimatedUtc',
      error: this.error, applyError: this.applyError, ruleCount: this.ruleCount,
      desktopState: s?.desktopProtection.state ?? 'unknown',
      protectionError: s?.desktopProtection.errorCode ?? null,
      sequence: s?.sequence ?? null, rulesAppliedSequence: this.appliedSnapshot?.sequence ?? null,
      observedAtUtc: s?.observedAtUtc ?? null, source: s?.source ?? 'service',
      effectiveTheme: s?.effectiveTheme === 'light' || s?.effectiveTheme === 'dark' ? s.effectiveTheme : null
    };
    if (host !== null) {
      // A locally elapsed countdown or fail-open cleanup is not proof that focus ended.
      // Only authorize navigation after a fresh idle snapshot was successfully applied,
      // and desktop website protection has also stopped (including proxy teardown).
      const canAutoReturn = validDomain(host) && this.connected && !this.error && !this.applyError
        && s?.focus.state === 'idle' && s.focus.remainingSeconds === 0
        && !s.websitePolicy.shouldBlock && this.ruleCount === 0
        && this.now() < this.deadline
        && this.appliedSnapshot?.epoch === s.epoch && this.appliedSnapshot.sequence === s.sequence
        && s.desktopProtection.state === 'inactive' && !s.desktopProtection.websiteProtectionActive;
      return { ...common, blocked: this.ruleCount > 0 && !!rule, canAutoReturn,
        site: rule ? { name: rule.name, domain: rule.domain } : null };
    }
    return { ...common, domainCount: s?.websitePolicy.rules.length ?? 0 };
  }
}
