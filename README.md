# FocusApp Chrome / Edge 插件 MVP

同一份 Manifest V3 代码，使用 **Native Messaging → `FocusApp.Service.v2` Named Pipe** 读取状态，以 DNR session rule 将被屏蔽的 HTTP/HTTPS 顶层导航重定向到 `blocked.html`。

先读 [WPF 对接前提与缺口](docs/WPF_REQUIREMENTS.md)。原始审计文档 [BROWSER_EXTENSION_INTEGRATION.md](BROWSER_EXTENSION_INTEGRATION.md) 区分的“现有 Service IPC”和“拟议浏览器协议”仍然有效；本项目新增实现后者和 Native Host，并未在 WPF 中增加 HTTP API。

## 1. 本地加载与真实 WPF 通信

要求 Windows、.NET 8 Runtime（构建另需 .NET 8 SDK），Chrome / Edge 120+。最低目标版本仍须独立验收。Node.js 20+ 只用于测试。无 npm/NuGet 第三方包依赖。

1. 使用与 WPF 相同的 Windows 用户登录，确认匹配版本的 FocusApp Service / Agent 可用。
2. Chrome 打开 `chrome://extensions`，Edge 打开 `edge://extensions`，启用“开发者模式”，选择“加载已解压的扩展程序”，目录为本项目的 **`extension`**。记录两个浏览器显示的扩展 ID。
3. 在本项目根目录运行（脚本执行策略由你的本机配置决定）：

   ```powershell
   .\scripts\Build-Host.ps1
   .\scripts\Register-NativeHost.ps1 -ChromeExtensionId '实际Chrome扩展ID' -EdgeExtensionId '实际Edge扩展ID'
   ```

   也可以只传一个浏览器的 ID。脚本生成 Host manifest，写入当前用户的 Chrome / Edge NativeMessagingHosts 注册项。无需管理员权限；不要以另一个管理员用户注册。Host 与插件是两个独立发布物。
4. 在扩展管理页重新加载插件，点击工具栏图标。应显示“已连接本机服务”、专注阶段、域名数和应用序号。没有专注时不安装规则。
5. 在 WPF 添加并启用 `example.com`，开始普通专注。正式进入 Focusing，且 Service 报告网站保护已生效后，访问 `https://example.com/` 和 `https://a.example.com/`，应进入提示页，显示名称、域名、图标和估计剩余时间。
6. 在 WPF 结束专注或等待自然结束。确认最新状态为空闲、桌面网站保护停止且插件规则删除成功后，已打开的拦截页会自动返回对应网站的 HTTPS 首页，无需点击。当前协议仅保存域名，不保留原路径/查询参数。通信中断或清理失败时不会误判为专注结束，提示页保留诊断和手动访问入口。

Host 默认发布到 `artifacts\host`。开发产物依赖本机 .NET 8 Runtime；正式安装包还需签名、受保护安装目录和实际商店 ID。换路径或商店后扩展 ID 可能变化，必须更新 allowed_origins；不要使用通配符。重新注册时同时传入所有要保留的 ID，避免改写 manifest 后遗漏另一个浏览器。

## 2. 不依赖 WPF 的首轮演示

**此模式仅验证浏览器 ↔ Native Host → DNR → 提示页，不验证 WPF。** 默认屏蔽 `example.com` 60 秒，时间从浏览器启动该演示 Host 时计算。界面明确标记“演示”。Host 重启会重新开始此演示计时。

```powershell
.\scripts\Build-Host.ps1 -Demo
.\scripts\Register-NativeHost.ps1 -ChromeExtensionId '实际Chrome扩展ID' -EdgeExtensionId '实际Edge扩展ID' -HostDirectory .\artifacts\demo-host -ReplaceExisting
```

重新加载插件，访问 `https://example.com/`。60 秒后确认规则移除。演示 Host 的 `demo.enabled` 文件是显式开关，真实 Host 永不自动回退到演示数据。切回真实通信：

```powershell
.\scripts\Register-NativeHost.ps1 -ChromeExtensionId '实际Chrome扩展ID' -EdgeExtensionId '实际Edge扩展ID' -HostDirectory .\artifacts\host -ReplaceExisting
```

