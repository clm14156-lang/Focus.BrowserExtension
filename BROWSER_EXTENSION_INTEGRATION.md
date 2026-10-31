# Chrome / Edge 浏览器插件本地对接方案

审计日期：2026-10-17。代码基线：`23d4084152cdb9394501ca43e6bd91c535638fc1`。

本文面向独立浏览器插件项目，区分以下三种内容：

- **现有实现**：已从本仓库源码确认，可以在后续对接中复用。
- **拟议协议/接口**：本文设计的集成契约，当前尚不存在，不能直接调用。
- **待验证边界**：源码分析能确认机制，但尚未进行浏览器、安装包和真实系统代理联调。

本次交付只新增本文档，没有实现 Native Host、HTTP API、浏览器插件，也没有修改现有网站屏蔽逻辑。

## 1. 结论与接入前提

**推荐：Chrome / Edge Manifest V3 插件 → Native Messaging Host（拟新增的小型 C# 进程）→ 现有 Named Pipe → FocusApp.Service。**

当前已经具备持久化屏蔽规则、普通/强制专注记录、访问控制运行状态，以及状态变更推送。插件无需读取 WPF 控件、扫描数据库或复制专注计时器。新增的 Host 负责读取、筛选和转换这些数据，插件负责浏览器内拦截及倒计时展示。

现有能力与缺口如下：

| 能力 | 当前情况 | 对接影响 |
| --- | --- | --- |
| Windows 本地进程读取状态 | 已有 `FocusApp.Service.v2` 命名管道，IPC 版本 2 | 新 Host 可复用 Contracts 客户端 |
| 网站屏蔽 | 本地 HTTP/CONNECT 代理，加当前 Windows 用户的系统代理设置 | 代理端口不是业务 API |
| 普通专注的进行中记录 | WPF 进入 Focusing 时通过 IPC 持久化 | 读取 `state.get` 的 `focusSessions` |
| 强制专注生命周期 | Service 管理、持久化并支持恢复 | 同时读取记录和运行健康状态 |
| 精简、原子化的浏览器状态快照 | **没有** | POC 可组合现有查询；生产版本建议增加只读投影接口 |
| Native Messaging Host、浏览器 Host 注册 | **没有** | 后续需要独立小进程和安装配置 |
| 面向插件的 HTTP/WebSocket API | **没有** | 本文对应地址均为拟议示例 |
| 普通专注准备阶段的服务端状态 | **不完整**，5 秒准备主要属于 WPF 本地状态 | 不能宣称现有 IPC 完整镜像所有 UI 阶段 |
| 精确的统一剩余秒数接口 | **没有**，现有 DTO 提供计划结束时间 | 现阶段只能计算估计值，存在时钟和进程恢复边界 |

安装浏览器插件本身不会安装本地 Host。开发和发布时须另外部署 Host；仅加载插件，不能直接连接命名管道，也不能直接调用 C# DLL。Native Messaging 的进程启动与权限要求见 [Chrome 官方文档](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)。

## 2. 当前网站屏蔽实现

### 2.1 规则从哪里来

1. `BlockingPageViewModel.Websites` 保存界面中的网站规则，单项包含 `Id / Name / Address / IsEnabled`。
2. 添加/编辑网站时调用 `AccessControlService.NormalizeWebsiteHost`。开关或列表变化触发 `BlockingChanged`。
3. `DesktopAccessControlBridge.PersistRulesAsync` 发送 `rules.websites.replace`，负载为 `ReplaceWebsiteRulesCommand`。
4. `ServiceStateCoordinator` 调用 `SqliteLocalDataStore.ReplaceWebsiteRulesAsync` 持久化；规则位于 `website_rules`，包含排序字段 `sort_order`。
5. `state.get` 返回的 `LocalDataSnapshotDto.WebsiteRules` 是当前配置。规则变更会发布 `state.changed`，执行器运行时还会刷新有效规则。

Service 使用已认证的 Windows SID 获取用户协调器。默认数据库位置为：

```text
%ProgramData%\FocusApp\Data\<Windows SID 经 SHA256 后的前24个十六进制字符>\focusapp.db
```

这是持久化实现细节，**插件/Host 不应直接读取数据库**。否则既绕过用户作用域与并发协调，也容易把历史配置误当成当前执行规则。

### 2.2 域名匹配的准确含义

`AccessControlService` 当前执行的是域名黑名单匹配：

- 接受 HTTP/HTTPS 地址或未带协议的域名；忽略路径、查询参数和端口。
- 使用 `Uri.IdnHost` 规范化国际化域名，小写化，并去掉最前面的 `www.`。
- 校验 DNS 域名；当前拒绝 IP、`localhost`、单标签主机、末尾带点及格式错误的域名。
- `example.com` 同时匹配自身及 `a.example.com`，不匹配 `notexample.com` 或 `example.com.evil.test`。
- 未启用规则不生效。多条命中时优先更长的规则域名，再按规则 ID 排序。
- 没有路径级规则、通配符语法或正则规则；也没有白名单优先逻辑、公共后缀特殊处理。

后续 Host 应复用 Core 的规范化函数，输出已验证的 ASCII 域名。不要用 JavaScript 的 `url.includes(address)` 替代；也不要自行把一条含路径的历史规则解释成“仅屏蔽该路径”。

### 2.3 真正的拦截链路

```text
WPF 专注阶段变化 / Service 强制专注生命周期
    → ServiceStateCoordinator 激活访问控制
    → AccessControlExecutionHost 启动 LocalWebsiteProxy
    → Service 请求 Agent prepare/apply 当前用户代理
    → Agent / UserProxyManager 更新 HKCU Internet Settings
    → 遵循该代理设置的浏览器请求进入本地代理
    → AccessControlService.EvaluateWebsite 判断域名
```

具体行为：

- `LocalWebsiteProxy` 监听 `IPAddress.Loopback`，端口由系统动态分配（构造监听器时端口为 `0`）。
- HTTP：解析代理形式的完整 URL；HTTPS：解析 `CONNECT host:port`，在建立隧道前判断域名。
- 命中返回 `403 Forbidden` 并发出 `WebsiteBlocked`；允许的请求直接转发或经已有 HTTP/SOCKS5 上游代理转发。
- 没有安装 HTTPS 中间人证书或解密 TLS 内容，不能分析 HTTPS 页面路径/正文。
- Agent 将 `ProxyEnable` 设为 1，将 HTTP/HTTPS 代理指向 `127.0.0.1:<动态端口>`；保存恢复日志 `%LocalAppData%\FocusApp\proxy-recovery.json`，结束/断线后尝试恢复。
- 当前拒绝覆盖 PAC/自动发现代理；手动 HTTP/SOCKS5 上游有适配逻辑，外部代理更改存在冲突/降级状态。

