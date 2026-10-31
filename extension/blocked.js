const $ = id => document.getElementById(id);
const host = new URLSearchParams(location.hash.slice(1)).get('host')?.toLowerCase() ?? '';
const validHost = host.length <= 253 && host.includes('.') && host.split('.').every(s => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(s));
let sample = null;
let sampledAt = 0;
let returning = false;

function setHeadline(main, accent = '') {
  $('headline-main').textContent = main;
  $('headline-accent').textContent = accent;
}

function setBadge(label, state) {
  $('block-status').textContent = label;
  $('block-badge').dataset.state = state;
}

function format(seconds) {
  if (seconds === null) return '--:--';
  const h = Math.floor(seconds / 3600);
  const m = Math.floor(seconds % 3600 / 60);
  const s = seconds % 60;
  return `${h ? `${h}:` : ''}${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

function tick() {
  const left = sample?.remainingSeconds;
  $('remaining').textContent = format(left == null ? null : Math.max(0, left - Math.floor((performance.now() - sampledAt) / 1000)));
}

async function sync() {
  if (!validHost) {
    setHeadline('没有可显示的', '网站');
    $('subtitle').textContent = '请通过正常的网站导航打开专注提示页';
    setBadge('无效地址', 'pending');
    $('access-note').textContent = '请通过正常的网站导航打开此页面。';
    $('message').textContent = '请通过正常的网站导航打开专注提示页。';
    return;
  }
  try {
    const state = await chrome.runtime.sendMessage({ type: 'blocked.get', host });
    if (!state) throw new Error('后台未响应');
    // The bridge resolves the app's focus theme. Keep the last confirmed theme on
    // disconnect/older hosts; neither OS preferences nor browser time choose it.
    if (state.effectiveTheme === 'light' || state.effectiveTheme === 'dark') {
      document.documentElement.dataset.theme = state.effectiveTheme;
      document.querySelector('meta[name="color-scheme"]').content = state.effectiveTheme;
    }
    sample = state;
    sampledAt = performance.now();
    if (state.canAutoReturn === true) {
      if (!returning) {
        returning = true;
        setHeadline('专注已结束', '正在返回');
        $('subtitle').textContent = '正在自动返回网站…';
        $('focus-status').textContent = '专注已结束';
        setBadge('已恢复', 'restored');
        $('access-note').textContent = '访问限制已解除，正在返回此网站。';
        $('return-site').hidden = true;
        $('message').hidden = true;
        tick();
        // The redirect contract retains only a validated host, so return to its HTTPS home.
        // replace() avoids leaving an obsolete interception page in browser history.
        location.replace(`https://${host}/`);
      }
      return;
    }
    $('site-name').textContent = state.site?.name || host;
    if (state.applyError) setHeadline('插件规则', '需要恢复');
    else if (!state.connected) setHeadline('正在等待', '连接恢复');
    else if (state.blocked) setHeadline('此网站', '已被屏蔽');
    else setHeadline('网站访问', '已恢复');
    if (state.applyError) setBadge('保护异常', 'error');
    else if (!state.connected) setBadge('待同步', 'pending');
    else if (state.blocked) setBadge('已屏蔽', 'blocked');
    else setBadge('已恢复', 'restored');
    $('subtitle').textContent = state.applyError ? '网站保护遇到问题，正在尝试恢复'
      : !state.connected ? '与本机软件暂时断开，正在重新同步状态'
      : state.blocked ? '为了减少干扰，专注时光已在专注期间限制访问此网站。' : '当前已解除此网站的访问限制';
    $('focus-status').textContent = !state.connected ? '连接中断' : state.applyError ? '保护异常'
      : ({ focusing: '距离专注结束', preparing: '准备专注', idle: '专注已结束', completing: '专注已结束' }[state.focusState] ?? '等待同步');
    $('access-note').textContent = state.applyError || !state.connected ? '正在同步访问状态，请稍候。'
      : state.blocked ? '专注结束后，即可重新访问此网站。' : '此网站的访问限制已解除。';
    $('timer-caption').textContent = `剩余专注时间 · 估计值${state.source === 'demo' ? ' · 演示数据' : ''}`;
    $('message').textContent = state.applyError || (!state.connected
      ? '与本机软件的通信已中断；插件正在重新连接。旧规则会在有效期内清理。'
      : state.blocked ? '专注结束后，网站会恢复正常访问。' : '当前没有此网站的有效屏蔽规则，可以重新访问。');
    $('message').hidden = state.connected && state.blocked && !state.applyError;
    $('return-site').hidden = state.blocked || !!state.applyError;
    tick();
  } catch {
    sample = null;
    setHeadline('正在等待', '连接恢复');
    setBadge('待同步', 'pending');
    $('subtitle').textContent = '插件后台暂时不可用，正在重新同步状态';
    $('focus-status').textContent = '等待同步';
    $('access-note').textContent = '正在同步访问状态，请稍候。';
    $('return-site').hidden = true;
    $('message').hidden = false;
    $('message').textContent = '插件后台暂时不可用，请稍后重试或重新加载插件。';
    tick();
  }
}

if (validHost) {
  $('site-domain').textContent = host;
  $('fallback-icon').textContent = host[0].toUpperCase();
  $('return-site').href = `https://${host}/`;
  const favicon = new URL(chrome.runtime.getURL('/_favicon/'));
  favicon.searchParams.set('pageUrl', `https://${host}/`);
  favicon.searchParams.set('size', '64');
  $('site-icon').addEventListener('load', () => { $('site-icon').hidden = false; $('fallback-icon').hidden = true; });
  $('site-icon').addEventListener('error', () => { $('site-icon').hidden = true; $('fallback-icon').hidden = false; });
  $('site-icon').src = favicon.href;
}
setInterval(tick, 250);
setInterval(sync, 2000);
document.addEventListener('visibilitychange', () => { if (!document.hidden) sync(); });
window.addEventListener('pageshow', sync);
await sync();
