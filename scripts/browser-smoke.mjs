// Optional real Chrome/Edge DNR/UI test. Native transport is explicitly simulated in the copied fixture.
// Requires Playwright; set PLAYWRIGHT_MODULE to its index.mjs if not installed in this repository.
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : 'playwright');
const browsers = process.argv.includes('--edge-only') ? ['edge'] : process.argv.includes('--chrome-only') ? ['chrome'] : ['chrome', 'edge'];
const output = path.join(root, 'artifacts', 'browser-smoke');
await mkdir(output, { recursive: true });

function snapshot() {
  const now = Date.now();
  return {
    epoch: 'browser-fixture', sequence: 1, serviceInstanceId: 'TEST-FIXTURE-NOT-WPF', sourceRevision: '0', effectiveTheme: 'light',
    observedAtUtc: new Date(now).toISOString(), leaseExpiresAtUtc: new Date(now + 30000).toISOString(), consistency: 'sampled', source: 'demo',
    focus: { state: 'focusing', configuredSeconds: 60, plannedEndAtUtc: new Date(now + 60000).toISOString(), remainingSeconds: 60, remainingAccuracy: 'estimatedUtc' },
    websitePolicy: { enforcementMode: 'mirrorReportedProtection', scope: 'mainFrame', shouldBlock: true, ruleSource: 'current', rules: [
      { id: 'a', domain: 'example.com', name: '示例网站', includeSubdomains: true },
      { id: 'b', domain: 'sub.example.com', name: '子网站', includeSubdomains: true },
      { id: 'c', domain: 'xn--fsqu00a.xn--0zwm56d', name: '国际域名', includeSubdomains: true }
    ] },
    desktopProtection: { state: 'active', websiteProtectionActive: true, expiresAtUtc: new Date(now + 60000).toISOString(), errorCode: null }
  };
}

