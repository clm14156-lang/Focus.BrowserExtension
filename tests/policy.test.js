import test from 'node:test';
import assert from 'node:assert/strict';
import { validateSnapshot, redirectRules, matchRule, remainingAt, RULE_ID } from '../extension/policy.js';
import { PolicyController } from '../extension/controller.js';

const now = Date.parse('2026-10-17T08:00:00Z');
function snapshot(sequence = 1) {
  return {
    epoch: 'epoch-a', sequence, serviceInstanceId: 'svc', sourceRevision: '1',
    observedAtUtc: new Date(now).toISOString(), leaseExpiresAtUtc: new Date(now + 30000).toISOString(), consistency: 'sampled',
    focus: { state: 'focusing', configuredSeconds: 60, plannedEndAtUtc: new Date(now + 60000).toISOString(), remainingSeconds: 60, remainingAccuracy: 'estimatedUtc' },
    websitePolicy: { enforcementMode: 'mirrorReportedProtection', shouldBlock: true, ruleSource: 'current', scope: 'mainFrame',
      rules: [{ id: 'a', name: '示例', domain: 'example.com', includeSubdomains: true }] },
    desktopProtection: { state: 'active', websiteProtectionActive: true, expiresAtUtc: new Date(now + 60000).toISOString() }
  };
}

function fakeApi() {
  let stored = {};
  const api = {
    runtime: { getURL: path => `chrome-extension://${'a'.repeat(32)}/${path}` },
    storage: { session: { async get() { return structuredClone(stored); }, async set(value) { stored = structuredClone(value); } } },
    rules: [], calls: [], reject: false,
    declarativeNetRequest: {
      async getSessionRules() { return structuredClone(api.rules); },
      async updateSessionRules(change) {
        if (api.reject) throw new Error('API denied');
        api.calls.push(change);
        api.rules = api.rules.filter(r => !change.removeRuleIds?.includes(r.id)).concat(change.addRules ?? []);
      }
    }
  };
  return api;
}