再次重新加载插件。Chrome 和 Edge 共用 manifest，测试时应一并切换。测试完成可卸载插件及注册项：

```powershell
.\scripts\Unregister-NativeHost.ps1
# 若注册的是演示目录：
.\scripts\Unregister-NativeHost.ps1 -HostDirectory .\artifacts\demo-host
```

卸载脚本只删除仍指向指定目录的本 Host 注册项，不会修改系统代理或结束专注。

## 3. 通信调试

先独立验证 Service 查询（只输出精简诊断，不修改专注或名单）：

```powershell
dotnet run --project .\native-host.tests\FocusApp.BrowserBridge.Tests.csproj -- --probe-service
```

成功会输出 Service 实例、阶段、剩余秒数、域名数量、保护健康状态；失败会明确输出 `SERVICE_UNAVAILABLE`、`TIMEOUT`、协议/数据错误。请确认当前 Windows 用户、Service 是否运行、Service/Contracts 版本，以及 Service 日志。不要请求本地代理的动态端口；它不是业务 API。

检查注册：

```powershell
Get-ItemProperty 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focusapp.browser_bridge'
Get-ItemProperty 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.focusapp.browser_bridge'
Get-Content .\artifacts\host\com.focusapp.browser_bridge.json
```

在扩展管理页点开 Service Worker 的“检查”，可执行：

```javascript
await chrome.declarativeNetRequest.getSessionRules()
await chrome.storage.session.get('policyCache')
```

缓存含当前有效名单，仅在自己的扩展调试器查看；不要把完整名单/访问路径上传到日志。真实 DNR 成功安装时存在规则 `10001`。弹窗的“快照 / 已应用序号”不同，且有 `DNR_APPLY_FAILED` 时，表示状态收到了但规则安装失败。清理失败显示 `DNR_CLEAR_FAILED` 并重试。

Host 的 stdout 仅写二进制长度帧，日志走 stderr；不能用 `echo` 的逐行 JSON 测试。Chrome/Edge 的“Host not found”通常是未注册、路径错误或 ID 不匹配；“Host exited”检查 Runtime、origin、可执行权限及帧格式。协议版本不兼容会停止自动重试，修复后点“重新连接”。Native 故障保留浏览器的具体错误文本。

## 4. 测试

```powershell
node --test tests/*.test.js
dotnet run --project .\native-host.tests\FocusApp.BrowserBridge.Tests.csproj
```

Host 测试在随机独立 Named Pipe 上模拟 Service，验证真实 IPC 编解码、只读调用及初始化期间重取；不会向生产 Service 写入测试数据。扩展测试验证排序、失效期限、状态恢复与 DNR 应用失败。

发布后的真实 Host 进程 / stdio 测试（显式演示数据，不注册 Host）：

```powershell
.\scripts\Build-Host.ps1 -Demo
node .\scripts\native-smoke.mjs
```

可选真实浏览器 smoke test（需本机已有 Playwright，或自行安装为开发依赖）：

```powershell
# 如果未本地安装 playwright，指定已有 Playwright 的 index.mjs：
$env:PLAYWRIGHT_MODULE = '你的playwright目录\index.mjs'
node .\scripts\browser-smoke.mjs
```

该脚本使用独立浏览器配置目录和扩展副本，**模拟 Native transport**，运行真实插件后台、DNR API、提示页和断线清理；不注册 Host、不访问真实网站。当前版本使用浏览器 CDP 的 `Extensions.loadUnpacked`，要求支持该开发调试接口的浏览器（已测版本见 [测试记录](docs/TEST_RESULTS.md)）；不能用于证明最低版本、真实 Host 或 WPF 已联通。可用 `--chrome-only` / `--edge-only`；路径通过 `CHROME_EXE` / `EDGE_EXE` 覆盖。截图在 `artifacts\browser-smoke`。

完整人工验收矩阵见 [TESTING.md](docs/TESTING.md)。联调至少分别完成 Chrome 与 Edge 的“WPF 开始 → 真实 Host → 重定向 → 结束恢复”。

## 5. 行为与边界

