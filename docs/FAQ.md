# EdgeNexus FAQ

## 1. 文档使用方式

本页回答当前源码和部署中最常见的问题。遇到复杂问题时继续阅读对应专题：

- [Getting-Started.md](Getting-Started.md)
- [PLC-Drivers.md](PLC-Drivers.md)
- [MQTT.md](MQTT.md)
- [Deployment.md](Deployment.md)
- [Configuration.md](Configuration.md)
- [WebAPI.md](WebAPI.md)
- [INDEX.md](INDEX.md)

## 2. 网关从哪里启动？

源码方式：

```powershell
dotnet run --project src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj
```

Windows 发布目录可运行 `Snet.Edge.WebApi.exe`，也可使用 `启动EdgeNexus网关.cmd`。

长期无人值守推荐 Windows Service、systemd 或 Docker。

## 3. 网页地址是什么？

默认：

```text
http://127.0.0.1:5088/
```

默认值来自 `src/Snet.Edge.WebApi/appsettings.json`。

## 4. 为什么 5080 打开的是别的页面？

5080 来自开发 `launchSettings.json` profile，且可能被其他程序占用。产品默认地址是 5088。

如果访问 5080 出现其他应用，说明该端口由其他进程监听，不是 EdgeNexus 页面被替换。

## 5. 点启动后浏览器没有反应怎么办？

先手工打开 5088，再检查：

```text
http://127.0.0.1:5088/health/ready
```

常见原因：

- exe 不存在。
- 网关启动失败。
- 端口被占用。
- PowerShell 执行策略。
- 默认浏览器关联异常。
- 服务运行在无桌面会话。

## 6. Linux 会自动打开浏览器吗？

通常不会。systemd、Docker 和无桌面 Linux 没有交互式浏览器会话。

应在管理电脑访问 Linux 工控机 IP 和映射端口，或通过受控反向代理/SSH 隧道访问。

## 7. 默认账号密码是什么？

开发引导默认：

```text
admin / 123456
```

生产首次初始化前必须覆盖。

## 8. 为什么首次登录要求改密？

数据库用户带首次改密标记。开发引导密码不应长期使用。

新密码至少 8 位，包含数字和特殊字符，不要求大小写组合。

## 9. 为什么账号被临时锁定？

默认连续失败 5 次，锁定 15 分钟。

配置：

```text
Authentication:MaximumFailedAttempts
Authentication:LockoutMinutes
```

不要通过删除数据库绕过锁定，这会同时丢失配置和历史。

## 10. 改了 appsettings 的默认密码为什么登录密码没变？

引导账号只用于新数据库初始化。已有 `gateway.db` 中的用户记录不会被 appsettings 自动覆盖。

应在 Web 中修改密码，或按受控恢复流程处理用户数据库。

## 11. 配置保存后为什么设备没变化？

页面修改的是草稿。必须：

1. 保存。
2. 校验草稿。
3. 发布生效。

采集只读取活动版本。

## 12. “校验草稿”是否校验全部设置？

它校验完整草稿快照，包括分组、设备、点位、MQTT 和引用关系，不只是当前页面。

## 13. “发布生效”会立即重启程序吗？

不会。发布创建新的活动版本，后台服务通过热更新逻辑观察并应用，不需要重启 Host。

## 14. 如何回滚工程版本？

在版本管理中预览历史版本并回滚。回滚会创建一个新的活动版本，不直接修改历史版本。

## 15. 历史版本可以删除吗？

API 支持删除非活动历史版本，并使用当前活动版本做并发保护。活动版本不能按普通历史版本删除。

## 16. 相同设备名和相同 IP 可以出现在不同分组吗？

配置身份同时受分组和设备关系影响。不同分组可以表达相同名称和地址的独立采集定义，但会建立两个独立设备循环和连接。

是否应这样配置取决于 PLC 连接上限和重复采集需求。非必要不要重复连接同一 PLC。

## 17. MQTT ID 是做什么的？

`MqttDeviceId` 是北向报文和 Topic 使用的外部设备标识。