当前不是 hosts 文件修改、DNS 过滤或浏览器扩展拦截。系统代理只覆盖实际经过它的流量；已建立的 CONNECT 隧道只在建立时检查域名，后续规则变更不会重新解析隧道中的 TLS 请求。自带代理策略、已缓存页面、已有连接等不能仅凭此源码认定全部可拦截。

`AccessControlStatusDto.LocalProxyPort` 仅用于转发。不能向它发送 `GET /api/status`：当前没有这样的路由，不能把这个端口复用为插件 API。

## 3. 专注状态、有效名单和倒计时怎么取

### 3.1 两种专注不能混为一谈

| 项目 | 普通专注 | 强制专注 |
| --- | --- | --- |
| 生命周期所有者 | WPF `FocusSessionViewModel` / Core 计时器 | Service `ServiceStateCoordinator` |
| 开始持久化 | `MainWindowViewModel.FocusSession_PropertyChanged` 在进入 Focusing 时发送 `focus.normal.start` | `StartForcedFocusAsync` 在 Preparing 时保存会话并启动后台生命周期 |
| 准备时间 | Core 定义 5 秒；当前普通准备态不通过上述启动请求持久化 | Preparing 记录已保存，包含计划开始/结束时间 |
| 进行中状态来源 | `state.get.focusSessions` 的非强制 Focusing 记录 | 会话记录为主，并结合 `focus.runtime-status.get` |
| 有效网站名单 | 当前 `state.websiteRules` 的启用规则，修改时可刷新执行器 | **会话 `websiteRuleSnapshots` 的启用规则** |
| 结束 | 正常完成保存 Completed；放弃可通过 `focus.normal.discard` 删除进行中记录 | Service 完成、保存、停止保护并发布运行状态 |
| Service 重启 | 不会按强制模式自动恢复，可能报告 Faulted | 会从会话快照恢复，或完成已经到期的会话 |

必须注意三处已确认的差异：

1. **`focus.runtime-status.get` 不是通用的 UI 专注状态接口。** 普通专注启动只持久化会话，并不把 `_focusRuntimeStatus` 改成 Focusing；它可能仍是 Idle。Service 重启遇到未结束的普通会话还会报告 Faulted。因此，不能用 `runtime.state == idle` 判断用户一定未专注。
2. **普通专注保存的 `BlockingEnabled` 当前固定传 `false`。** 实际激活网站保护的是 `DesktopAccessControlBridge`，它另发 `access-control.activate`。不能据此字段把普通专注的屏蔽关闭。强制专注的该字段和启动规则快照才有对应语义。
3. **强制专注期间不要退回全局名单。** `StartForcedFocusAsync` 保存规则快照，`RefreshActiveExecutorsAsync` 继续使用该快照。空快照不代表可以借用当前全局规则；若会话声称有屏蔽但必需快照缺失，应报告数据异常。

`FocusRuntimeStatusDto.State == Faulted` 也不必然代表计时已停止：`PublishFocusingStatus` 可在强制专注仍进行时因访问控制错误发布 Faulted。插件应分别展示“专注状态”和“保护健康状态”。

### 3.2 建议的读取与投影顺序（后续 Host 实现）

1. 以与浏览器用户相同的 Windows 身份连接 Named Pipe。
2. 注册事件处理器，再读取 `system.ping`、`state.get`、`access-control.status.get`、`focus.runtime-status.get`。
3. 在会话列表中查找 Preparing/Focusing 记录；排除 Completed。时间字段失效、重复活动会话或字段矛盾时返回 `unknown`/明确错误，不任意选第一条。
4. 普通会话计划结束已过时，不继续当作有效专注；现有过期普通记录清理并不是每次 `state.get` 都执行。强制会话已到期但还在收尾时，剩余时间为 0，并等待后台完成/重取状态。
5. 根据是否强制选择对应规则来源；只导出启用且规范化有效的域名，域名相同时合并匹配条件。
6. 读取访问控制状态；`WebsiteProtectionActive` 表示 Service 报告的执行器状态，**不等同于浏览器插件已经安装规则，更不保证每个浏览器请求都被系统代理覆盖**。

自动计划不应由插件再次计算或启动。当前会话可携带 `AutomaticRuleId`；插件消费实际会话和保护状态即可。

### 3.3 剩余专注时间的精度限制

现有 IPC 没有 `remainingSeconds` 或 Service 的“有效当前时间”字段。对 Focusing 会话可估算：

```text
remainingSeconds = clamp(ceil((PlannedEndAtUtc - UtcNow).TotalSeconds), 0, ConfiguredSeconds)
```

强制 Preparing 时正式专注的剩余时间显示 `ConfiguredSeconds`，不把准备的 5 秒计入专注时长。没有会话且状态确定为空闲时为 0；会话/时间未知时为 `null`，不要显示成“已结束”。

限制必须暴露给插件：

- 普通专注 UI 使用 Core 自身推进的计时数据，计划结束估算不保证与 UI 每秒完全一致。
- Service 的强制计时通过 `GetEffectiveUtcNow()` 加单调时钟补偿系统时间回拨。外部 Host 直接读取 `UtcNow` 不能完全复制这一时基。
- 现有 `system.ping` 仅返回服务实例、服务启动时间和协议版本，不返回 ServerNow；不能拿启动时间校准倒计时。
- Service 中查不到普通准备态或未成功持久化的普通会话时，不能推断它们不存在于 WPF。当前方案是服务可见状态投影，并非全部 UI 状态镜像。

POC 应标注 `remainingAccuracy: "estimatedUtc"`。收到采样值后插件可用 `performance.now()` 推进展示，在新快照、系统唤醒、明显时钟跳变时重新同步。浏览器重启后不要沿用旧的单调时钟基点。

如上线要求严格复现强制计时，应在后续阶段增加 Service 只读投影，由其有效时钟计算剩余秒数；普通专注若要求逐秒严格一致，还需要其 WPF 计时所有者提供只读状态。**这些能力目前都未实现。**

## 4. 三种可行的本地通信方案

下表是基于本仓库已有 Named Pipe 架构的工程评估，不是已有功能列表。