- 跟随 **Service 报告的网站保护**，并不在代理失败时自行改用专注意图。普通专注不拿 `runtime=idle` 或 `BlockingEnabled=false` 误判结束；强制专注仅使用其会话快照。
- 事件触发采样，100 ms 合并，至少约每 5 秒校准；全量状态只适合小数据 POC。非原子读取最多重取 3 次，否则明确报错。
- 名称来自现有网站规则 `Name`，规范化完全复用 Core。图标使用浏览器 favicon API；无图标/失败时显示本地域名字母图标，不依赖外部图标服务或 WPF 新图标接口。
- 单一聚合 DNR session rule 只匹配 HTTP/HTTPS `main_frame`，包含子域。仅将命中的 ASCII 主机名放进提示页 fragment，丢弃原路径、查询、端口和凭据。页面只可读取该站展示信息，拿不到整个名单。
- 正常结束、策略到期、30 秒租期失效都会清理；断线后最多保留 10 秒且不超过会话/保护/租期截止。使用 timer、Native 消息、alarm 和 UI 查询冗余触发，后台恢复也会校验旧缓存。
- **10/30 秒是清理目标，浏览器并不提供规则自动到期保证。** 系统睡眠、后台崩溃、alarm 延迟可使清理延后，恢复运行后重检。倒计时是 `estimatedUtc`，页面使用单调时钟推进，不能替代 WPF 的权威强制计时。
- 关闭 WPF 窗口不等于 Service 结束。强制专注可由 Service 继续，插件继续跟随；Service/Host 退出才进入通信失败流程。浏览器退出清除 session rules，再启动需重新同步。
- 拦截页约每 2 秒检查展示状态。只有最新已应用快照确认 idle、桌面网站保护 inactive 且自有规则已清空时才自动返回该域名的 HTTPS 首页；单纯倒计时归零、失联 fail-open 清理或名单中移除该网站不会自动跳转。后台标签页计时可能被浏览器节流，恢复可见时也会立即重查。
- 不移走已有标签页，不保证覆盖站点 Service Worker 缓存、XHR、iframe、下载、WebSocket 或隐私模式。插件可被禁用。恢复插件规则不保证 WPF 系统代理也已经恢复。

协议与新增字段见 [PROTOCOL.md](docs/PROTOCOL.md)，复用源码来源见 [Vendor/README.md](native-host/Vendor/README.md)。

## 拦截页界面调整

拦截页按双主题参考采用右侧布局，保留左侧人物插画。白天使用浅色卡片与暖色边框，夜晚使用半透明深蓝卡片与蓝色边框；标题与倒计时采用橙色。页面名称、域名、favicon、倒计时、恢复和断线状态仍由原有后台数据驱动。

- `extension/blocked.html`：屏蔽图标、标题、网站信息卡与专注状态布局。
- `extension/blocked.css`：以 `data-theme` 和 CSS 变量实现两套配色，包含桌面、竖屏和窄屏适配。
- `extension/assets/white_background.png`、`dark_background.png`：从项目 `asset/` 原样打包，通过 `cover` 等比裁切。
- `native-host/FocusThemeResolver.cs`：从软件现有 `FocusTheme` 设置计算生效主题并投影到 `effectiveTheme`。遵循桌面源码：Light/Dark 固定，System 在本地 05:00–18:00 为白天，其余为夜晚；不参考 Windows 主题。设置变化沿现有事件和采样链路同步。

双主题更新需要新版 Native Host；旧 Host 无此字段时保留最近确认主题，首次加载使用浅色基础样式。当前机器因旧 Host 进程占用 DLL，已将新版发布到 `artifacts/host-themes`，并保留原注册清单位置、允许的扩展 ID，只把清单中的可执行路径改到新版。旧进程继续运行，重新加载扩展后会启动新版，再刷新拦截页即可生效，无需修改注册表。原清单备份为 `artifacts/host-themes/previous-host-manifest.json`。

后续更新此部署可在关闭扩展的 Native 连接后运行 `dotnet publish native-host/FocusApp.BrowserBridge.csproj -c Release --self-contained false -o artifacts/host-themes`。全新机器仍按前面的标准 Build-Host/Register-NativeHost 步骤安装。主题同步通常在下一次 Service 事件或约 5 秒采样后、再下一次页面查询生效；确认结束后的结果页会自动返回，不用浏览器自行推测软件状态。