for (const name of browsers) {
  let sequence = 1;
  const fixtureDir = path.join(output, `extension-${name}`);
  await cp(path.join(root, 'extension'), fixtureDir, { recursive: true });
  const manifestPath = path.join(fixtureDir, 'manifest.json');
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  manifest.background.service_worker = 'test-entry.js';
  await writeFile(manifestPath, JSON.stringify(manifest));
  const initial = JSON.stringify(snapshot());
  const bootstrap = `
    globalThis.fixtureErrors = [];
    const originalConsoleError = console.error.bind(console);
    console.error = (...args) => { globalThis.fixtureErrors.push(args.map(String).join(' ')); originalConsoleError(...args); };
    globalThis.fixtureSnapshot = ${initial};
    globalThis.hostAvailable = true;
    const ports = [];
    chrome.runtime.connectNative = () => {
      const messages = [], disconnects = [];
      let closed = false;
      const p = {
        onMessage: { addListener: f => messages.push(f) },
        onDisconnect: { addListener: f => disconnects.push(f) },
        disconnect() { if (!closed) { closed = true; disconnects.forEach(f => f()); } },
        postMessage(r) {
          queueMicrotask(() => {
            if (closed) return;
            if (!globalThis.hostAvailable) { p.disconnect(); return; }
            const result = r.method === 'hello' ? { protocolVersion: 1 } : globalThis.fixtureSnapshot;
            messages.forEach(f => f({ version: 1, type: 'response', id: r.id, result, error: null }));
          });
        },
        push(s) { if (!closed) messages.forEach(f => f({ version: 1, type: 'event', event: 'snapshot.changed', data: s })); }
      };
      ports.push(p); return p;
    };
    globalThis.pushFixture = s => { globalThis.fixtureSnapshot = s; ports.forEach(p => p.push(s)); };
    globalThis.loseHost = () => { globalThis.hostAvailable = false; ports.forEach(p => p.disconnect()); };
  `;
  await writeFile(path.join(fixtureDir, 'test-native.js'), bootstrap);
  await writeFile(path.join(fixtureDir, 'test-entry.js'), "import './test-native.js';\nimport './service-worker.js';\n");
  const executablePath = name === 'edge'
    ? (process.env.EDGE_EXE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe')
    : (process.env.CHROME_EXE ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe');
  const profile = path.join(root, '.test-profiles', `${name}-${Date.now()}`);
  const context = await chromium.launchPersistentContext(profile, {
    executablePath, headless: true, ignoreDefaultArgs: ['--disable-extensions'],
    viewport: { width: 1672, height: 941 }, deviceScaleFactor: 1,
    args: ['--enable-unsafe-extension-debugging']
  });
  try {
    context.on('weberror', error => console.error(name, 'web error:', error.error().message));
    const cdp = await context.browser().newBrowserCDPSession();
    const { id } = await cdp.send('Extensions.loadUnpacked', { path: fixtureDir });
    const base = `chrome-extension://${id}/`;
    const worker = context.serviceWorkers().find(w => w.url().startsWith(base))
      ?? await context.waitForEvent('serviceworker', { predicate: w => w.url().startsWith(base) });
    const popup = await context.newPage();
    await popup.goto(base + 'popup.html');
    try { await popup.locator('#summary').filter({ hasText: '网站重定向保护已启用' }).waitFor({ timeout: 8000 }); }
    catch (error) {
      console.error(name, 'popup:', await popup.locator('body').innerText());
      console.error(name, 'worker:', await worker.evaluate(async () => ({ stub: String(chrome.runtime.connectNative), cache: await chrome.storage.session.get(null), rules: await chrome.declarativeNetRequest.getSessionRules() })));
      throw error;
    }
    const apiRules = await worker.evaluate(() => chrome.declarativeNetRequest.getSessionRules());
    assert.equal(apiRules.length, 1, `${name}: DNR rule installed`);
    const page = await context.newPage();
    await context.route(/^https?:/, route => route.fulfill({ contentType: 'text/html', body: '<html><title>Allowed fixture</title><body>Allowed fixture</body></html>' }));
    await page.goto('https://sub.example.com/private?q=secret');
    await page.waitForURL(base + 'blocked.html#host=sub.example.com');
    await page.locator('#site-name').filter({ hasText: '子网站' }).waitFor();
    assert.equal(await page.locator('#site-domain').textContent(), 'sub.example.com');
    assert.ok(!page.url().includes('secret'));
    assert.notEqual(await page.locator('#remaining').textContent(), '--:--');
    assert.equal(await page.locator('#headline').innerText(), '此网站已被屏蔽');
    assert.equal(await page.locator('#subtitle').textContent(), '为了减少干扰，专注时光已在专注期间限制访问此网站。');
    assert.equal(await page.locator('#focus-status').textContent(), '距离专注结束');
    assert.equal(await page.locator('#block-status').textContent(), '已屏蔽');
    assert.equal(await page.locator('#block-badge').getAttribute('data-state'), 'blocked');
    assert.equal(await page.locator('#access-note').textContent(), '专注结束后，即可重新访问此网站。');
    assert.equal(await page.locator('#return-site').isVisible(), false);
    const imageStatus = await page.evaluate(async () => {
      const images = ['assets/white_background.png', 'assets/dark_background.png'];
      return Promise.all(images.map(src => new Promise(resolve => {
        const image = new Image();
        image.onload = () => resolve({ src, width: image.naturalWidth, height: image.naturalHeight });
        image.onerror = () => resolve({ src, width: 0, height: 0 });
        image.src = chrome.runtime.getURL(src);
      })));
    });
    assert.ok(imageStatus.every(image => image.width > 0), 'both theme backgrounds are accessible in the extension');
    const countdownBefore = await page.locator('#remaining').textContent();
    await page.waitForFunction(previous => document.getElementById('remaining').textContent !== previous, countdownBefore);
    await page.screenshot({ path: path.join(output, `${name}-blocked.png`), fullPage: true });
    for (const theme of ['light', 'dark']) {
      const themedState = snapshot(); themedState.sequence = ++sequence; themedState.effectiveTheme = theme;
      await worker.evaluate(s => globalThis.pushFixture(s), themedState);
      await page.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      // Deliberately use the opposite OS color scheme to catch accidental matchMedia/CSS coupling.
      await page.emulateMedia({ colorScheme: theme === 'light' ? 'dark' : 'light' });
      assert.equal(await page.locator('html').getAttribute('data-theme'), theme);
      assert.match(await page.locator('.page-scene').evaluate(el => getComputedStyle(el).backgroundImage),
        theme === 'light' ? /assets\/white_background\.png/ : /assets\/dark_background\.png/);
      for (const [width, height] of [[1699, 926], [1827, 1034], [1321, 1108], [1024, 600], [390, 844], [320, 640]]) {
        await page.setViewportSize({ width, height });
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${name}: ${theme} has no horizontal overflow at ${width}px`);
        assert.ok(await page.evaluate(() => document.documentElement.scrollHeight <= innerHeight), `${name}: ${theme} normal content fits at ${width}×${height}`);
        await page.screenshot({ path: path.join(output, `${name}-blocked-${theme}-${width}.png`), fullPage: true });
      }
    }
    // A sample valid at the host may arrive just after its protection/session
    // deadline. It must clear rules without logging INVALID_SNAPSHOT or returning.
    for (const boundary of ['protection', 'focus']) {
      const delayed = snapshot();
      const receivedAt = Date.now();
      delayed.sequence = ++sequence;
      delayed.observedAtUtc = new Date(receivedAt - 1000).toISOString();
      delayed.leaseExpiresAtUtc = new Date(receivedAt + 29000).toISOString();
      if (boundary === 'protection') delayed.desktopProtection.expiresAtUtc = new Date(receivedAt - 1).toISOString();
      else {
        delayed.focus.plannedEndAtUtc = new Date(receivedAt - 1).toISOString();
        delayed.focus.remainingSeconds = 1;
      }
      await worker.evaluate(s => globalThis.pushFixture(s), delayed);
      await page.waitForFunction(async sequence => {
        const s = await chrome.runtime.sendMessage({ type: 'blocked.get', host: 'sub.example.com' });
        return s.sequence === sequence && !s.blocked;
      }, delayed.sequence);
      const expired = await page.evaluate(() => chrome.runtime.sendMessage({ type: 'blocked.get', host: 'sub.example.com' }));
      assert.match(expired.error, /SNAPSHOT_EXPIRED/);
      assert.equal(expired.canAutoReturn, false);
      assert.equal(page.url(), base + 'blocked.html#host=sub.example.com');
      assert.equal((await worker.evaluate(() => chrome.declarativeNetRequest.getSessionRules())).length, 0);
      assert.deepEqual(await worker.evaluate(() => globalThis.fixtureErrors), [], `${name}: ${boundary} expiry in transit is not a protocol error`);
      const fresh = snapshot(); fresh.sequence = ++sequence;
      await worker.evaluate(s => globalThis.pushFixture(s), fresh);
      await page.waitForFunction(async sequence => {
        const s = await chrome.runtime.sendMessage({ type: 'blocked.get', host: 'sub.example.com' });
        return s.sequence === sequence && s.connected && s.blocked;
      }, fresh.sequence);
    }
    const longState = snapshot();
    longState.sequence = ++sequence;
    longState.websitePolicy.rules[1].name = '这是来自动态名单的网站名称，用于验证长名称的自然换行与完整显示';
    longState.focus.configuredSeconds = 86399;
    longState.focus.remainingSeconds = 86399;
    longState.focus.plannedEndAtUtc = new Date(Date.now() + 86399000).toISOString();
    longState.desktopProtection.expiresAtUtc = longState.focus.plannedEndAtUtc;
    await worker.evaluate(s => globalThis.pushFixture(s), longState);
    const longHost = `${'long-site-'.repeat(5)}test.sub.example.com`;
    await page.goto(`https://${longHost}/`);
    await page.locator('#site-name').filter({ hasText: longState.websitePolicy.rules[1].name }).waitFor();
    assert.equal(await page.locator('#site-domain').textContent(), longHost);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${name}: dynamic long name/domain/hour timer fits at 320px`);
    await page.screenshot({ path: path.join(output, `${name}-blocked-long.png`), fullPage: true });
    for (const theme of ['light', 'dark']) {
      longState.sequence = ++sequence; longState.effectiveTheme = theme;
      await worker.evaluate(s => globalThis.pushFixture(s), longState);
      await page.waitForFunction(theme => document.documentElement.dataset.theme === theme, theme);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${name}: ${theme} long name/domain/hour timer fits`);
      await page.screenshot({ path: path.join(output, `${name}-blocked-${theme}-long.png`), fullPage: true });
    }
    // Exercise presentation states without altering the real worker or its policy decisions.
    const displayState = await page.evaluate(async host => chrome.runtime.sendMessage({ type: 'blocked.get', host }), longHost);
    for (const [changes, badge, focus, time] of [
      [{ remainingSeconds: null }, 'blocked', '距离专注结束', '--:--'],
      [{ effectiveTheme: null }, 'blocked', '距离专注结束', null],
      [{ blocked: false, focusState: 'preparing' }, 'restored', '准备专注', null],
      [{ blocked: false, focusState: 'completing', remainingSeconds: 0 }, 'restored', '专注已结束', '00:00'],
      [{ blocked: false, focusState: 'idle', remainingSeconds: 0 }, 'restored', '专注已结束', '00:00'],
      [{ applyError: 'DNR_APPLY_FAILED: fixture' }, 'error', '保护异常', null]
    ]) {
      await page.evaluate(state => {
        chrome.runtime.sendMessage = async () => state;
        window.dispatchEvent(new Event('pageshow'));
      }, { ...displayState, ...changes, canAutoReturn: false });
      await page.waitForFunction(expected => document.getElementById('block-badge').dataset.state === expected.badge
        && document.getElementById('focus-status').textContent === expected.focus, { badge, focus });
      if (time) assert.equal(await page.locator('#remaining').textContent(), time);
      assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark', 'missing theme must keep the last confirmed app theme');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${name}: ${focus} fits at 320px`);
    }
    await page.evaluate(() => {
      chrome.runtime.sendMessage = async () => { throw new Error('fixture: background unavailable'); };
      window.dispatchEvent(new Event('pageshow'));
    });
    await page.locator('#message').filter({ hasText: '插件后台暂时不可用' }).waitFor();
    assert.equal(await page.locator('#remaining').textContent(), '--:--');
    assert.equal(await page.locator('#block-badge').getAttribute('data-state'), 'pending');
    assert.equal(await page.locator('#return-site').isVisible(), false);
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark', 'background failure must keep the last confirmed theme');
    await page.reload(); // Restore the real runtime API before testing navigation authorization.
    const normalState = snapshot(); normalState.sequence = ++sequence;
    await worker.evaluate(s => globalThis.pushFixture(s), normalState);
    await page.setViewportSize({ width: 1672, height: 941 });
    const denied = await page.evaluate(async () => {
      try { return await chrome.runtime.sendMessage({ type: 'status.get' }); } catch { return null; }
    });
    assert.ok(!denied, 'blocked page cannot request popup-wide status');
    for (const host of ['notexample.com', 'example.com.evil.test']) {
      await page.goto(`https://${host}/`);
      assert.equal(await page.title(), 'Allowed fixture');
    }
    await page.goto('http://example.com:8080/path');
    await page.waitForURL(base + 'blocked.html#host=example.com');
    await page.goto('https://例子.测试/path');
    await page.waitForURL(base + 'blocked.html#host=xn--fsqu00a.xn--0zwm56d');
    const secondBlockedPage = await context.newPage();
    await secondBlockedPage.goto('https://sub.example.com/another-blocked-path');
    await secondBlockedPage.waitForURL(base + 'blocked.html#host=sub.example.com');
    await secondBlockedPage.locator('#headline').filter({ hasText: '此网站已被屏蔽' }).waitFor();
    const end = snapshot();
    end.sequence = ++sequence; end.focus.state = 'idle'; end.focus.remainingSeconds = 0;
    end.websitePolicy.shouldBlock = false; end.desktopProtection.state = 'inactive'; end.desktopProtection.websiteProtectionActive = false;
    await worker.evaluate(s => globalThis.pushFixture(s), end);
    await popup.locator('#summary').filter({ hasText: '当前未启用插件网站保护' }).waitFor();
    assert.equal((await worker.evaluate(() => chrome.declarativeNetRequest.getSessionRules())).length, 0);
    await Promise.all([
      page.waitForURL('https://xn--fsqu00a.xn--0zwm56d/'),
      secondBlockedPage.waitForURL('https://sub.example.com/')
    ]);
    assert.equal(await page.title(), 'Allowed fixture');
    assert.equal(await secondBlockedPage.title(), 'Allowed fixture');
    await page.screenshot({ path: path.join(output, `${name}-auto-return.png`), fullPage: true });
    await secondBlockedPage.close();
    await page.goto('https://example.com/');
    assert.equal(await page.title(), 'Allowed fixture');
    const resume = snapshot(); resume.sequence = ++sequence;
    await worker.evaluate(s => globalThis.pushFixture(s), resume);
    await popup.locator('#summary').filter({ hasText: '网站重定向保护已启用' }).waitFor();
    await page.goto('https://sub.example.com/stay-on-interception-if-disconnected');
    await page.waitForURL(base + 'blocked.html#host=sub.example.com');
    await page.locator('#headline').filter({ hasText: '此网站已被屏蔽' }).waitFor();
    await worker.evaluate(() => globalThis.loseHost());
    await popup.locator('#connection').filter({ hasText: '未连接' }).waitFor();
    // Real worker timeout and DNR removal, with DevTools open. Sleep/background cases remain manual acceptance.
    await popup.locator('#rules').filter({ hasText: '3 / 0' }).waitFor({ timeout: 16000 });
    assert.equal((await worker.evaluate(() => chrome.declarativeNetRequest.getSessionRules())).length, 0);
    assert.equal(page.url(), base + 'blocked.html#host=sub.example.com', 'disconnect cleanup must not be mistaken for focus completion');
    await page.locator('#focus-status').filter({ hasText: '连接中断' }).waitFor();
    assert.equal(await page.locator('#block-badge').getAttribute('data-state'), 'pending');
    console.log(`PASS ${name} ${context.browser().version()}: app light/dark themes independent of OS, responsive UI/countdown, automatic return in multiple tabs after confirmed end, no auto return on disconnect, cleanup (simulated Native transport)`);
  } finally { await context.close(); }
}
