# 复用 WPF 源码快照

本目录 `.cs` 文件从用户本机 `windowBlocked01` 项目的 Contracts / Core 原样复制，未修改：

- Contracts：`IpcProtocol.cs`、`IpcOperations.cs`、`NamedPipeIpcClient.cs`。
- Core：`AccessControlModels.cs`、`AccessControlService.cs`，以及 2026-10-29 新增复用的 `FocusThemeMode.cs`。

双主题接入核对了当前桌面源码 `FocusSessionViewModel.RefreshFocusTheme` 和 `CurrentGreetingState`：`Light`/`Dark` 固定，`System` 以软件本地问候时段划分（05:00–18:00 白天，其他时间夜晚），不读取 Windows 主题。Host 的 `FocusThemeResolver` 使用同一规则，将现有 `state.get.settings.focusTheme` 转成生效主题；普通、强制专注均读取当前设置，设置变化沿现有 `state.changed` 重新采样。未修改桌面项目。

它们让 Host 使用真实 DTO、管道客户端和域名规范化逻辑；无需引用 WPF 窗口，也不向外部源码目录写入构建产物。`provenance.json` 保存逐文件 SHA-256。原始对接文档的审计基线是 `23d4084152cdb9394501ca43e6bd91c535638fc1`，本地源码的 Git HEAD 因环境访问限制未独立确认，因此不宣称这些哈希等于远端该提交；已核对当前 API 与对接文档吻合。

部署前应与实际安装的 FocusApp 同版本源码逐文件核对；WPF 升级 Contracts 时需要同步并回归。不要仅通过放宽 JSON 校验兼容未知协议。正式联合发布可替换成经过版本锁定的 Contracts/Core 项目或包引用，但应避免对本机绝对路径的隐式依赖。

Contracts 本身包含写操作 DTO；Host 的 `ServiceSnapshotProvider.Query` 另有硬编码四查询白名单，浏览器协议也只有四种只读/订阅方法。复用 DTO 不意味着向浏览器开放写权限。
