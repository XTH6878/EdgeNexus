# EdgeNexus Getting Started

## 1. 目标

本文从源码启动开始，走完登录、创建分组、创建设备、配置点位、配置 MQTT、校验发布、启动采集和确认上传的真实流程。

架构原理见 [Internal-Architecture.md](Internal-Architecture.md)，字段细节见 [Configuration.md](Configuration.md)，API 见 [WebAPI.md](WebAPI.md)。

## 2. 前置条件

- .NET 10 SDK。
- Windows 或 Linux x64。
- 一个可访问的 PLC、OPC UA Server 或协议仿真器。
- 一个可访问的 MQTT Broker。
- 浏览器。

检查 SDK：

```powershell
dotnet --info
```

项目目标框架由 `Directory.Build.props` 固定为 `net10.0`。

## 3. 获取源码

当前工作目录应包含：

```text
Snet.Edge.slnx
src/
tests/
docs/
deploy/
```

如果使用压缩包，先完整解压，避免从 ZIP 预览中直接启动。

## 4. 还原与构建

```powershell
dotnet restore Snet.Edge.slnx
dotnet build Snet.Edge.slnx -c Release
```

依赖版本集中定义在 `Directory.Packages.props`。

## 5. 启动 Web 网关

```powershell
dotnet run --project src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj
```

`appsettings.json` 默认监听：

```text
http://127.0.0.1:5088
```

浏览器打开：

```text
http://127.0.0.1:5088/
```

不要使用旧开发 profile 中的 `5080` 判断产品默认端口；产品启动配置以 `appsettings.json` 的 `Urls` 为准。

## 6. 使用启动脚本

WebApi 项目包含：

- `Start-EdgeNexus.ps1`
- `启动EdgeNexus网关.cmd`

发布后的 Windows 目录可双击 CMD 或运行 PowerShell 脚本。脚本会启动 `Snet.Edge.WebApi.exe`、轮询 `/health/ready`，成功后打开 `http://127.0.0.1:5088`。

更完整方式见 [Deployment.md](Deployment.md)。

## 7. 首次登录

开发引导默认值：

```text
用户名：admin
密码：123456
```

首次登录必须修改密码。

新密码规则：

- 至少 8 位。
- 包含数字。
- 包含特殊字符。
- 不要求同时包含大写和小写字母。

连续失败默认达到 5 次后锁定 15 分钟。对应配置见 [Configuration.md](Configuration.md)。

## 8. 生产环境首次启动

生产首次初始化前设置环境变量：

```text
Authentication__AdminUsername=你的管理员账号
Authentication__AdminPassword=强密码
Authentication__RequireHttps=true
Swagger__Enabled=false
```

如果由反向代理终止 TLS，可按部署拓扑决定 Kestrel 是否直接要求 HTTPS，但外部管理链路仍应使用 TLS。

## 9. 理解草稿与活动版本

页面编辑的是草稿，不会立即改变运行中的设备。

正确流程：

```text
编辑草稿 -> 校验草稿 -> 发布生效 -> 后台服务观察活动快照
```

未发布的设备不会开始采集，未发布的删除也不会停止活动版本中的设备。

## 10. 创建分组

分组是扫描与上传策略的基本组织单位。

至少配置：

- 分组名称。
- Topic 模板。
- 扫描周期。
- 上传周期。
- 是否启用。

示例：

```text
名称：Line01
Topic：factory/${line}/${deviceId}/data
Line：line01
扫描周期：1000 ms
上传周期：10 s
```

允许多个用途共享同一网卡，也允许同一分组中的多台设备共享 Topic 模板。实际 Topic 会按每台设备解析变量。

## 11. 扫描周期与上传周期

- `ScanCycleMs`：PLC 读取节奏。
- `UploadCycleSeconds`：周期型 MQTT 聚合节奏。
- `PublishOnChange`：点位变化达到死区后可提前上传。

变化上传不会删除分组周期上传逻辑；具体聚合行为见 [MQTT.md](MQTT.md)。

合法范围：

- 扫描周期：100 ms 到 86400000 ms。
- 上传周期：1 s 到 86400 s。

## 12. 创建设备

通用字段：

- 分组。
- 设备名称。
- MQTT ID，可空。
- MQTT 模板，可空。
- 协议。
- Host。
- Port。
- 连接超时。
- 接收超时。
- Station。
- 是否启用。