为空时回退设备名称。

它不改变 PLC 连接参数，也不是 MQTT ClientId。

## 18. 设备选择 MQTT 模板是否生效？

生效。`DeviceDefinition.MqttTemplateId` 在聚合和设备健康路径中传给模板解析器。

为空时使用内置标准模板。

## 19. 分组 Topic 和 Last Will Topic 冲突吗？

不冲突。

- 分组 Topic：业务数据发送目标。
- Last Will Topic：MQTT 客户端异常离线时由 Broker 发布的遗嘱目标。

建议使用不同 Topic 层级。

## 20. “断点续传”是什么？

对应 `StoreAndForwardEnabled`。

Broker 断开时最终 Topic/Payload 写入 SQLite Outbox，恢复后自动补发。

## 21. SQLite 存储路径为什么没有单独页面配置？

数据目录由 Host 启动配置 `Gateway:DataDirectory` 决定，不适合在运行页面内随意切换。

数据库、Outbox、用户和日志都依赖该目录，动态迁移风险高。

## 22. 删除点位后为什么 MQTT 仍上传？

先区分：

- 只删除草稿但未发布。
- 活动版本已删除但 Outbox 仍有旧消息。
- Broker/订阅端展示历史 retained 消息。
- 页面缓存仍显示旧值。

正确操作：发布删除、观察 Outbox 清理、检查 Retain 和 Broker 订阅历史。

## 23. MQTT 是一台设备一条消息吗？

当前是设备级消息。

每个 `MqttPublishRequest` 只含一个 `DeviceData`。同分组设备可发送到同一 Topic 模板，但 Payload 不合并多设备。

## 24. 是否应该按分组合并多台设备？

当前没有实现。设备级消息有更小故障域、重试粒度清晰、单条 Payload 可控的稳定性优势。

只有服务器明确要求批量协议，并经过消息大小、延迟和重试语义设计后再增加。

## 25. 设备状态 Tag 为什么没有上传？

检查：

- 设备是否绑定启用模板。
- 模板是否包含 Tag、Value、Quality。
- 模板的设备错误 Tag 前缀是否合法。
- DeviceHealthMonitor 是否观察到设备和状态。
- 草稿是否已发布。
- Outbox 是否有消息。

模板预览应显示系统状态 Tag。

## 26. 设备错误 Tag 名可以自定义吗？

可以配置前缀 `DeviceErrorTagPrefix`。

最终格式：

```text
{Prefix}_{DeviceId}
```

默认：

```text
#DEVICE_ERROR_{DeviceId}
```

## 27. 通信失败错误码可以自定义吗？

可以。模板字段 `CommunicationFailureCode` 接受非零整数，可正可负。

正常值固定为 `0`。

## 28. Quality 是否表示设备通信错误？

不是。

- 设备通信错误通过系统状态 Tag 的 `value` 表达。
- `quality` 表示该 Tag 数据本身是否有效。
- 普通点位使用自己的 Quality 表达单点问题。

## 29. 十个点有两个读取失败，设备是什么状态？

只要至少有一个点位成功，设备级通信错误 Tag 保持 `0`。

设备 UI 可显示 `Degraded`，两个失败点自己的 Quality 显示异常。

## 30. 何时设备错误 Tag 变成非零？

全部有效点位持续失败、连接断开或超时达到失败阈值时。

默认连续 3 次失败确认故障。

## 31. 恢复为什么不是一次成功就 Online？

默认连续 2 次成功确认恢复，用于避免网络抖动导致状态快速闪烁。

## 32. 为什么 S7 状态断断续续？

常见原因：

- 单点地址错误被误当设备错误。
- 扫描周期小于真实读取耗时。
- Rack/Slot 或系列不匹配。
- S7-1200/1500 未允许 PUT/GET。
- DB 优化访问未关闭。
- 网络丢包或 PLC 连接上限。

当前防抖与“至少一个点成功即通信正常”规则用于减少假离线。

## 33. 握手成功是否就应该显示正常？

不能只看握手。

