const $ = id => document.getElementById(id);
const names = { idle: '空闲', preparing: '准备中', focusing: '专注中', completing: '结束处理中', unknown: '未知' };
async function refresh() {
  try {
    const s = await chrome.runtime.sendMessage({ type: 'status.get' });
    $('connection').textContent = s.connected ? (s.source === 'demo' ? '已连接 · 演示模式' : '已连接本机服务') : '未连接 / 等待同步';
    $('summary').textContent = s.applyError ? '插件规则应用失败' : s.active ? '网站重定向保护已启用' : '当前未启用插件网站保护';
    $('focus').textContent = names[s.focusState];
    $('remaining').textContent = s.remainingSeconds === null ? '未知' : `${Math.floor(s.remainingSeconds / 60)} 分 ${s.remainingSeconds % 60} 秒`;
    $('rules').textContent = `${s.domainCount} / ${s.ruleCount}`;
    $('desktop').textContent = `${s.desktopState}${s.protectionError ? ` (${s.protectionError})` : ''}`;
    $('sequence').textContent = `${s.sequence ?? '--'} / ${s.rulesAppliedSequence ?? '--'}`;
    $('error').textContent = s.applyError || s.error || '';
  } catch { $('connection').textContent = '插件后台不可用'; }
}
$('reconnect').addEventListener('click', async () => { await chrome.runtime.sendMessage({ type: 'reconnect' }); await refresh(); });
setInterval(refresh, 1000);
await refresh();