test('aggregate DNR redirect is main_frame only and discards path, query, credentials', () => {
  const [r] = redirectRules(validateSnapshot(snapshot(), now), `chrome-extension://${'a'.repeat(32)}/`);
  assert.equal(r.id, RULE_ID);
  assert.deepEqual(r.condition.resourceTypes, ['main_frame']);
  const re = new RegExp(r.condition.regexFilter, 'i');
  for (const url of ['https://sub.example.com/private?q=secret#x', 'http://example.com:8080/path', 'https://user:password@example.com/a']) {
    const match = re.exec(url);
    assert.ok(match);
    const redirected = r.action.redirect.regexSubstitution.replace('\\1', match[1]);
    assert.match(redirected, /blocked\.html#host=(sub\.)?example\.com$/);
    assert.ok(!redirected.includes('secret') && !redirected.includes('password'));
  }
  assert.equal(re.test('file:///example.com'), false);
});

test('domain boundary and most specific name match', () => {
  const rules = [...snapshot().websitePolicy.rules, { id: 'b', name: '子站', domain: 'a.example.com' }];
  assert.equal(matchRule(rules, 'x.a.example.com').name, '子站');
  assert.equal(matchRule(rules, 'example.com').name, '示例');
  assert.equal(matchRule(rules, 'notexample.com'), null);
  assert.equal(matchRule(rules, 'example.com.evil.test'), null);
  assert.equal(matchRule(rules, 'example.com.'), null);
});

test('invalid, expired, empty, duplicate and oversized blocking snapshots are rejected', () => {
  const changes = [
    s => { s.leaseExpiresAtUtc = new Date(now - 1).toISOString(); },
    s => { s.websitePolicy.rules = []; },
    s => { s.websitePolicy.rules[0].domain = 'localhost'; },
    s => { s.websitePolicy.rules.push(s.websitePolicy.rules[0]); },
    s => { s.websitePolicy.rules = Array(1001).fill(s.websitePolicy.rules[0]); },
    s => { s.focus.state = 'preparing'; },
    s => { s.desktopProtection.state = 'faulted'; },
    s => { s.focus.remainingSeconds = null; },
    s => { s.focus.remainingSeconds = 61; }
  ];
  for (const mutate of changes) { const s = snapshot(); mutate(s); assert.throws(() => validateSnapshot(s, now)); }
});

test('ended focus removes only own session rule and old sequence cannot reactivate it', async () => {
  const api = fakeApi();
  api.rules.push({ id: 999 });
  const c = new PolicyController(api, () => now);
  await c.initialize();
  await c.accept(snapshot());
  assert.equal(c.status().active, true);
  const end = snapshot(2);
  end.focus.state = 'idle'; end.focus.remainingSeconds = 0; end.websitePolicy.shouldBlock = false;
  await c.accept(end);
  assert.deepEqual(api.rules, [{ id: 999 }]);
  assert.equal(await c.accept(snapshot()), false);
  assert.equal(c.status().active, false);
});

test('disconnect grace survives worker recreation and cannot be extended', async () => {
  let time = now;
  const api = fakeApi();
  let c = new PolicyController(api, () => time);
  await c.initialize(); await c.accept(snapshot());
  await c.disconnect('lost');
  assert.equal(c.deadline, now + 10000);
  time += 5000;
  c = new PolicyController(api, () => time);
  await c.initialize();
  assert.equal(c.deadline, now + 10000);
  time += 5001; await c.sweep();
  assert.equal(api.rules.length, 0);
  assert.equal(c.status().connected, false);
});

test('clear at session deadline or 30s lease even with native connection still open', async () => {
  for (const endOffset of [3000, 60000]) {
    let time = now;
    const api = fakeApi(); const c = new PolicyController(api, () => time);
    await c.initialize();
    const s = snapshot(); s.focus.plannedEndAtUtc = new Date(now + endOffset).toISOString();
    await c.accept(s);
    time += Math.min(endOffset, 30000) + 1; await c.sweep();
    assert.equal(api.rules.length, 0);
  }
});

test('protection or focus expiring in transit is cleared without installing expired rules or authorizing return', async () => {
  for (const boundary of ['protection', 'focus']) {
    let time = now;
    const api = fakeApi();
    api.rules.push({ id: 999 });
    const c = new PolicyController(api, () => time);
    await c.initialize();
    await c.accept(snapshot());
    const delayed = snapshot(2);
    if (boundary === 'protection') delayed.desktopProtection.expiresAtUtc = new Date(now + 500).toISOString();
    else delayed.focus.plannedEndAtUtc = new Date(now + 500).toISOString();
    time += 501;
    assert.doesNotThrow(() => validateSnapshot(delayed, time));
    const callsBefore = api.calls.length;
    await c.accept(delayed);
    assert.deepEqual(api.rules, [{ id: 999 }]);
    assert.ok(api.calls.slice(callsBefore).every(call => !call.addRules?.length), 'expired snapshots only clear rules');
    assert.equal(c.status('example.com').blocked, false);
    assert.equal(c.status('example.com').canAutoReturn, false);
    assert.match(c.status().error, /SNAPSHOT_EXPIRED/);
    await c.accept(snapshot(3));
    assert.equal(c.status('example.com').blocked, true, 'a fresh valid snapshot restores blocking');
    assert.equal(c.status().error, null);
  }
});

test('protection or session already invalid when sampled is still rejected', () => {
  for (const field of ['protection', 'focus']) {
    const broken = snapshot();
    if (field === 'protection') broken.desktopProtection.expiresAtUtc = new Date(now).toISOString();
    else broken.focus.plannedEndAtUtc = new Date(now).toISOString();
    assert.throws(() => validateSnapshot(broken, now + 1), /保护期限|会话过期/);
  }
});

test('a deadline passing during DNR application is cleared immediately even if cleanup needs a retry', async () => {
  for (const cleanupFails of [false, true]) {
    let time = now;
    const api = fakeApi();
    const apply = api.declarativeNetRequest.updateSessionRules;
    api.declarativeNetRequest.updateSessionRules = async change => {
      await apply(change);
      if (change.addRules?.length) { time += 501; api.reject = cleanupFails; }
    };
    const c = new PolicyController(api, () => time);
    const ending = snapshot();
    ending.desktopProtection.expiresAtUtc = new Date(now + 500).toISOString();
    await c.accept(ending);
    assert.equal(c.status().active, false);
    assert.equal(c.status('example.com').canAutoReturn, false);
    if (cleanupFails) {
      assert.match(c.status().applyError, /DNR_CLEAR_FAILED/);
      assert.equal(api.rules.length, 1);
      api.reject = false;
      await c.sweep();
    }
    assert.equal(api.rules.length, 0);
  }
});

test('DNR failure records actual prior application and cleanup retries', async () => {
  let time = now;
  const api = fakeApi(); const c = new PolicyController(api, () => time);
  await c.initialize(); await c.accept(snapshot());
  api.reject = true;
  await c.accept(snapshot(2));
  assert.equal(c.status().rulesAppliedSequence, 1);
  assert.equal(c.status().active, false);
  assert.match(c.status().applyError, /APPLY_FAILED/);
  time += 30001; await c.sweep();
  assert.equal(api.rules.length, 1);
  assert.match(c.status().applyError, /CLEAR_FAILED/);
  api.reject = false; await c.sweep();
  assert.equal(api.rules.length, 0);
});

test('browser restart without cache clears any stale own rules', async () => {
  const api = fakeApi(); api.rules = [{ id: RULE_ID }];
  const c = new PolicyController(api, () => now);
  await c.initialize();
  assert.equal(api.rules.length, 0);
  assert.equal(c.status().focusState, 'unknown');
});

test('blocked page gets only selected site, no complete domain list', async () => {
  const api = fakeApi(); const c = new PolicyController(api, () => now);
  await c.accept(snapshot());
  assert.deepEqual(c.status('sub.example.com').site, { name: '示例', domain: 'example.com' });
  assert.equal(c.status('evil.test').site, null);
  assert.equal('websitePolicy' in c.status('sub.example.com'), false);
  assert.equal('domainCount' in c.status('sub.example.com'), false);
});

test('estimated remaining and unknown stay bounded', () => {
  assert.equal(remainingAt(snapshot(), now + 2500), 58);
  assert.equal(remainingAt(snapshot(), now + 90000), 0);
  const s = snapshot(); s.focus.remainingSeconds = null;
  assert.equal(remainingAt(s, now), null);
});

function endedSnapshot(sequence = 2) {
  const end = snapshot(sequence);
  end.focus.state = 'idle'; end.focus.remainingSeconds = 0;
  end.websitePolicy.shouldBlock = false;
  end.desktopProtection.state = 'inactive'; end.desktopProtection.websiteProtectionActive = false;
  return end;
}

test('effective theme follows accepted app snapshots and survives reconnect cache without changing blocking', async () => {
  const api = fakeApi();
  let c = new PolicyController(api, () => now);
  for (const [index, theme] of ['light', 'dark', 'light'].entries()) {
    await c.accept({ ...snapshot(index + 1), effectiveTheme: theme });
    assert.equal(c.status('example.com').effectiveTheme, theme);
    assert.equal(c.status('example.com').blocked, true);
    assert.equal(c.status('example.com').remainingSeconds, 60);
    assert.equal(api.rules.length, 1);
  }
  c = new PolicyController(api, () => now);
  await c.initialize();
  assert.equal(c.status('example.com').effectiveTheme, 'light');
  await c.disconnect('fixture');
  assert.equal(c.status('example.com').effectiveTheme, 'light');
});

test('missing or unknown theme from older hosts does not invent an effective theme or disable blocking', async () => {
  const c = new PolicyController(fakeApi(), () => now);
  for (const [index, theme] of [undefined, null, 'System', 'invalid'].entries()) {
    await c.accept({ ...snapshot(index + 1), effectiveTheme: theme });
    assert.equal(c.status('example.com').effectiveTheme, null);
    assert.equal(c.status('example.com').blocked, true);
  }
});

test('auto return is authorized only after idle snapshot and rule removal have succeeded', async () => {
  const api = fakeApi(); const c = new PolicyController(api, () => now);
  await c.accept(snapshot());
  assert.equal(c.status('sub.example.com').canAutoReturn, false);
  await c.accept(endedSnapshot());
  assert.equal(api.rules.length, 0);
  assert.equal(c.status('sub.example.com').canAutoReturn, true);
  // A stale runtime diagnostic must not override the confirmed idle/inactive state.
  const withRuntimeWarning = endedSnapshot(3);
  withRuntimeWarning.desktopProtection.errorCode = 'FOCUS_RUNTIME_FAULTED';
  await c.accept(withRuntimeWarning);
  assert.equal(c.status('sub.example.com').canAutoReturn, true);
  assert.equal(c.status('javascript:alert(1)').canAutoReturn, false);
  assert.equal(c.status('localhost').canAutoReturn, false);
  assert.equal(c.status('127.0.0.1').canAutoReturn, false);
});

test('auto return waits for desktop teardown, and does not trigger for a removed domain mid-focus', async () => {
  const api = fakeApi(); const c = new PolicyController(api, () => now);
  for (const [state, websiteActive] of [['active', true], ['deactivating', false], ['active', false]]) {
    const end = endedSnapshot((c.snapshot?.sequence ?? 0) + 1);
    end.desktopProtection.state = state; end.desktopProtection.websiteProtectionActive = websiteActive;
    await c.accept(end);
    assert.equal(c.status('example.com').canAutoReturn, false);
  }
  const removed = snapshot(4); removed.websitePolicy.shouldBlock = false;
  removed.websitePolicy.rules = [];
  await c.accept(removed);
  assert.equal(c.status('example.com').canAutoReturn, false);
});

test('disconnection, local expiry, cached idle and failed DNR clearing never authorize auto return', async () => {
  let time = now;
  const api = fakeApi(); let c = new PolicyController(api, () => time);
  await c.accept(snapshot());
  api.reject = true; await c.accept(endedSnapshot());
  assert.equal(c.status('example.com').canAutoReturn, false);
  api.reject = false; await c.accept(endedSnapshot(3));
  assert.equal(c.status('example.com').canAutoReturn, true);
  c = new PolicyController(api, () => time); await c.initialize();
  assert.equal(c.status('example.com').canAutoReturn, false);
  await c.accept(endedSnapshot(4)); await c.disconnect('lost');
  time += 10001; await c.sweep();
  assert.equal(c.status('example.com').canAutoReturn, false);
  time = now;
  const fresh = new PolicyController(fakeApi(), () => time);
  await fresh.accept(snapshot());
  time += 61000; await fresh.sweep();
  assert.equal(fresh.status('example.com').remainingSeconds, 0);
  assert.equal(fresh.status('example.com').canAutoReturn, false);
});
