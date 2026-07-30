# Contributing to EdgeNexus

感谢参与 EdgeNexus。这个项目首先服务于工业现场长期稳定运行，因此每次修改都应优先考虑故障隔离、可回滚、可观测和跨平台行为。

开始前请先阅读 [文档中心](docs/INDEX.md) 和 [内部架构](docs/Internal-Architecture.md)。

## 开发环境

- .NET 10 SDK
- PowerShell 7 或兼容 Shell
- Windows 或 Linux x64
- 可选的 MQTT Broker、PLC 仿真器或真实设备

```powershell
dotnet restore Snet.Edge.slnx
dotnet build Snet.Edge.slnx -c Release
dotnet test tests/Snet.Edge.Tests/Snet.Edge.Tests.csproj -c Release
```

## 修改前阅读路径

- 改分层或 DI：`docs/Internal-Architecture.md`、`docs/Architecture.md`
- 改采集循环：`docs/Runtime-Flows.md`、`docs/PLC-Drivers.md`
- 改 MQTT：`docs/MQTT.md`、`docs/Performance.md`
- 改配置或数据库：`docs/Configuration.md`
- 改 HTTP：`docs/WebAPI.md`
- 改 public 类型：`docs/Public-API.md`
- 改导入导出：`docs/Excel-Template.md`
- 新增协议：`docs/Plugin-Development.md`

## 架构红线

- Web 页面和 API 不直接操作 PLC 会话。
- Web 页面和端点不直接编写业务 SQL。
- PLC 驱动不生成 MQTT Topic 或 JSON Payload。
- 运行时采集不读取未发布草稿。
- Domain 不引用 Infrastructure 或 WebApi。
- Application 不依赖具体 HSL、MQTTnet、SQLite 或 ASP.NET Core 实现。
- 不在请求线程内运行长期采集或 MQTT 重连循环。
- 不使用无限队列、无限重试或无限并发。
- 不使用 `Thread.Sleep` 阻塞后台服务。
- 不因单设备、单点或单条 MQTT 消息异常停止 Host。

## C# 约定

项目公共编译配置为：

- `net10.0`
- C# 12
- Nullable 开启
- Implicit Usings 开启
- TreatWarningsAsErrors 开启

修改代码时：

- 遵循当前 file-scoped namespace 风格。
- 异步 API 接受并传递 `CancellationToken`。
- 长生命周期资源实现 `IDisposable` 或 `IAsyncDisposable`。
- 日志使用 `ILogger` / Serilog，不新增 `Console.WriteLine`。
- DTO 与不可变模型优先使用 record。
- 不为一次性简单逻辑引入新的抽象层。
- 热路径避免重复 LINQ、重复 JSON 反序列化和无界集合增长。

## 新增 PLC 协议

当前项目没有运行时 DLL 插件加载器。新增协议属于编译期扩展，至少检查：

1. `ProtocolKind`
2. `DeviceDefinition` 协议专属字段
3. `ProtocolDescriptors`
4. `DeviceProtocolValidator`
5. `TagProtocolValidator`
6. `ProtocolPointAddressCatalog`
7. `HslDeviceDriverFactory`
8. `IDeviceDriver` 实现或适配器
9. 网络诊断逻辑
10. Excel 导入别名与字段映射
11. Web 协议元数据和动态表单
12. 单元测试与真实硬件记录
13. `docs/PLC-Drivers.md`
14. `docs/Plugin-Development.md`

不要只在工厂类中增加一个分支；否则 Web 参数、校验、点表和文档会失去同步。

## 修改 MQTT

保持以下流水线：

```text
采集结果 -> TagSample -> DeviceData/TagData -> PayloadBuilder/TopicBuilder -> Outbox -> MQTT Client
```

修改模板、变量或报文时必须验证：

- 标准模板预览。
- 自定义字段常量和变量替换。
- 设备通信状态 Tag。
- 设备级模板绑定。
- 分组级 Topic 模板。
- 已入 Outbox 消息不被后续模板修改。
- 删除设备和点位后陈旧 Outbox 的清理行为。
- MQTT 断开、恢复、补发和死信。

## 修改数据库

- 使用显式事务保护多步写入。
- 保持 SQLite WAL 和现有连接配置兼容。
- Schema 变化必须有初始化或迁移策略。
- 不直接删除现场数据而不提供兼容或备份说明。
- 更新 `docs/Configuration.md` 的表、列和生命周期。
- 更新 `CHANGELOG.md`。

## 修改 BackgroundService

每个后台服务必须：

- 正确处理 Host 取消令牌。
- 捕获周期内业务异常。
- 在停止时不把预期的 `OperationCanceledException` 上抛为故障。
- 使用有界等待或 `PeriodicTimer`，不能忙循环。
- 说明依赖、生产者、消费者和数据所有权。
- 更新 `docs/Internal-Architecture.md` 与 `docs/Runtime-Flows.md`。

## 测试要求

至少运行最接近改动的测试，再运行完整 Release 测试：

```powershell
dotnet test tests/Snet.Edge.Tests/Snet.Edge.Tests.csproj -c Release
```

新增行为应优先增加相邻测试。涉及真实 PLC 的修改还应记录：

- PLC 型号和固件。
- 协议模式和端口。
- 点位地址与数据类型。
- 字节序。
- 连续运行时长。
- 断线、重连和超时结果。

不要把 Mock 测试结果表述为真实硬件兼容认证。

## 文档同步矩阵

| 代码变化 | 必须更新 |
|---|---|
| public class/interface/record/enum | `docs/Public-API.md` |
| HTTP 路由或 DTO | `docs/WebAPI.md` |
| 配置项或默认值 | `docs/Configuration.md` |
| SQLite 表或列 | `docs/Configuration.md`、`docs/Internal-Architecture.md` |
| HostedService / Channel | `docs/Runtime-Flows.md`、`docs/Performance.md` |
| 协议或地址规则 | `docs/PLC-Drivers.md`、`docs/Excel-Template.md` |
| MQTT Topic/Payload/Outbox | `docs/MQTT.md` |
| 部署文件或默认端口 | `docs/Deployment.md`、`README.md` |
| 用户可见行为 | `CHANGELOG.md` |

## 提交说明

当前工作区未提供远程仓库和 CI 规则，因此不虚构分支名、PR 模板或签署流程。建议提交说明至少包含：

- 解决的问题。
- 影响的模块。
- 稳定性风险。
- 回滚方式。
- 测试结果。
- 文档更新位置。

## 安全信息

不要提交：

- 生产 PLC IP、用户名和密码。
- MQTT Broker 密码、客户端证书和私钥。
- 现场数据库和 Outbox 文件。
- 真实工厂 Topic、设备编号和网络拓扑。
- `data/logs` 中可能包含现场信息的日志。

发现安全问题时按 [SECURITY.md](SECURITY.md) 处理。

## License 注意事项

当前仓库没有 `LICENSE` 文件。贡献者在项目所有者选择许可证前，不应假定提交内容已经按某一开源许可证发布。HslCommunication 等依赖还可能有独立商业授权要求。