握手证明连接建立，不证明配置点位可读取。当前设备健康以实际读取成功为重要依据，同时单点失败不直接判设备通信失败。

## 34. 扫描周期越长性能越好吗？

通常网络和 CPU 压力更低，但数据时效性下降。

应按工艺要求、PLC 响应能力、点位数和 MQTT 需求选择，不是越长越好。

## 35. 扫描周期 30 秒可以吗？

可以，合法范围允许 30 秒。

采集循环还有心跳逻辑用于长扫描周期保持连接健康，默认心跳 10 秒。

## 36. 设备能读值但采集状态显示离线怎么办？

检查：

- 当前值是否来自旧缓存。
- 是否达到连续成功恢复阈值。
- 是否只有无效/禁用点位。
- 运行状态时间戳和最后成功时间。
- 是否查看了草稿设备而活动版本不同。

## 37. Modbus 点位为什么没有实时值？

检查：

- 地址是零基偏移。
- 功能码正确。
- 01/02 只配 Bool。
- 03/04 配寄存器类型。
- Station 正确。
- ByteOrder 正确。
- 设备已发布并启动采集。

## 38. 功能码为什么只在 Modbus 点位出现？

因为 `ModbusFunctionCode` 是 Modbus 点位语义。其他协议不使用该字段，前后端均按协议条件展示和校验。

## 39. 三菱读不到数据怎么办？

检查：

- TCP/UDP。
- Binary3E/Ascii3E/Binary1E/Ascii1E。
- 1E 只能 TCP。
- PLC 号、网络号和目标 IO。
- X/Y 地址进制。
- D/M 等软元件是否存在。
- ByteOrder。

UDP 诊断必须实际读点确认。

## 40. 三菱支持 A 兼容 1E 吗？

当前模型支持：

- `Binary1E`
- `Ascii1E`

只允许 TCP，并主要使用 PLC 编号。

## 41. 为什么西门子 Float 用 ABCD，三菱用 DCBA 才正常？

因为 PLC 字节与字排列可能不同。Float32 占 4 字节，设备写入顺序和协议返回顺序共同决定 ByteOrder。

正确值应以已知测试量或 PLC 工程监视值校验。

## 42. 欧姆龙 FINS 读不到怎么办？

检查：

- DA1/SA1 节点。
- DNA/SNA 网络号。
- 单元号。
- FINS 路由表。
- D/CIO/W/H 地址格式。
- Bool 位号 00-15。
- PLC 是否允许 FINS/UDP 或对应传输。

## 43. 欧姆龙 CIP 和 FINS 有什么区别？

- FINS 读取内存区地址。
- CIP 读取 NJ/NX 发布变量名。

协议、端口、地址和路由不能混用。

## 44. Beckhoff ADS 只开端口为什么还不通？

ADS 跨主机通常还需要双方 AMS Route 和正确 AMS Net ID。仅开放 TCP 端口不能替代 TwinCAT 路由配置。

## 45. OPC UA 为什么连接失败？

检查：

- Endpoint URL 和端口。
- 安全模式。
- 用户名密码。
- 服务端证书。
- 客户端信任目录权限。
- `AutoAcceptUntrusted` 是否仅用于受控测试。
- NodeId 是否完整。

## 46. 点位导入支持哪些格式？

- `.xlsx`
- `.csv`

单设备最小列：`TagName`、`Address`、`DataType`。

工程最小列见 [Excel-Template.md](Excel-Template.md)。

## 47. 导入会直接影响生产吗？

不会。工程导入写草稿，仍需校验和发布。

## 48. replaceExisting 有什么风险？

它替换草稿中的分组、设备和点位。使用前应导出工程或确认有可回滚版本。

## 49. 网络配置会修改系统 IP 吗？

不会。

软件只读取系统网卡，并保存 PLC、MQTT、Web 三个用途绑定。源码不调用 `netsh` 修改 IP。

## 50. 只有一块网卡时怎么配置？

保持 `Auto` 即可。解析结果会使用唯一可绑定网卡。