`MqttDeviceId` 为空时，运行路径回退使用设备名称作为外部 DeviceId。

`MqttTemplateId` 为空时，模板解析器使用内置标准模板。

## 13. 协议专属参数

Web 表单从 `/api/v1/protocols` 读取元数据，选择协议后动态显示对应字段。

示例：

- Siemens：系列、Rack、Slot、连接类型、TSAP。
- Mitsubishi：TCP/UDP、3E/1E、网络号、PLC 号等。
- Omron FINS：源/目标网络、节点和单元。
- Omron CIP / AB：路由路径和数组读取选项。
- Beckhoff：AMS Net ID、AMS 端口、符号缓存。
- OPC UA：用户名、密码、安全端点和证书信任策略。

字段完整表见 [Configuration.md](Configuration.md)。

## 14. 设备连接测试

保存设备草稿后可执行连接测试。

诊断链路可能包括：

1. Ping。
2. TCP 端口。
3. 协议握手或临时驱动连接。
4. 配置了点位时尝试读取。

三菱 UDP 没有 TCP 握手，需要有效点位和 PLC 返回报文才能确认通信。

连接测试成功不等于所有点位地址正确。

## 15. 配置点位

点位至少包含：

- 名称。
- 地址。
- 数据类型。

通用可选字段：

- Enabled。
- ByteOrder。
- Operator。
- Operand。
- Decimals。
- PublishOnChange。
- Deadband。
- StringLength。
- Remark。

Modbus TCP 额外选择功能码，功能码与数据类型必须匹配。

## 16. Modbus 示例

读取第一个保持寄存器：

```text
Address：0
DataType：UInt16
FunctionCode：03 ReadHoldingRegisters
```

读取从偏移 2 开始的 Float32：

```text
Address：2
DataType：Float32
FunctionCode：03 ReadHoldingRegisters
ByteOrder：按设备实际存储选择
```

如果设备手册写 `40001`，本系统通常填写偏移 `0`，不要直接照抄 40001。

## 17. Siemens 示例

```text
Bool：DB1.DBX0.0
UInt16：DB1.DBW2
Float32：DB1.DBD4
```

S7-1200/1500 通常需要关闭 DB 优化访问并允许 PUT/GET。更完整规范见 [PLC-Drivers.md](PLC-Drivers.md)。

## 18. 三菱示例

```text
Int16：D100
Float32：D200
Bool：M100
```

Float32 的正确字节序取决于 PLC 中实际字与字节排列。协议不同不代表业务值必须使用同一个 ByteOrder。

## 19. 导入点位

设备点位页面支持 `.xlsx` 和 `.csv` 预检。

最小列：

```text
TagName,Address,DataType
```

导出文件和全部列见 [Excel-Template.md](Excel-Template.md)。

## 20. 配置 MQTT 连接

北向传输至少检查：

- Enabled。
- Host。
- Port。
- ClientId。
- Username / Password。
- QoS。
- Retain。
- TLS。
- Last Will Topic / Payload。
- Store and Forward。
- Outbox 容量与补发限速。

Last Will Topic 是 Broker 在客户端非正常离线时发布的网关遗嘱，不与分组数据 Topic 冲突。

## 21. 网络用途绑定

如果工控机有多网卡，可在网络配置中绑定：

- PLC 采集网络。
- MQTT 上传网络。
- Web 管理网络。

选择 `Auto` 时使用操作系统路由。软件不创建新 IP，不修改 Windows/Linux 网卡配置。

## 22. 创建或编辑 MQTT 模板

模板可选择：

- 设备字段。
- 网关字段。
- 时间字段。
- Tag、Value、Quality。
- 自定义字段。
- 设备通信状态 Tag 前缀。
- 通信失败非零错误码。

正常错误码固定为 `0`。通信失败码可为正数或负数，但不能为 `0`。

## 23. 自定义字段

自定义字段由名称和值组成。

常量示例：

```text
名称：factoryId
值：F001
```

变量示例：

```text
名称：machine
值：${deviceName}
```

支持变量以当前 `MqttVariableResolver` 实现为准，详见 [MQTT.md](MQTT.md)。

## 24. 模板预览

模板页面的正式预览使用测试 `DeviceData` 生成 JSON。

预览应同时检查：