| 方案 | 新增部分 | 实现难度 | 稳定性/同步特点 | 维护成本 |
| --- | --- | --- | --- | --- |
| **A. Native Messaging + 现有 Named Pipe（推荐）** | C# Host、浏览器 Host manifest 和注册项、扩展后台 | 中等；可复用现有 IPC 客户端 | 推送，不占 TCP 端口；浏览器按连接启动 Host。须处理 Host/Service 断线、浏览器生命周期和安装配置 | 中等；重点维护 Host 部署、扩展 ID 与协议版本 |
| B. Loopback HTTP API + 轮询 | 用户态 HTTP Bridge，内部连同一管道；配对认证 | 原型较低，生产认证/多用户处理为中等 | 易手工调试；轮询有延迟，MV3 后台休眠会影响短周期轮询。还需处理端口冲突和代理环境 | 中等；长期维护 API、令牌、端口发现及浏览器网络权限 |
| C. Loopback WebSocket + 全量快照 | 用户态 WebSocket Bridge，内部连同一管道；握手认证 | 中高 | 实时推送；需要心跳、重连、睡眠恢复。Chrome 的活跃 WebSocket 生命周期支持有版本条件 | 中高；除协议外还有网络监听、认证和后台存活问题 |

三种方案都应接 Service，避免把新服务仅挂在某个 WPF 弹窗/窗口生命周期中。直接由 WPF 提供接口虽能读取普通准备态与本地秒数，但关闭桌面进程后接口会消失，而强制专注仍可能由 Service 继续运行。

### 4.1 为什么选 A

- 当前已经是 WPF、Agent、Service 分工，Contracts 已提供双向异步管道客户端；Host 只需做协议适配和最小数据投影。
- 通过现有 Windows SID 分区拿到同一用户状态，不额外设计 HTTP 端口到用户的映射。
- 浏览器扩展不碰系统代理配置，不抢 Agent 的代理所有权，不改变现有拦截链路。
- 无需暴露新的本地 TCP 管理端口，避免 HTTP 配对及端口发现成本。
- `connectNative` 长连接适合状态推送。Chrome 对 Native Messaging 与 MV3 后台存活的规则见 [Service Worker 生命周期](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle)。仍须处理断线，不能假设进程永不退出。

边界：插件与 Host 是两个发布物；Chrome 和 Edge 的安装来源可能对应不同扩展 ID。应分别配置注册项，并把被允许的正式扩展 ID 写入 Host manifest。参见 [Edge Native Messaging 部署说明](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)。

### 4.2 如果选择 HTTP 或 WebSocket

以下地址是**拟议示例，当前不存在**，`17873` 不是现有代理端口，也不是承诺的产品端口。

```http
GET http://127.0.0.1:17873/v1/browser/snapshot
Authorization: Bearer <按用户配对获得的随机令牌>
Accept: application/json
```

返回第 6 节的同一快照；可加 `GET /v1/health`，但不返回历史任务、数据库路径等信息。HTTP Bridge 只监听 `127.0.0.1`，固定开发端口冲突必须明确报错；生产需处理多个 Windows 用户同时登录的端口分配/发现，不能让后登录用户连到前一用户的状态。

