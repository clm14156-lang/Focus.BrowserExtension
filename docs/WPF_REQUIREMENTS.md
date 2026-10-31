# WPF / Service 对接前提和待补能力

## MVP 所需的现有能力（已核对源码）

1. 同用户可连接 `FocusApp.Service.v2`，IPC v2，支持 `system.ping`、`state.get`、`access-control.status.get`、`focus.runtime-status.get`。
2. `state.get` 有活动会话的 `status / isForcedMode / configuredSeconds / plannedEndAtUtc / websiteRuleSnapshots` 和全局网站规则 `id / name / address / isEnabled`。
3. 三类变更事件与 Core 的 `AccessControlService.NormalizeWebsiteHost`。

原始文档的确没有现成 Native Host、HTTP API 或浏览器 v1 接口。本项目已经新增 Host 和浏览器协议；没有把它们当作 WPF 已有接口，也没有调用拟议的 `browser.snapshot.get`。本机找到了 WPF 源码，完整 DTO 和 Core 函数已核实并以源码快照复用；因此 POC 不要求先改 WPF 业务代码。源码来源与哈希另见 Vendor 记录，尚未用真实运行中的 Service 验证部署版本一致性。

如果部署的 WPF 版本缺少上述查询/事件、不同的 DTO，或普通 Focusing 未成功持久化，需 WPF 项目提供匹配版本 Contracts/Core 和运行 Service。插件不能靠读取 UI/数据库补出这些事实。首次联调必须先运行 README 的 `--probe-service`。

## WPF / Service 生产版本需要补充（目前未实现）

| 项目 | 现有缺口 | 本 MVP 的明确限制 |
| --- | --- | --- |
| 精简一致聚合查询，例如 `browser.snapshot.get` | 四个查询不是原子操作；`state.get` 包含完整历史且有 1 MiB 限制 | 事件失效重采样，最多 3 次，`consistency=sampled`，不伪称原子 |
| 服务端权威剩余时间 / 时基 | 外部只能按计划结束 UTC 估计；强制计时有自己的有效时钟 | `remainingAccuracy=estimatedUtc`，处理明显时钟跳变后重同步 |
| 普通准备态及逐秒计时投影 | 普通 5 秒准备主要在 WPF；不能完整镜像 UI | 不声称看到普通准备态，不提前装规则 |
| `BrowserObserver` 角色及服务端只读白名单 | 当前只有 Desktop / Agent；角色本身也不是程序签名认证 | Host 用 Desktop，但内部硬编码只准四个查询，浏览器不能透传 operation |
| 正式 Host 安装/升级配置 | 浏览器加载插件不会安装 Host | 本项目提供用户级开发注册脚本；正式安装器需负责可执行文件保护、签名和正式 ID |

## 已确认的技术差异

- 文档原 MVP 是 DNR `block`；本需求是 `redirect`。已增加 HTTP/HTTPS host 权限和 web_accessible_resources，以支持 `blocked.html`。原文的最小权限示例不能直接拿来做重定向。
- 拟议快照的规则原本只有 ID 和 domain，无法显示用户给网站起的名字。Host 现在新增 `rules[].name`，由现有 DTO `Name` 投影，插件与 Host 同时实现此扩展。WPF 无需增加网站名称接口。
- 现有网站 DTO 没有图标。MVP 使用浏览器 favicon API 和本地字母图标；若要求与 WPF 自定义图标完全一致，需要 WPF 定义图标来源/格式及读取协议，不能假定现成 `iconUrl`。
- 文档默认是 `mirrorReportedProtection`。若要求 PAC/代理故障时插件仍按专注意图保护，需要产品/WPF 共同定义 `focusIntent` 契约；本项目没有擅自切换。
- DNR 无自动过期。睡眠、浏览器后台终止时无法承诺精确 10 秒或专注结束瞬间删除。这是浏览器平台边界，WPF 新增接口也不能单独解决。
- 关闭 WPF 主窗口后 Service 仍可能继续强制专注；仅按“窗口退出”解除会破坏现有语义。MVP 跟随 Service，通信失效才短暂保留并清理规则。
