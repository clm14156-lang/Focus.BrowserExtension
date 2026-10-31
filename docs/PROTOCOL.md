# 本项目实现的浏览器协议 v1

Native Host 名称 `com.focusapp.browser_bridge`。传输使用 4 字节小端 UTF-8 字节长度 + JSON；双向最多 256 KiB；stdout 仅传协议。与 Service IPC v2 分开编解码，浏览器输入绝不直接转发到管道。

请求：

```json
{"version":1,"type":"request","id":"request-1","method":"hello","params":{}}
```

只支持 `hello / snapshot.get / snapshot.subscribe / snapshot.unsubscribe`，必须先 hello，params 必须为空对象。每连接每秒最多 10 次显式请求。ID 原样关联响应。

```json
{"version":1,"type":"response","id":"request-1","result":{"protocolVersion":1,"snapshotSubscription":true,"consistency":"sampled","remainingAccuracy":"estimatedUtc"},"error":null}
```

get 和 subscribe 返回原始文档 §6.2 的全量精简快照，并增加 `websitePolicy.rules[].name`。不传任务、数据库路径、代理端口或完整设置。subscribe 响应包括首份快照，后续为：

```json
{"version":1,"type":"event","event":"snapshot.changed","data":"此处实际是快照对象"}
```

新增的断线/采样错误事件：

```json
{"version":1,"type":"event","event":"bridge.error","data":{"code":"SERVICE_UNAVAILABLE","message":"本机 Service 连接已断开","retryable":true}}
```

读取错误响应的 `result` 为 null、`error` 为相同错误对象。Host 初始化失败不会返回空闲或演示数据。插件退避 1、2、4、8、15 秒（带抖动）重新创建 Host。协议不兼容停止自动重试，等待升级后手动重连。

- epoch 在连接/Service 实例变化时更新，序号在该 epoch 内严格递增。Host gate 串行化查询、序号分配和所有输出。插件串行应用快照，拒绝旧序号、已退休 epoch，并在序号缺口时校准。
- 启动/重连先注册 IPC 事件，再读取四个查询。事件处理器仅标记失效，不等待同一管道响应。源事件 sequence 缺口也触发失效。100 ms 合并、5 秒校准、最多连续重采样 3 次。
- `sourceRevision` 使用十进制字符串；不会拿它作为运行状态事件版本。`source` 是本项目附加诊断字段，真实模式为 `service`，显式开发夹具为 `demo`。
- `name` 最多 200 字符，空名用规范化域名；启用非法规则、损坏的强制快照、多个活动会话全部报错，不静默截断/回退。相同规范化域名按规则 Guid 排序取第一个名称。
- 强制快照允许只有应用规则、网站名单为空；`BlockingEnabled=true` 且两类必需快照都空视为异常。普通会话的 `BlockingEnabled` 不参与保护判断。
- 无会话但网站保护明确有效时，使用 current 名单，focus 为 unknown，剩余为 null；普通会话已过期不会因残余保护复活。
- 快照的保护/会话期限先以 `observedAtUtc` 校验采样时的一致性；若期限在传输或异步 DNR 应用期间经过，控制器立即清理规则并等待新快照，不安装已过期规则、不误报 `INVALID_SNAPSHOT: 保护期限`、不授权自动返回。采样时就已过期或结构矛盾的数据仍拒绝，快照租期与本机时钟的校验保持不变。
- 插件只安装自有 session rule `10001`，用 `requestDomains` 和完整 URL 正则限制 HTTP/HTTPS 顶层导航；规则替换和删除都检查结果。不会动其他 ID 或系统代理。
- 扩展内 UI 消息 `status.get / reconnect` 仅允许 `popup.html`；`blocked.get` 仅允许 `blocked.html` 并返回请求域名的最具体匹配。同时检查 sender.id 和完整页面位置，无 content script / externally_connectable。
- 站点文本使用 textContent，重新访问链接只从校验过的 ASCII DNS 主机生成 HTTPS 根地址。没有保存原路径或查询参数。
- `blocked.get` 增加后台计算的 `canAutoReturn`。仅在新快照已成功应用、focus 为 idle/0、网站策略关闭、自有 DNR 规则为空、桌面网站保护 inactive 且连接/租期正常时为 true。拦截页据此使用 `location.replace` 自动返回经校验的 HTTPS 根地址；不把本地计时归零或断线清理当作专注完成。
- 快照和 `blocked.get` 增加 `effectiveTheme: "light" | "dark"`。Host 从现有 `state.get.settings.focusTheme` 读取软件偏好，按桌面 `FocusSessionViewModel` 的规则解析：`Light` 固定白天，`Dark` 固定夜晚，`System` 按软件本地时段在 05:00/18:00 切换。页面不读取 OS/browser 主题、时间或完整设置；沿现有 `state.changed` 与 5 秒校准同步，下一次页面查询更新样式。旧 Host 缺失该字段时 UI 收到 null，保留最近确认主题（首次加载采用浅色基础样式），不影响拦截或自动返回。主题接入需要同时更新 Host。

平台依据：[Chrome DNR 权限、session rules、重定向资源要求](https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest)、[Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)、[Edge Host 注册](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)、[favicon API](https://developer.chrome.com/docs/extensions/how-to/ui/favicons)。
