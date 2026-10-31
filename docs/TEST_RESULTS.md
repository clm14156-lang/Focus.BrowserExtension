# 验证记录 · 2026-10-17

## 已通过

| 层次 | 结果 | 范围 |
| --- | --- | --- |
| 插件逻辑 | Node.js 24，10/10 测试 | 域名边界、重定向隐私、状态校验、序号、倒计时、结束/租期/断线清理、缓存恢复、DNR 应用/清理错误 |
| Host / Service IPC 适配 | .NET 8，14/14 测试 | 普通与强制语义、快照隔离、故障状态、Core IDN 规范化、超限、分段帧、只读 Native 协议、随机测试 Named Pipe 的真实四查询和事件重采样 |
| 发布构建 | Release 构建成功，0 warning / error | 真实与演示目录均可生成 .exe，依赖本机 .NET 8 Runtime |
| 发布 Host 进程 | native-smoke 通过 | 实际 exe/stdin/stdout、origin 校验、hello/subscribe/push/unsubscribe、禁止写方法、EOF。provider 是演示夹具 |
| Chrome 155.0.8059.82 | browser-smoke 通过 | 真实扩展后台、DNR session rule 和页面；自身/子域/相似域/端口/IDN、名称、倒计时、页面消息边界、正常结束、10 秒断线清理 |
| Edge 155.0.4283.45 | browser-smoke 通过 | 与 Chrome 相同代码和场景，使用独立测试 Profile |

浏览器测试用副本里的显式模拟 Native transport 驱动真实插件代码；网站响应本地模拟，不访问真实网站，不改 Host 注册表。Chrome 测试在沙箱内运行；Edge 在沙箱内启动即崩溃，改为沙箱外独立 Profile 后通过。截图位于 `artifacts/browser-smoke/chrome-blocked.png` 和 `edge-blocked.png`，已目视检查页面布局。

## 尚未通过真实环境验收

真实 `--probe-service` 在沙箱内和沙箱外均返回 **`SERVICE_UNAVAILABLE`**。只能确认当前环境无法取得真实 Service 快照，不能据此断言未安装 Service 或 WPF 存在故障。真实的 **WPF → Service → Native Host → 浏览器** 联调尚未完成。

需要在同用户、匹配版本且可连接的 FocusApp Service 上按 TESTING.md 验收。最低 120 版本、Windows 多用户、真实后台休眠、系统睡眠唤醒、时间回拨、真实强制恢复、大历史 1 MiB 限制、真实系统代理恢复与发布安装器均未完成环境测试。测试中保持 DevTools/CDP 连接，不能据此证明所有后台生命周期表现。

## 拦截页夜景 UI 重构回归

2026-10-17 更新：使用用户提供的第三张夜景素材，原样随扩展打包；品牌使用现有 WPF Logo。仅调整拦截页结构、独立 CSS、展示文案以及静态资源声明，Native Host、后台协议和 DNR 决策保持不变。

- Chrome `155.0.8059.82`、Edge `155.0.4283.45` 均通过最终 browser-smoke；使用独立 Profile、真实扩展后台/DNR API、显式模拟 Native transport。
- 目视检查 `1672×941` 参考比例、`1321×1108` 当前截图比例，以及 `390×844`、`320×640` 窄屏；无横向溢出。
- 验证来自测试快照的名称、域名、favicon、倒计时继续动态更新；长名称、长 DNS 主机及 `23:59:59` 的窄屏展示正常。
- 验证专注结束后的恢复标题、重新访问链接、DNR 移除，以及失联后的规则清理；原有 10 项 Node 策略测试通过。
- 背景与用户原文件 SHA-256 相同；所有 web-accessible 静态资源存在，浏览器能加载背景和 Logo。

预览在 `artifacts/browser-smoke/`：`chrome-blocked.png`、`chrome-blocked-1321.png`、`chrome-blocked-390.png`、`chrome-blocked-320.png`、`chrome-blocked-long.png`、`chrome-restored.png`，Edge 同名对应文件也已生成。预览包含明确标识的演示数据，生产页面继续使用现有 Host 状态。

## 专注结束自动返回网站

2026-10-17 更新：拦截页在后台确认最新快照 idle/0、桌面网站保护 inactive、自有 DNR 规则删除成功后，通过 `location.replace` 自动返回当前域名的 HTTPS 首页。不再要求点击恢复按钮；不保存或恢复原路径、查询参数和端口。

- Node 策略测试 **13/13** 通过，覆盖正常结束、运行状态残留 Faulted 诊断、桌面保护收尾、专注中移除域名、失联、本地估计到期、旧缓存和 DNR 清理失败等授权边界。
- Chrome `155.0.8059.82` 与 Edge `155.0.4283.45` 的实际浏览器回归通过：多个拦截标签页在确认结束后各自自动返回对应网站；IDN 正常；通信中断后即使规则按 fail-open 清理，也不会误触发自动返回。
- 测试继续使用独立 Profile、真实 DNR API、显式模拟 Native transport 和本地网站响应；未修改当前用户浏览器、Native Host 注册或 WPF 状态。普通后台标签页仍可能受浏览器节流影响，恢复可见时会重新同步。
- 新的导航截图为 `artifacts/browser-smoke/chrome-auto-return.png` 与 `edge-auto-return.png`；此前 `*-restored.png` 是旧版需要点击的历史预览。