## 51. 多块网卡可以都选同一块吗？

可以。三个用途没有互斥限制。

## 52. Web 绑定到错误网卡会怎样？

`WebNetworkBindingMiddleware` 会检查请求到达的本地地址是否满足 Web 管理用途绑定。变更前应确保仍有可访问管理路径。

## 53. MQTT 连不上怎么排查？

按顺序：

1. Ping Broker。
2. TCP 端口测试。
3. MQTT 连接测试。
4. 核对认证。
5. 核对 TLS 证书。
6. 核对 MQTT 网卡用途。
7. 查看运行状态 LastError。
8. 查看日志。

## 54. 服务端口输入框太小是后端问题吗？

不是通信逻辑问题，属于前端表单样式。后端 Port 字段仍按整数范围验证。

## 55. MQTT 模板支持手写任意 JSON 吗？

不支持。当前采用系统字段勾选和自定义字段，避免现场手写 JSON 破坏结构。

## 56. 自定义字段怎么用？

常量：

```text
factoryId = F001
```

变量：

```text
machine = ${deviceName}
```

保存后用模板预览和测试发送验证。

## 57. 模板修改需要重启吗？

不需要。模板保存到 SQLite 并更新内存缓存，新入队消息使用新模板。

旧 Outbox 消息保持原 Payload。

## 58. MQTT 断线会影响 PLC 采集吗？

设计上不会。采集产生统一数据，MQTT 通过 SQLite Outbox 异步发送。

磁盘达到 Critical 时资源保护会停止采集和持久化入队，以避免整机失控。

## 59. Outbox 满了会怎样？

`OutboxMaxMessages` 是硬限制。应通过状态、指标和磁盘监控提前处理，不能把容量调成无限。

## 60. 死信是什么？

消息超过 `MaxPublishAttempts` 后进入 `mqtt_dead_letter`，保留 Topic、Payload、尝试次数、错误和失败时间。

当前 Web API 提供查询，手工重放能力属于候选 Roadmap。

## 61. 为什么系统需要 Watchdog？

Watchdog 是独立进程，通过 HTTP Health 观察主网关。主进程完全无响应时，内部自恢复也可能无法执行，因此需要进程外观察。

## 62. Watchdog 会不会重启风暴？

设计包含连续失败阈值、重启冷却和每小时次数限制。恢复命令必须显式配置，不通过 Shell 拼接任意参数。

## 63. Docker 数据会丢吗？

Compose 使用命名卷 `gateway-data`。删除容器通常不删除卷，但执行删除卷操作会丢数据。

上线前必须验证备份和恢复。

## 64. Windows Service 为什么不打开浏览器？

服务运行在非交互会话，不能依赖桌面浏览器。应手工访问或通过管理工作站访问。

## 65. 当前有插件系统吗？

没有运行时 DLL 插件加载。

新增协议需要编译期修改多个层，见 [Plugin-Development.md](Plugin-Development.md)。

## 66. 当前使用 Polly 自动重试吗？

包依赖包含 `Polly.Core`，但当前源码未发现实际策略装配。采集重连和 MQTT 恢复由项目自己的状态机与退避逻辑实现。

## 67. 当前支持 PLC 写入吗？

公共 `IDeviceDriver` 只有连接、读取和断开，没有写入方法。

不要把项目描述为已支持 PLC 写入。

## 68. 当前支持历史趋势数据库吗？

没有。TelemetryBuffer 保存最新值和变化，SQLite 用于配置、Outbox、用户和审计，不是时序历史库。

## 69. 当前有 License 吗？

没有 `LICENSE` 文件。在项目所有者提交许可证前，不能假定源码已授予开源使用、修改或分发许可。

## 70. 还有问题怎么办？

先收集：

- 当前活动配置版本。
- 设备协议和参数。
- 点位地址与类型。
- 运行状态和 Quality。
- `/health/ready`。
- MQTT Runtime 和 Outbox 状态。
- 脱敏日志。
- 操作系统与部署方式。

再从 [INDEX.md](INDEX.md) 进入对应专题。