- 设备字段是否按勾选出现。
- `d` 数组是否包含普通点位。
- `d` 数组是否包含设备通信状态 Tag。
- 自定义字段是否替换变量。
- Topic 模板是否解析。

## 25. MQTT 测试发送

测试发送不是只在浏览器显示 JSON。后端会构建正式 Topic/Payload，并通过 `IMqttMessagePublisher` 进入 SQLite Outbox。

因此测试前应确认：

- Broker 配置正确。
- MQTT 已启用。
- Outbox 没有达到硬限制。
- 数据目录可写。

## 26. 校验草稿

点击顶部 `校验草稿`。

验证器会检查：

- 分组周期与 Topic。
- 设备分组引用。
- 协议参数范围。
- 点位地址、数据类型和功能码。
- MQTT 配置。
- 模板引用。
- 重复或孤立配置。

所有错误应修复后再发布。

## 27. 发布生效

点击顶部 `发布生效`。

发布成功后：

- 生成新的配置版本。
- 采集服务周期性观察新活动快照。
- 新增设备启动循环。
- 删除设备的旧循环收到取消。
- MQTT 配置变更通知发送服务。

## 28. 启动采集

顶部采集开关控制 `IAcquisitionControl`。

启动后关注：

- 设备状态。
- 最近成功时间。
- 点位实时值。
- 点位 Quality。
- 设备错误码。

Web 实时值来自认证后的 SignalR，并有 REST 自愈校准。

## 29. 判断设备状态

- Online：通信和读取满足成功阈值。
- Degraded：仍有成功点位，但存在点位失败。
- Reconnecting / Offline：全部读取持续失败或连接故障。

单点失败由该点 `Quality` 表达，不应仅凭一个坏点把设备通信错误 Tag 判为失败。

## 30. 判断 MQTT 状态

页面和 `/api/v1/mqtt/runtime` 可查看：

- Disabled
- Connecting
- Connected
- Reconnecting
- Faulted

Outbox 页面可查看待发送数、死信数和最老消息年龄。

## 31. 验证 Broker 报文

使用 Broker 自带工具或订阅客户端订阅实际分组 Topic。

标准报文示例：

```json
{
  "deviceId": "PLC001",
  "timestamp": "2026-07-29T02:53:08.0000000+00:00",
  "d": [
    { "tag": "#DEVICE_ERROR_PLC001", "value": 0, "quality": 0 },
    { "tag": "PLC001:Temperature", "value": 35.6, "quality": 0 }
  ]
}
```

实际字段由模板决定。

## 32. 删除点位后的行为

删除点位只修改草稿。必须：

1. 保存草稿。
2. 校验。
3. 发布。
4. 等待运行服务观察活动版本。

配置发布会触发陈旧 Outbox 清理同步，但已经被 Broker 接收的历史消息无法撤回。

## 33. 版本回滚

版本管理中预览历史快照，确认后执行回滚。

回滚要求提供当前活动版本用于并发保护。成功后会创建一个新的版本，采集服务再观察该新活动版本。

## 34. 健康检查

```text
GET /health/live
GET /health/ready
GET /health
GET /metrics
```

- Live：进程可响应。
- Ready：初始化、资源和 SQLite 就绪。
- Health：Watchdog 兼容入口，语义等同 Ready。
- Metrics：Prometheus 文本指标。

## 35. 常见启动问题

### 页面打不开

- 确认进程仍在运行。
- 确认地址是 `127.0.0.1:5088`。
- 请求 `/health/ready`。
- 检查 `data/logs`。
- 检查端口占用和防火墙。

### 打开了其他程序页面

说明端口被其他程序占用或访问了错误端口。停止冲突程序，或用 `--urls` 指定新的监听地址。

### Linux 浏览器不自动打开

systemd、Docker 和无桌面 Linux 不会自动打开浏览器，这是正常行为。应在管理电脑访问工控机 IP 和映射端口。

## 36. 下一步阅读

- 点表批量维护：[Excel-Template.md](Excel-Template.md)
- 协议开发：[Plugin-Development.md](Plugin-Development.md)
- 性能调优：[Performance.md](Performance.md)
- 生产部署：[Deployment.md](Deployment.md)
- 常见问题：[FAQ.md](FAQ.md)
- 文档中心：[INDEX.md](INDEX.md)