本次仅更新插件，无需重新构建或注册 C# Host。重新加载扩展，并刷新原先已打开的拦截页后生效。

## 拦截页按新版参考图还原 · 2026-10-23

- 使用现有 `extension/assets/blocked_background.png` 全屏等比 `cover`，保留原始插画；左侧仅叠加深蓝渐变。
- 按参考图调整品牌位置、标题字号与橙色、卡片尺寸和留白，增加低饱和红色屏蔽标签；计时区域改为时钟图标、“距离专注结束”和动态剩余时间。底部显示两行指定文案。
- 网站名称、域名、favicon、剩余时间仍使用原有后台与浏览器 API；保留确认结束后的自动返回和异常时的提示。状态标签随屏蔽、准备、收尾、空闲、保护异常及失联状态更新。
- Node 策略测试 **13/13** 通过。Chrome **155.0.8059.85**、Edge **155.0.4283.62** 的真实扩展回归均通过，Native transport 和网站响应使用显式测试夹具，不代表真实 WPF 联调。
- 目视检查 `1827×1034` 参考尺寸，以及 `1672×941`、`1321×1108`、`390×844`、`320×640`。普通内容均无需页面滚动，无横向溢出；长名称、长域名与 `23:59:59` 可完整换行，内容增高时允许正常纵向滚动。
- 额外验证未知剩余时间显示 `--:--`，准备、收尾与空闲状态不误显示屏蔽标签，保护异常及后台无响应时提示正确，多个标签页确认结束后各自自动返回，断线清理不触发自动返回。
- 预览：`artifacts/browser-smoke/chrome-blocked-1827.png`、`chrome-blocked-390.png`、`chrome-blocked-320.png`、`chrome-blocked-long.png`，Edge 对应同名截图。截图中的网站信息来自测试夹具，生产页面未写死示例。

## 拦截页白天/夜晚双主题 · 2026-10-29

- 两张背景从项目 `asset/white_background.png`、`dark_background.png` 原样复制；按对应浅色/深蓝参考恢复右侧屏蔽图标、标题、网站卡片和橙色倒计时，不添加主题切换按钮。
- 核对当前桌面源码 `FocusSessionViewModel.RefreshFocusTheme/CurrentGreetingState` 和设置持久化链路。Host 复用 `FocusThemeMode`，从既有四查询中的 `state.get.settings.focusTheme` 解析生效主题并传递 `effectiveTheme`；没有新增写操作、系统主题监听或独立浏览器主题时钟。
- Node **15/15**、Host/IPC **16/16** 测试通过，覆盖 Light/Dark 固定、System 在 04:59/05:00/17:59/18:00 等边界的切换、普通/强制主题更新、未知字段回退和原有策略授权边界。
- Chrome **156.0.8078.48**、Edge **156.0.4314.42** 真实扩展测试通过。每个软件主题均故意配置相反的浏览器系统配色；主题仍与 Host 一致。验证同一个页面自动切换、无主题时保留最近确认值、异常状态、倒计时、IDN、多标签页自动返回，以及断线清理不触发自动返回。
- 检查双主题在 `1699×926`、`1827×1034`、`1321×1108`、`1024×600`、`390×844`、`320×640` 下普通内容无页面滚动和横向溢出；双主题长名称、长域名与小时计时可正常增高、换行和纵向滚动。
- 真实 Service 的只读 `--probe-service` 成功：当前 idle、保护 inactive，生效主题 light。浏览器完整拦截回归仍使用显式模拟 Native transport，未人为启动或结束真实专注。
- 新版 Host Release 发布到 `artifacts/host-themes` 成功。旧目录 DLL 被当前 Host 占用，因此采用独立目录并更新原清单里的路径；不停止原进程、不改变扩展 ID 或注册表，重新加载扩展后启用。
- 预览为 `artifacts/browser-smoke/chrome-blocked-light-1699.png`、`chrome-blocked-dark-1699.png` 及各尺寸、`*-light-long.png`、`*-dark-long.png`；Edge 对应截图也已生成。

## 保护期限边界报错修复 · 2026-10-29

针对 `service-worker.js:30` 记录的 `FocusApp INVALID_SNAPSHOT: 保护期限`，复现了采样时保护有效、到达扩展时已过期的情况。

- 保护/会话字段按采样时刻 `observedAtUtc` 检查一致性；采样时就已过期的数据仍拒绝。
- 控制器在安装 DNR 规则前和异步 API 返回后均检查实际期限；到期立即清理，不添加已过期规则，清理失败仍保留诊断并支持重试。
- 到期快照不授权自动返回；新快照可恢复正常保护。快照租期和本机时钟校验未放宽。
- Node 策略测试 **18/18** 通过，新增保护/会话在途到期、采样时无效、应用期间到期与清理失败重试场景。浏览器回归增加了真实 Worker/DNR 下的在途到期用例，并检查没有误报错误、误安装规则或误跳转。
- Chrome **156.0.8078.48**、Edge **156.0.4314.42** 回归均通过；使用独立测试 Profile、模拟 Native transport 和本地网站响应。
- 真实 Service 只读检查成功，当前为 focusing、网站保护 active；未修改真实专注状态、名单、代理或 Native Host。
