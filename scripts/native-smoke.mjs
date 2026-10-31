// Real published Host process / stdio framing test. No registry or production Service changes.
import { cp, mkdir, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = path.join(root, 'artifacts', 'native-smoke');
await mkdir(output, { recursive: true });
await cp(path.join(root, 'artifacts', 'demo-host'), output, { recursive: true });
const origin = `chrome-extension://${'a'.repeat(32)}/`;
await writeFile(path.join(output, 'com.focusapp.browser_bridge.json'), JSON.stringify({
  name: 'com.focusapp.browser_bridge', description: 'Native test fixture', type: 'stdio',
  path: path.join(output, 'FocusApp.BrowserBridge.exe'), allowed_origins: [origin]
}));
const executable = path.join(output, 'FocusApp.BrowserBridge.exe');
const child = spawn(executable, [origin, '--parent-window=0'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
let buffer = Buffer.alloc(0);
let stderr = '';
const responses = new Map();
let resolveEvent;
const event = new Promise(resolve => { resolveEvent = resolve; });
child.stderr.on('data', data => { stderr += data; });
child.stdout.on('data', data => {
  buffer = Buffer.concat([buffer, data]);
  while (buffer.length >= 4 && buffer.length >= buffer.readUInt32LE(0) + 4) {
    const size = buffer.readUInt32LE(0);
    assert.ok(size > 0 && size <= 256 * 1024);
    const message = JSON.parse(buffer.subarray(4, 4 + size).toString('utf8'));
    buffer = buffer.subarray(4 + size);
    if (message.type === 'response') { responses.get(message.id)?.(message); responses.delete(message.id); }
    if (message.type === 'event') resolveEvent(message);
  }
});
let id = 0;
function send(method) {
  return new Promise((resolve, reject) => {
    const requestId = String(++id);
    const timer = setTimeout(() => reject(new Error(`Host response timeout; stderr=${stderr}`)), 5000);
    responses.set(requestId, response => { clearTimeout(timer); resolve(response); });
    const body = Buffer.from(JSON.stringify({ version: 1, type: 'request', id: requestId, method, params: {} }));
    const header = Buffer.alloc(4); header.writeUInt32LE(body.length);
    child.stdin.write(Buffer.concat([header, body]));
  });
}
try {
  assert.equal((await send('hello')).result.protocolVersion, 1);
  const first = (await send('snapshot.subscribe')).result;
  assert.equal(first.source, 'demo');
  assert.equal(first.websitePolicy.rules[0].domain, 'example.com');
  assert.equal((await send('focus.forced.start')).error.code, 'METHOD_NOT_ALLOWED');
  const changed = await Promise.race([event, new Promise((_, reject) => setTimeout(() => reject(new Error('No push event')), 5000))]);
  assert.equal(changed.event, 'snapshot.changed');
  assert.ok(changed.data.sequence > first.sequence);
  assert.equal((await send('snapshot.unsubscribe')).result.subscribed, false);
  child.stdin.end();
  const exit = await new Promise((resolve, reject) => { child.once('exit', resolve); child.once('error', reject); });
  assert.equal(exit, 0, stderr);
  assert.equal(buffer.length, 0, 'No truncated or extra stdout bytes');

  const denied = spawn(executable, [`chrome-extension://${'b'.repeat(32)}/`], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let deniedOutput = '';
  denied.stdout.on('data', data => { deniedOutput += data; });
  denied.stderr.resume(); denied.stdin.end();
  const deniedExit = await new Promise((resolve, reject) => { denied.once('exit', resolve); denied.once('error', reject); });
  assert.equal(deniedExit, 1);
  assert.equal(deniedOutput, '');
  console.log('PASS published Native Host: origin validation, binary framing, hello/subscribe/push/unsubscribe, read-only rejection, clean EOF (demo provider; no WPF)');
} finally { if (child.exitCode === null) child.kill(); }
