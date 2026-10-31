import { PolicyController } from './controller.js';
import { validateSnapshot } from './policy.js';

const HOST = 'com.focusapp.browser_bridge';
const controller = new PolicyController(chrome);
let serial = Promise.resolve();
let port = null;
let retry = 0;
let retryTimer;
let expiryTimer;
let stopped = false;
let nextId = 0;
const pending = new Map();
const retiredEpochs = new Set();
let lastWall = Date.now();
let lastMonotonic = performance.now();

function enqueue(work) {
  const result = serial.then(async () => {
    const wall = Date.now();
    const monotonic = performance.now();
    if (Math.abs((wall - lastWall) - (monotonic - lastMonotonic)) > 5000) {
      controller.deadline = 0;
      await controller.sweep();
    }
    lastWall = wall;
    lastMonotonic = monotonic;
    return work();
  });
  serial = result.catch(e => console.error('FocusApp', e.message));
  return result;
}

async function scheduleExpiry() {
  clearTimeout(expiryTimer);
  if (controller.deadline > Date.now()) {
    const when = controller.deadline;
    await chrome.alarms.create('focus-expiry', { when });
    expiryTimer = setTimeout(() => enqueue(async () => {
      await controller.sweep();
      if (port) refresh().catch(fail);
    }), Math.max(1, when - Date.now()));
  }
}

async function fail(error) {
  const message = error?.message ?? String(error);
  if (message.includes('PROTOCOL_UNSUPPORTED')) stopped = true;
  const oldPort = port;
  port = null;
  oldPort?.disconnect();
  for (const p of pending.values()) { clearTimeout(p.timer); p.reject(new Error(message)); }
  pending.clear();
  await enqueue(async () => { await controller.disconnect(message); await scheduleExpiry(); });
  scheduleReconnect();
}

function scheduleReconnect() {
  if (stopped || retryTimer) return;
  const delay = Math.min(15000, 1000 * 2 ** Math.min(retry++, 4)) + Math.random() * 250;
  retryTimer = setTimeout(() => { retryTimer = null; connect(); }, delay);
  // Alarm is a second trigger if the worker is suspended before the timer runs.
  chrome.alarms.create('focus-reconnect', { when: Date.now() + delay }).catch(console.error);
}

function request(method, timeout = 22000) {
  return new Promise((resolve, reject) => {
    if (!port) { reject(new Error('HOST_DISCONNECTED')); return; }
    const id = `r${++nextId}`;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('TIMEOUT: 本地通信超时')); }, timeout);
    pending.set(id, { resolve, reject, timer });
    try { port.postMessage({ version: 1, type: 'request', id, method, params: {} }); }
    catch (e) { clearTimeout(timer); pending.delete(id); reject(e); }
  });
}

async function accept(s) {
  if (retiredEpochs.has(s?.epoch)) return;
  let gap = false;
  await enqueue(async () => {
    if (retiredEpochs.has(s?.epoch)) return;
    validateSnapshot(s);
    const previous = controller.snapshot;
    if (previous?.epoch === s.epoch && previous.serviceInstanceId !== s.serviceInstanceId)
      throw new Error('SNAPSHOT_INCONSISTENT: Service 实例变化但 epoch 未更新');
    gap = previous?.epoch === s.epoch && s.sequence > previous.sequence + 1;
    // Ignore stale initial responses. Only current port messages reach this function.
    if (previous && previous.epoch !== s?.epoch) retiredEpochs.add(previous.epoch);
    const changed = await controller.accept(s);
    if (changed) { retry = 0; await scheduleExpiry(); }
  });
  // All events are full snapshots; additionally request calibration after a detected sequence gap.
  if (gap && port) refresh().catch(fail);
}

async function refresh() {
  const connection = port;
  try { await accept(await request('snapshot.get')); }
  catch (error) { if (port === connection) throw error; }
}

function connect() {
  if (port || stopped) return;
  clearTimeout(retryTimer);
  retryTimer = null;
  const p = chrome.runtime.connectNative(HOST);
  port = p;
  p.onMessage.addListener(message => {
    if (port !== p) return;
    if (message?.version !== 1) { fail(new Error('PROTOCOL_UNSUPPORTED: Host 协议版本不兼容')); return; }
    if (message.type === 'response') {
      const req = pending.get(message.id);
      if (!req) return;
      clearTimeout(req.timer);
      pending.delete(message.id);
      if (message.error) req.reject(new Error(`${message.error.code}: ${message.error.message}`));
      else req.resolve(message.result);
    } else if (message.type === 'event' && message.event === 'snapshot.changed') {
      accept(message.data).catch(fail);
    } else if (message.type === 'event' && message.event === 'bridge.error') {
      fail(new Error(`${message.data?.code}: ${message.data?.message}`));
    }
  });
  p.onDisconnect.addListener(() => {
    const message = chrome.runtime.lastError?.message ?? 'Native Host 已断开';
    if (port === p) fail(new Error(message));
  });
  (async () => {
    const hello = await request('hello', 6000);
    if (hello?.protocolVersion !== 1) throw new Error('PROTOCOL_UNSUPPORTED: hello 失败');
    await accept(await request('snapshot.subscribe'));
  })().catch(error => { if (port === p) fail(error); });
}

// Only these two extension pages may ask for UI state. No raw snapshots or forwarding API.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  let url;
  try { url = new URL(sender.url); } catch { return false; }
  const base = chrome.runtime.getURL('');
  const page = `${url.protocol}//${url.host}${url.pathname}`;
  const popup = page === `${base}popup.html`;
  const blocked = page === `${base}blocked.html`;
  if (sender.id !== chrome.runtime.id || (!popup && !blocked)) return false;
  if (message?.type === 'status.get' && popup) {
    enqueue(async () => { await controller.sweep(); return controller.status(); }).then(sendResponse);
  } else if (message?.type === 'blocked.get' && blocked && typeof message.host === 'string') {
    enqueue(async () => { await controller.sweep(); return controller.status(message.host); }).then(sendResponse);
  } else if (message?.type === 'reconnect' && popup) {
    stopped = false;
    retry = 0;
    if (port) refresh().catch(fail); else connect();
    sendResponse({ ok: true });
    return false;
  } else return false;
  return true;
});

chrome.alarms.onAlarm.addListener(alarm => {
  if (!alarm.name.startsWith('focus-')) return;
  enqueue(async () => { await controller.sweep(); await scheduleExpiry(); })
    .then(() => { if (!port) connect(); else if (alarm.name === 'focus-watchdog') refresh().catch(fail); });
});
chrome.runtime.onStartup.addListener(() => { if (!port) connect(); });
chrome.runtime.onInstalled.addListener(() => { if (!port) connect(); });

enqueue(async () => {
  await chrome.storage.session.setAccessLevel({ accessLevel: 'TRUSTED_CONTEXTS' });
  await controller.initialize();
  await chrome.alarms.create('focus-watchdog', { periodInMinutes: 0.5 });
  await scheduleExpiry();
}).then(connect).catch(fail);