扩展从 Service Worker `fetch`，声明 `http://127.0.0.1/*` 的 `host_permissions`，配对令牌不能放在网页 Content Script 或 URL 参数中。权限/CORS 不能代替 API 认证；参见 [Chrome 跨域请求规则](https://developer.chrome.com/docs/extensions/develop/concepts/network-requests)。

WebSocket 可使用拟议的 `ws://127.0.0.1:17873/v1/browser/events`。浏览器的 WebSocket 构造器不能任意设置 Authorization 请求头，建议连接后第一帧发送令牌，认证成功前不发任何状态，并在 3 秒未认证时关闭。不要把令牌拼到查询字符串。活跃连接可用 20 秒心跳检测失联，需单独验证 Chrome/Edge 最低版本及后台行为。

两者都要求：限定 Host/Origin，随机令牌和配对流程，拒绝无认证读取，禁止 `Access-Control-Allow-Origin: *`，请求大小/频率限制，`Cache-Control: no-store`，以及 DNS rebinding 防护。Origin 可作为额外校验，但不是身份证明；本地非浏览器进程能够伪造它。

## 5. 可复用的现有 Service IPC

### 5.1 传输与身份

```text
管道逻辑名：FocusApp.Service.v2
Windows 路径：\\.\pipe\FocusApp.Service.v2
帧：4 字节小端长度（UTF-8 字节数）+ JSON 正文
现有帧上限：1,048,576 字节
现有协议版本：2
JSON：camelCase 属性；枚举 camelCase 字符串；区分属性名大小写
```

`NamedPipeServiceWorker` 拒绝 Network SID，允许本地已认证用户；通过 `RunAsClient` 获取真实 Windows SID，`UserStateCoordinatorProvider` 按此 SID 隔离数据。不要让浏览器传入一个 SID 来选择数据库。

当前角色只有 `desktop`、`agent`，没有 `browser`。Agent 被限制不能修改核心业务数据，但仍可发代理结果等执行类命令；**不是通用只读角色**。而且 Agent 身份读取 `access-control.status.get` 可能触发强制专注保护修复。

POC Host 可以用现有 `IpcClientRole.Desktop`，但其实现必须硬编码只允许下表的四个查询，不能把浏览器提供的 operation 直接透传。这样访问控制状态查询不会进入仅针对 Agent 的修复分支。首次任何 IPC 请求仍可能触发现有初始化/强制会话恢复，这是当前 Service 的行为，不是新 Host 主动发送激活命令。

生产建议新增 `BrowserObserver` 角色及服务端只读白名单；这是后续改动，不是当前可用角色。现有 `ClientRole` 是客户端声明值，SID 才由系统验证；增加枚举本身不能防御同 SID 恶意进程伪装成 Desktop，不能声称现有管道已验证客户端程序签名。

### 5.2 现有操作及用途

| 已实现 operation | 请求 payload | 返回 DTO / 主要字段 | 用途 |
| --- | --- | --- | --- |
| `system.ping` | `{}` | `PingResponse`：`serviceInstanceId / serviceStartedAtUtc / protocolVersion` | 连接与版本检查、发现 Service 重启 |
| `state.get` | `{}` | `LocalDataSnapshotDto`：`revision / focusSessions / websiteRules`，以及任务等完整业务数据 | 活动会话、全局名单、强制会话规则快照 |
| `access-control.status.get` | `{}` | `AccessControlStatusDto`：`state / websiteProtectionActive / applicationProtectionActive / localProxyPort / expiresAtUtc / lastError` | 保护执行状态、期限和故障 |
| `focus.runtime-status.get` | `{}` | `FocusRuntimeStatusDto`：`state / sessionId / plannedEndAtUtc / lastError` | 强制生命周期及健康信息，不能代替普通会话查询 |

Host 应监听的已实现事件：

- `state.changed`：`{ revision, state }`，完整业务快照。
- `focus.runtime-state-changed`：`{ status }`。
- `access-control.state-changed`：`{ status }`。

`access-control.blocked` 是已有系统拦截通知事件，可用于后续诊断；它不是插件拦截事件。MVP 无需转发，更不要把插件命中伪装为已有 Service 事件。Host 必须忽略 `agent.proxy-action.requested` 和启动注册请求，不能冒充 Agent 应答。

### 5.3 真实 IPC 格式示例

以下是现有协议格式示例，ID 为演示值；JSON 前仍须加长度帧，不能用逐行 JSON 代替。

```json
{
  "kind": "request",
  "protocolVersion": 2,
  "requestId": "11111111-1111-4111-8111-111111111111",
  "clientRole": "desktop",
  "operation": "focus.runtime-status.get",
  "payload": {},
  "error": null,
  "sequence": 0
}
```

```json
{
  "kind": "response",
  "protocolVersion": 2,
  "requestId": "11111111-1111-4111-8111-111111111111",
  "clientRole": null,
  "operation": "focus.runtime-status.get",
  "payload": {
    "state": "idle",
    "sessionId": null,
    "plannedEndAtUtc": null,
    "lastError": null
  },
  "error": null,
  "sequence": 0
}
```

这个 Idle 响应并不排除 `state.get.focusSessions` 中存在普通 Focusing 记录。名单 DTO 的实际结构为：

```json
{
  "id": "22222222-2222-4222-8222-222222222222",
  "name": "示例网站",
  "address": "example.com",
  "isEnabled": true,
  "sortOrder": 0
}
```

下面的 C# 片段只演示复用**现有**读取 API，不是一个已存在的 Native Host，也没有实现第 6 节的浏览器协议：

```csharp
using FocusApp.Contracts;

static async Task ReadCurrentServiceDataAsync(CancellationToken ct)
{
    await using var client = new NamedPipeIpcClient(IpcClientRole.Desktop);
    // 实际 Host 应在这里先注册 EventReceived / Disconnected，缓冲初始化期间的失效信号。
    await client.ConnectAsync(TimeSpan.FromSeconds(3), ct);
    var timeout = TimeSpan.FromSeconds(5);

    var ping = await client.SendAsync<EmptyPayload, PingResponse>(
        IpcOperations.Ping, new EmptyPayload(), timeout, ct);
    var data = await client.SendAsync<EmptyPayload, LocalDataSnapshotDto>(
        IpcOperations.GetState, new EmptyPayload(), timeout, ct);
    var protection = await client.SendAsync<EmptyPayload, AccessControlStatusDto>(
        IpcOperations.GetAccessControlStatus, new EmptyPayload(), timeout, ct);
    var runtime = await client.SendAsync<EmptyPayload, FocusRuntimeStatusDto>(
        IpcOperations.GetFocusRuntimeStatus, new EmptyPayload(), timeout, ct);

    // 后续新增：按本文投影、过滤隐私数据，再通过 Native Messaging 返回。
    // 不要将 data（包含历史任务、描述等）直接发给扩展。
}
```

每次重新查询必须使用新的 RequestId。当前 Service 有按 RequestId 缓存响应的机制，重复使用固定 ID 可能得到旧结果；客户端默认自动创建新 GUID。

### 5.4 现有接口不足以直接作为最终插件契约

- `state.get` 包含全部历史记录、任务和设置，体积可能超过当前 1 MiB 帧上限；它没有分页或按字段投影参数。
- 三个状态读取不是同一时刻的原子快照；普通持久化与保护激活本来就是不同请求。
- `revision` 只覆盖持久化状态变更，不能代表所有运行状态；协调器重建后从初始值开始，不能跨 Service 生命周期永久比较。
- envelope 的 `sequence` 是**每条连接**的事件序号，重连后重新计数；不是持久化、可重放的事件日志。

POC 可把事件作为失效通知，串行重取状态，并在有并发变化时重试；不能承诺完全原子。大量历史数据、严格倒计时或生产级一致性要求下，建议后续在 Service 增加只读的 **`browser.snapshot.get`（拟议、未实现）**：仅返回活动会话、有效域名、保护状态和时基，使用已认证 SID，复用业务状态且不执行新的屏蔽指令。该聚合快照需在现有协调逻辑中取得一致数据并审查锁顺序，不能用扩大消息上限代替数据最小化。

## 6. 拟议的插件协议 v1（全部尚未实现）

本节用于独立插件与未来 Host 共同实现。它与内部 IPC v2 是两套契约；不能向当前 Service 直接发送这些方法。

### 6.1 请求、响应与推送

| 拟议方法 | 行为 |
| --- | --- |
| `hello` | 协商外部协议版本及能力，如 `snapshotSubscription / consistency / remainingAccuracy` |
| `snapshot.get` | 返回当前全量精简快照 |
| `snapshot.subscribe` | 开始订阅；响应中包含首次全量快照，随后推送变更 |
| `snapshot.unsubscribe` | 停止当前连接订阅 |

不提供开始/结束专注、修改规则、代理操作或任意 IPC 转发方法。请求 ID 由插件生成，必须原样关联响应；未知方法返回错误。

```json
{
  "version": 1,
  "type": "request",
  "id": "request-17",
  "method": "snapshot.subscribe",
  "params": {}
}
```

成功响应形状为 `{ "version": 1, "type": "response", "id": "request-17", "result": SNAPSHOT, "error": null }`。后续推送形状为 `{ "version": 1, "type": "event", "event": "snapshot.changed", "data": SNAPSHOT }`。

### 6.2 完整快照示例

以下所有值均为演示数据，`observedAtUtc` 是拟新增 Host 的采样时间，不是现有 Service 新增字段：

```json
{
  "epoch": "33333333-3333-4333-8333-333333333333",
  "sequence": 17,
  "serviceInstanceId": "example-service-instance",
  "sourceRevision": "42",
  "observedAtUtc": "2026-10-17T12:00:00Z",
  "leaseExpiresAtUtc": "2026-10-17T12:00:30Z",
  "consistency": "sampled",
  "focus": {
    "state": "focusing",
    "sessionId": "44444444-4444-4444-8444-444444444444",
    "isForcedMode": true,
    "isAutomatic": false,
    "configuredSeconds": 1800,
    "plannedEndAtUtc": "2026-10-17T12:25:00Z",
    "remainingSeconds": 1500,
    "remainingAccuracy": "estimatedUtc"
  },
  "websitePolicy": {
    "enforcementMode": "mirrorReportedProtection",
    "shouldBlock": true,
    "reason": "reportedProtectionActive",
    "ruleSource": "sessionSnapshot",
    "scope": "mainFrame",
    "rules": [
      {
        "id": "22222222-2222-4222-8222-222222222222",
        "domain": "example.com",
        "includeSubdomains": true
      }
    ]
  },
  "desktopProtection": {
    "state": "active",
    "websiteProtectionActive": true,
    "expiresAtUtc": "2026-10-17T12:25:00Z",
    "errorCode": null
  }
}
```

字段约定：

| 字段 | 含义/约束 |
| --- | --- |
| `epoch` | 每次 Host 重新连接 Service 时新生成的标识；Service 或 Host 重启/重新同步后变化 |
| `sequence` | 本 epoch 内 Host 发布快照的递增安全整数；不是内部 IPC 的 sequence |
| `sourceRevision` | 源业务 revision，仅诊断；用十进制字符串避免 JavaScript 64 位整数精度问题 |
| `consistency` | POC 为 `sampled`；只有实现一致聚合读取后才可宣称 `atomic` |
| `focus.state` | 外部统一值 `idle / preparing / focusing / completing / unknown`；保护错误不直接映射成 idle |
| `remainingSeconds` | 非负整数或 `null`；Preparing 为配置时长；未知不可伪造为 0 |
| `remainingAccuracy` | 当前 POC 只能为 `estimatedUtc`；未来权威有效时基可新增明确能力值 |
| `ruleSource` | `current` 或 `sessionSnapshot`；只发送当前应使用的那套启用域名 |
| `shouldBlock` | Host 明确投影的插件执行决策；扩展不重新猜测普通/强制语义 |
| `scope` | MVP 为 `mainFrame`；不能宣称等同于系统代理的全部网络覆盖 |
| `leaseExpiresAtUtc` | 协议有效期，用于失联检测；不是浏览器 DNR 自带的过期时间 |

`desktopProtection` 是桌面侧诊断；插件自身是否成功安装 DNR 规则应另存本地状态，例如 `rulesAppliedSequence / lastApplyError`。不得用前者冒充后者。插件不需要 `localProxyPort`、任务、目标描述或完整设置，因此外部快照不包含这些字段。

### 6.3 推荐 MVP 的屏蔽决策

为避免对现有行为做隐式改变，MVP 默认 **`mirrorReportedProtection`：跟随 Service 报告的网站保护状态**：

```text
shouldBlock = 快照有效且规则来源已确定且有效域名非空
              且 access.state 属于 active / partiallyActive
              且 access.websiteProtectionActive == true
              且 access.expiresAtUtc 有效且尚未到期
              且已知活动会话不是 preparing
```

- 普通专注的 `BlockingEnabled=false` 不参与这个判断。
- 没有活动专注记录但访问控制明确仍有效时，可跟随访问控制保护，`reason` 标为 `accessControlOnly`；专注状态/专注剩余时间标为未知，不凭保护期限编造专注会话。
- `inactive / deactivating` 不启用插件规则。`activating / proxyConflict / faulted` 作为过渡/故障处理，不能据此显示“保护正常”；无可验证的新决策时按第 8 节的短暂失效策略处理旧规则。
- 空启用名单、准备中、数据不一致时不新装屏蔽规则。会话结束或保护期限到期后清理插件自有规则。

这个默认值不是“用插件替代系统代理”的完整设计。若后续目标是代理失效时插件仍按专注意图独立保护，应新增明确的 **`focusIntent` 模式（拟议）**，根据活动会话及有效名单决策，不能仍以 `WebsiteProtectionActive` 为前提。该模式需特别定义普通专注持久化失败、普通会话残留、主动取消、强制恢复故障时的行为，并单独验收。不要未经定义就把“配置启用”“专注进行中”和“保护实际生效”视为同一个布尔值。

### 6.4 Host 传输要求与示例配置

拟新增 Host 可以引用本仓库 Contracts/Core，但不需要创建 WPF 窗口。Native Messaging 使用标准输入输出；Windows 的长度前缀同样为 4 字节小端，但 JSON 正文是本节协议，**不能直接用只认识 `IpcEnvelope` 的 `IpcFrameCodec` 解析它**。两端各有自己的解码、校验和适配层。

要求：字节流精确读取、严格帧长度检查、UTF-8、串行写 stdout 防止响应/事件交错、断管释放连接、日志写 stderr/文件。建议本协议把双向消息上限进一步限制到 256 KiB，超限返回明确错误，不截断域名。Native Messaging 本身的格式/限制见 [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)。

拟议 Host manifest（不是插件 manifest）：

```json
{
  "name": "com.focusapp.browser_bridge",
  "description": "FocusApp read-only browser bridge",
  "path": "C:\\Program Files\\FocusApp\\BrowserBridge\\FocusApp.BrowserBridge.exe",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://<CHROME_EXTENSION_ID>/",
    "chrome-extension://<EDGE_EXTENSION_ID>/"
  ]
}
```

占位符必须替换成实际正式/开发扩展 ID，不能使用通配符。Host 还应校验启动参数中的调用方 origin，只接受配置中相同的允许来源；Windows 的 `--parent-window` 参数不应被误当成 origin。

后续安装器可写入以下两个注册项的默认字符串值，指向该 manifest 的绝对路径。本次没有创建它们：

```text
HKCU\Software\Google\Chrome\NativeMessagingHosts\com.focusapp.browser_bridge
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.focusapp.browser_bridge
```

按用户安装时 Host 应以该用户权限运行，不以管理员/LocalSystem 运行去读取别人的状态。选择机器级安装时，须由安装器保护程序和配置文件，并验证 Chrome/Edge 的注册表视图及实际安装来源；不同商店/开发加载 ID 的处理见 [Edge 官方说明](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)。

## 7. 插件端最小实现边界（后续实现）

### 7.1 Manifest V3 权限示例

下面是拟议的最小配置，不是本仓库已存在的插件文件。Chrome 示例最低版本设为 120，Edge 仍需在支持的最低目标版本上独立联调：

```json
{
  "manifest_version": 3,
  "name": "FocusApp Browser Integration",
  "version": "0.1.0",
  "minimum_chrome_version": "120",
  "background": { "service_worker": "service-worker.js" },
  "permissions": [
    "nativeMessaging",
    "declarativeNetRequest",
    "storage",
    "alarms"
  ]
}
```

Native Messaging 不需要新增 localhost 网络权限。MVP 使用 DNR `block`，不读取网页内容；若未来改成自定义拦截页重定向，还需相应 host 权限及 `web_accessible_resources`，不可直接沿用这个最小 manifest。DNR 的 block 权限、规则与域名条件见 [Chrome DNR 文档](https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest)。

### 7.2 规则映射示例

原始域名始终先由 Host 校验/规范化。MVP 把同一有效策略中的域名合并为一条插件自有 session rule；规则 ID `10001` 是本方案的保留示例，不是桌面规则 GUID。

```json
{
  "id": 10001,
  "priority": 1,
  "action": { "type": "block" },
  "condition": {
    "requestDomains": ["example.com"],
    "regexFilter": "^https?://",
    "resourceTypes": ["main_frame"]
  }
}
```

使用 `chrome.declarativeNetRequest.updateSessionRules` 同次移除旧自有规则并安装新规则；`shouldBlock=false` 时只移除。空域名列表不能生成一条无条件规则。串行应用快照，成功后才记录 `rulesAppliedSequence`；API 拒绝、权限不足、超限时必须显示插件保护失败，不能继续声称已同步成功。

选择 session rules 是为了减少跨浏览器重启遗留屏蔽：它们在浏览器退出/扩展更新时清理，动态规则则会持久化。域名条件支持子域与 Punycode。相关行为见 [DNR session rules 与条件](https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest)。

该 MVP 只阻止新的 HTTP/HTTPS 顶层导航，**不会自动移走已经打开的页面，也不等价于所有 iframe、XHR、下载、WebSocket 或缓存内容均被封锁**。网络栈以外由站点 Service Worker/CacheStorage 返回的内容也有边界。需要扩大覆盖时应另定资源类型、已打开标签处理及相应权限，不把它们藏在“域名同步”里。

### 7.3 插件状态与界面数据

- Service Worker 负责唯一 Native 连接、快照校验及 DNR 更新；浏览器弹窗关闭不应断开后台订阅。
- 扩展 UI 从后台拿当前精简展示状态及估计秒数，不让每个 UI 自建 Host。
- 最新有效快照、epoch/sequence 和应用结果可存 `chrome.storage.session`；它可跨后台休眠保留，浏览器重启后必须重新获取。不要把有效规则云同步到别的机器/用户。参见 [Chrome storage](https://developer.chrome.com/docs/extensions/reference/api/storage)。
- MVP 的 DNR 条件并不回传哪个域名 GUID 命中；如以后显示命中规则名称，应按 Core 的最具体域名规则处理，不能把聚合规则 ID 当成原规则 ID。

## 8. 状态同步、异常和安全约束

以下数值是拟议的 MVP 默认值，可在后续压力测试后调整，不是现有 Service 新配置。

### 8.1 启动、更新与重连

1. 扩展后台在启动时检查旧自有规则及缓存有效期；先处理过期规则，再建立唯一的 `connectNative` 连接。
2. `hello` 协商版本；成功后 `snapshot.subscribe`，响应携带首份全量快照，后续仍发送全量精简快照，避免插件实现差量补丁。
3. Host 先监听内部事件，再进行初始化查询。事件只入队、标记失效，约 100 ms 合并一次读取；每次最多一项投影任务，不让连续任务变更造成无限队列。
4. **内部 `EventReceived` 在 NamedPipeIpcClient 的读取循环中同步调用。处理器不能 `.Wait()`/`.Result` 等待同一连接的新响应，否则会阻塞读取循环；应入队给独立异步任务。**
5. 初始化/读取期间再次发生变更则重取；POC 最多连续重试 3 次，仍不稳定就明确报告 `SYNC_IN_PROGRESS`，不要冒充原子一致。后续聚合接口才能从根本上改进这一点。
6. 拟定每 5 秒进行一次快照校准，快照租期 30 秒；正常事件无需等待该周期。POC 全量 `state.get` 只适合小数据，禁止每秒读取全部历史；生产换为精简投影。
7. Host/Service 断线立即标记 disconnected。连接重试使用 1、2、4、8、15 秒上限并加少量抖动；Native `port.onDisconnect` 读取 `chrome.runtime.lastError`，重新创建 Host，避免紧密循环。
8. 重连产生新 epoch、重取全量快照，不依赖旧事件补发。仅在同 epoch 内比较 sequence；忽略旧包。序号缺口、状态矛盾或 Service 实例变化触发全量重同步。

`state.changed.revision` 可去重业务快照，但不能用于判断运行状态事件是否过期。初始化期间的推送可能先于查询响应到达，插件必须避免较早的初始响应覆盖更晚的推送。Host 在自己的单一输出队列中分配 sequence，统一序列化输出。

### 8.2 断线及到期策略

推荐 MVP 使用有界失效策略：

- 首次没有拿到有效快照：显示“未连接/未同步”，不新装屏蔽规则。
- 已有规则后失联：保留最多 10 秒，且不超过原策略期限及 30 秒租期；随后清除插件自有规则，状态仍显示“未连接”。这是插件的 fail-open 决策，不会终止桌面专注或停掉 Service 代理。
- 倒计时到 0：显示 0 并触发重同步/到期清理，不在插件自行提交完成记录。
- 休眠/唤醒、浏览器后台重建、时钟跳变：重新检查有效期并同步，不连续重放丢失的计时 tick。
- 强制专注若产品要求失联后也必须保留插件屏蔽，须显式定义另一种有截止时间的 fail-closed 策略并验收；本文不把它当作已存在行为。

**DNR 规则本身没有本文租期的自动过期机制。** 清理依赖扩展后台实际调用删除规则。可用 Native 消息、结束时间检查及 `chrome.alarms` 做冗余触发，但闹钟可能延迟、睡眠不会唤醒机器，也不能保证秒级截止。后台异常终止时，规则可能留到后台再次运行或浏览器退出。因此上述 10/30 秒是实现目标，不是浏览器提供的严格实时保证。参见 [Chrome alarms 限制](https://developer.chrome.com/docs/extensions/reference/api/alarms) 和 [后台生命周期](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle)。

### 8.3 错误格式与处理

拟议错误响应：

```json
{
  "version": 1,
  "type": "response",
  "id": "request-17",
  "result": null,
  "error": {
    "code": "SERVICE_UNAVAILABLE",
    "message": "暂时无法连接本地后台服务",
    "retryable": true
  }
}
```

| 错误/情况 | 建议处理 |
| --- | --- |
| Host 未安装、origin 不允许、Host 无法启动 | 显示对应安装/授权信息；不要统称“专注已结束” |
| `SERVICE_UNAVAILABLE / TIMEOUT` | 进入 disconnected，按退避重连；沿用有界失效策略 |
| `PROTOCOL_UNSUPPORTED` | 停止持续重试，提示升级插件/Host/桌面端；两层协议版本分开诊断 |
| `INVALID_MESSAGE / METHOD_NOT_ALLOWED` | 拒绝当前请求；重复畸形输入可关闭连接 |
| `SYNC_IN_PROGRESS / SNAPSHOT_INCONSISTENT` | 不发布“已同步”；重取，必要时等待稳定快照 |
| `INVALID_RULE_SNAPSHOT / INVALID_RULE` | 报告规则来源/规范化失败；不得用全局规则替代损坏的强制快照 |
| `PAYLOAD_TOO_LARGE / RULESET_TOO_LARGE` | 不截断名单、不装一半规则；提示精简/分包协议需求，保持故障状态 |
| DNR 更新失败 | 保存本地应用错误与尚在生效的旧规则版本；恢复/清理也须检查 API 结果 |

内部已有 `IpcErrorCode` 与超时/断线异常类型，Host 可映射为上述外部代码。内部完整快照超出帧上限时，不保证能收到一个可读的超限响应；应结合服务日志诊断，不能把每次超时都解释成“Service 未安装”。

日志建议包含 UTC 时间、操作、请求 ID、epoch/sequence、Service 实例、规则数量、同步/安装耗时及异常堆栈。Host 使用轮转文件或 stderr，stdout 只写协议帧。默认不记录访问 URL 路径/查询、令牌、任务内容或完整名单；面向扩展的错误不透传本地路径/原始异常堆栈。

### 8.4 安全边界

- Native manifest 只允许明确扩展 ID，Host 再校验调用 origin；正式、开发 ID 应分别管理。保护 Host 可执行文件与 manifest 的写权限，避免被替换。
- 每个 Host 使用调用用户的 Windows token 连接管道，不接受外部 SID、数据库路径、程序路径或任意命令。
- 浏览器协议仅允许读取/订阅。即使 POC 内部用了 Desktop 角色，Host 也不得暴露 `rules.*.replace`、`focus.*.start`、`access-control.*` 写命令，或代答 Agent 的代理操作。
- Native 权限只放在可信扩展后台。若开放 UI 消息，需要验证 sender 的扩展页面 URL；Content Script 的 sender.id 同样可能是本扩展 ID，不能只检查这一项。不要启用任意网页的 `externally_connectable`，不要提供任意 URL 代理访问能力。
- 网站名单只给后台执行器；网页 Content Script 或 web-accessible 拦截页不应得到全部状态。展示来自规则的文本时使用 textContent，不插入未经处理的 HTML。
- 后续 Host 对外限制为 256 KiB、最多 1000 个规范化域名和每连接每秒最多 10 次显式读取请求，超限明确失败；这些是方案限制，不是当前产品限制。
- 同一 Windows 用户的多个 Chrome/Edge Profile 默认读取同一份 FocusApp 用户策略，不存在现成的浏览器 Profile 隔离。隐私模式是否允许扩展运行需另行验证，未获允许时不能承诺覆盖。
- 扩展可被禁用或卸载；本方案不是不可绕过的浏览器管控。它不扩大或替代当前强制模式的系统防护保证。

## 9. 最小可行对接与测试步骤

以下为**下一实施阶段**的步骤。本次仅代码审计和文档校验，未创建这些项目、未注册 Host、未运行真实浏览器联调。

### 步骤 1：建立只读数据验证程序

在独立 Host 项目中引用相同版本的 Contracts/Core，按第 5 节连接现有 Service。只调用四个查询并订阅三个状态事件。使用测试用户配置 `example.com` 与一个禁用规则，打印到 stderr 的精简投影；不要读/改用户数据库或发送激活/结束指令。

首先验证：普通专注时 runtime 可能 Idle，但持久化会话为 Focusing；普通 `BlockingEnabled=false` 仍可能有网站保护；强制模式使用会话快照。若这三条未核对，不进入规则拦截开发。

### 步骤 2：实现 Host 外部协议并本地注册

实现 v1 hello/get/subscribe/unsubscribe、帧限制、只读白名单、重连和全量精简快照。分别配置 Chrome/Edge 开发 ID，写入用户级 Host 注册项。先只展示状态，不安装 DNR，核对 5 秒准备期、开始/结束、名单开关、估计时间与错误提示。

数据体积过大或精确时基要求无法满足时，先补后续只读聚合接口，不能继续声称 POC 的全量采样可以无条件用于生产。

### 步骤 3：在扩展后台接入 DNR

在确认快照有效后安装自有 session rule。普通变更/结束时重算或清理；强制会话不受本次启动以后全局名单变化影响。只验证 HTTP/HTTPS 顶层导航的 MVP 范围，不把已打开页面是否还能操作当成接口调用失败。

### 步骤 4：Chrome 与 Edge 各自执行验收矩阵

| 测试 | 期望结果 |
| --- | --- |
| 全部启用网站为空 | 插件规则为空；界面不得声称有有效网站屏蔽 |
| 普通专注进入 Focusing | 能从持久化会话识别；保护激活后按当前名单拦截，不读取 BlockingEnabled 判定 |
| 普通专注不足阈值放弃/提前结束/自然结束 | 持久化与保护状态同步后规则清理；历史 Completed 不重新激活 |
| 普通专注准备的 5 秒 | 不宣称从现有 Service 看到了完整普通准备态；不提前安装规则 |
| 强制专注准备、正式开始、完成 | 准备中不保护；开始后使用快照；结束清理；插件无结束强制专注权限 |
| 强制专注期间全局名单变化 | 有效名单仍来自该会话快照，不能切换成全局列表 |
| 规则 example.com、子域与相似域 | 自身/子域命中；notexample.com、example.com.evil.test 不命中 |
| 大小写、www、IDN、端口、路径 | 与 Core 的域名规范化一致；禁用/非法域名不装规则 |
| DNS 尾点、IP、localhost、非 HTTP(S) | 验证浏览器规范化差异并记录结果；不得无依据扩大 Core 支持范围 |
| 代理只有应用生效、PAC 冲突、ProxyConflict、Faulted | 插件显示真实故障/降级；mirror 模式不擅自按专注意图激活 |
| 已打开页面、XHR/iframe、站点离线缓存 | 记录 MVP 覆盖之外的行为，不能报告成全部网站访问已受控 |
| Host 退出、Service 停止/重启、代理 Agent 不在线 | 可见断线与退避；有界处理旧规则；重连新 epoch，拒绝旧快照覆盖 |
| 普通会话残留后 Service 重启 | 不按强制模式恢复；显示恢复/状态边界，不仅凭未来 plannedEnd 判定保护有效 |
| 强制会话恢复，含缺失规则快照 | 正常恢复仍使用快照；缺失时明确报错，不能回退全局规则 |
| 系统回拨/快进时间、睡眠唤醒 | 重新同步；明确估计时间限制，正常结束后无永久遗留规则 |
| 浏览器重启、扩展后台重建/更新 | 重建连接及校验缓存，不因丢失全局变量造成永久旧策略 |
| 错误 ID/origin、版本不兼容、畸形/超长帧、未知方法 | 明确拒绝，禁止写操作；不泄漏完整业务数据 |
| 两个 Windows 用户、两个浏览器/多个 Profile | Windows 用户相互隔离；同一用户的多连接不抢 Agent 代理动作 |
| 超过 1 MiB 历史快照、1000+ 域名、DNR API 拒绝 | 可观测失败；无静默截断、部分安装或假成功 |

正常变更推送到规则生效可设定“测试机常规负载下目标 1 秒内”的验收指标；记录延迟分布，不把调试机结果当成平台实时保证。分别测正常事件通路和校准/断线通路。

### 步骤 5：复用项目现有测试作为回归基础

后续实施可先运行下列现有测试，再增加 Host 协议与真实浏览器集成测试：

```powershell
dotnet test tests/FocusApp.Tests/FocusApp.Tests.csproj --filter "FullyQualifiedName~AccessControlServiceTests|FullyQualifiedName~AccessControlExecutionTests|FullyQualifiedName~AccessControlCoordinatorTests|FullyQualifiedName~ForcedFocusRecoveryTests|FullyQualifiedName~NamedPipeCommunicationTests"
```

现有测试覆盖域名匹配、HTTP/CONNECT 拦截、代理恢复、服务通信、普通会话持久化和强制恢复；它们**不能证明新的插件/Host 已可用**。需要新增长度帧拆包/粘包、stdout 并发、只读白名单、事件乱序与重连、新 epoch、倒计时误差和 DNR 生命周期测试。

## 10. 关键代码位置与职责

以下链接固定到本次审计提交，便于独立插件项目核对事实。行号对应该提交，后续代码变更时以符号名为准。

| 位置 | 关键符号/用途 |
| --- | --- |
| [Core / AccessControlService.cs:26](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Core/AccessControlService.cs#L26) | `EvaluateWebsite`、75 行的 `NormalizeWebsiteHost`、160 行的域名后缀匹配 |
| [Core / AccessControlModels.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Core/AccessControlModels.cs) | `WebsiteAccessRule`、`BlockedAccessResult` |
| [Desktop / BlockingPageViewModel.cs:186](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/ViewModels/BlockingPageViewModel.cs#L186) | 网站添加/编辑规范化、`BlockingChanged`、规则开关 |
| [Desktop / DesktopAccessControlBridge.cs:150](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/Services/DesktopAccessControlBridge.cs#L150) | 普通阶段变化驱动激活/撤销；202 行起规则持久化 |
| [Desktop / MainWindowViewModel.cs:956](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/ViewModels/MainWindowViewModel.cs#L956) | 普通 `LocalFocusSessionDto` 构造，`BlockingEnabled=false`，`StartNormalFocusAsync` |
| [Desktop / MainWindowViewModel.cs:876](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/ViewModels/MainWindowViewModel.cs#L876) | 普通会话放弃与持久化删除 |
| [Desktop / DesktopFocusSessionBridge.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/Services/DesktopFocusSessionBridge.cs) | 强制开始/取消、Service 权威会话同步到 WPF |
| [Core / FocusSessionEngine.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Core/FocusSessionEngine.cs) | 准备 5 秒、普通计时、剩余时间与状态机 |
| [Contracts / IpcProtocol.cs:8](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Contracts/IpcProtocol.cs#L8) | 管道名、协议 2、帧上限、JSON 约定；119 行起长度帧编解码 |
| [Contracts / IpcOperations.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Contracts/IpcOperations.cs) | 已有 operation 常量；122/147 行状态 DTO，213 行完整快照，238 行会话，311 行网站 DTO |
| [Contracts / NamedPipeIpcClient.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Contracts/NamedPipeIpcClient.cs) | 连接、请求关联、超时、`EventReceived`、断线 |
| [Service / NamedPipeServiceWorker.cs:16](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/NamedPipeServiceWorker.cs#L16) | SID 验证；90 行 ACL，127 行事件订阅/发送，275 行角色校验 |
| [Service / UserStateCoordinatorProvider.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/UserStateCoordinatorProvider.cs) | Windows SID → 用户协调器与数据库 |
| [Service / ServiceStateCoordinator.cs:98](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/ServiceStateCoordinator.cs#L98) | IPC 分派；332 行普通启动；510 行强制启动/规则快照；724 行恢复 |
| [Service / ServiceStateCoordinator.cs:938](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/ServiceStateCoordinator.cs#L938) | 强制保护与 Faulted 语义；981 行有效时钟，1102 行激活流程 |
| [Service / ServiceStateCoordinator.cs:1485](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/ServiceStateCoordinator.cs#L1485) | Agent 查询保护状态时的修复分支；1621 行有效规则刷新 |
| [Service / AccessControlExecutionHost.cs:81](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Service/AccessControlExecutionHost.cs#L81) | 网站代理/应用执行器生命周期与网站拦截事件 |
| [Infrastructure / LocalWebsiteProxy.cs:49](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Infrastructure/AccessControl/LocalWebsiteProxy.cs#L49) | 动态回环端口；187 行拦截；272 行 HTTP/CONNECT 解析；348 行隧道转发 |
| [Infrastructure / UserProxyManager.cs:171](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Infrastructure/AccessControl/UserProxyManager.cs#L171) | 代理准备/恢复日志、PAC 拒绝、上游代理；400 行起 HKCU Internet Settings 操作 |
| [Agent / AgentWorker.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Agent/AgentWorker.cs) | 代理动作执行、所有权校准、断线恢复；扩展 Host 不应复制这些动作 |
| [Infrastructure / SqliteLocalDataStore.cs:306](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Infrastructure/Persistence/SqliteLocalDataStore.cs#L306) | 网站规则存储；另有会话规则快照表 |
| [Infrastructure / LocalDataPathProvider.cs](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Infrastructure/Persistence/LocalDataPathProvider.cs) | 默认数据目录与 Windows SID 哈希作用域 |
| [Desktop / DesktopServiceConnection.cs:360](https://github.com/clm14156-lang/windowBlocked01/blob/23d4084152cdb9394501ca43e6bd91c535638fc1/src/FocusApp.Desktop/Services/DesktopServiceConnection.cs#L360) | 已有重连/初始查询；477 行起事件处理，业务 revision 去重 |

后续新增范围应独立评审：Host 小项目、安装注册、插件项目、必要的只读聚合 DTO/operation 与角色白名单。无需为了浏览器集成先重构当前代理、域名规则或专注业务状态机。
