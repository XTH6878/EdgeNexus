# Changelog

本文件记录 EdgeNexus 对外可见的行为变化。格式参考 Keep a Changelog，但当前工作区没有可用 Git 历史，因此不反向虚构旧版本号、发布日期或提交记录。

完整文档入口见 [docs/INDEX.md](docs/INDEX.md)，规划项见 [docs/Roadmap.md](docs/Roadmap.md)。

## [Unreleased]

当前没有待发布变更。

## [V1.1] - 2026-11-22

### Added

- 新增 [Apache License 2.0](LICENSE) 开源许可证文件，明确项目开源使用、修改与商业分发授权。
- 新增 `.gitattributes` 统一跨平台换行符与前端 vendored 静态资源二进制保护，防止 Windows 克隆时 `core.autocrlf` 破坏前端资产 SHA-256 完整性校验。
- 新增内置纯 `.NET 10` 原生异步工业协议栈 `NativePlcProtocols.cs`，完整实现 Siemens S7（ISO-COTP + S7 PDU 协商与多变量批读）、Modbus TCP、Mitsubishi MC（3E/1E Binary/ASCII TCP/UDP）、Keyence KV MC、Omron FINS/TCP（含节点分配握手与分片读取）、Panasonic Mewtocol（ASCII + BCC 校验）、Allen-Bradley EtherNet/IP & Omron CIP（RegisterSession + SendRRData + `0x4C` 单读 / `0x0A` 多服务包批读）、Beckhoff ADS（AMS/TCP + 符号句柄解析缓存 + `0xF080` SumRead 批读）、Inovance TCP（AM/H3U/H5U/Easy 地址翻译）与 Delta TCP（DVP/AS 地址翻译）。
- 新增 `NativePlcProtocolWireTests` 回环报文级集成测试，覆盖各协议真实 TCP/UDP 握手、批量读取、单点非法地址异常隔离及配置快照内存缓存验证（测试总数由 181 提升至 189 并全部通过）。

### Changed

- `OpcUaDeviceDriver.ReadManyAsync` 升级为单次 `session.ReadAsync` 节点列表批量读取，并对单节点 Bad StatusCode 执行点位级隔离，大幅降低多点位 OPC UA 轮询往返开销。
- `SqliteGatewayConfigurationStore.GetSnapshotAsync` 增加线程安全的活动配置快照内存缓存（在初始化、发布草稿、独立应用 MQTT 配置及回滚时原子刷新），消除 `TelemetryAggregationWorker` 每 250ms 重复查询 SQLite 与反序列化 JSON 的 CPU/GC 开销。
- `MqttOutbox` 增加待发消息原子计数器缓存，将 `PublishAsync` 入队容量检查从每次事务内 `SELECT COUNT(1) FROM mqtt_outbox` 全表扫描优化为 O(1) 内存判断。

### Removed

- 彻底移除第三方商业闭源通信库 `HslCommunication` NuGet 依赖，消除 24 小时试用断连限制与商业授权合规风险，保持 100% 网卡绑定（`LocalBinding`）与原有公共类型接口兼容。

## [V1.0] - 2026-07-30

### Added

- 建立完整文档中心，覆盖架构、配置、运行时、PLC、MQTT、Web API、公共类型、部署、性能、扩展开发、Excel 点表和 FAQ。
- README 使用当前源码启动后的真实登录页与运行总览截图。
- 增加贡献、安全和变更记录治理文件。
- 提供 `win-x64` 与 `linux-x64` 自包含 Release 资产。

### Changed

- README 统一使用当前 `appsettings.json` 的默认 Web 地址 `http://127.0.0.1:5088/`。
- 文档明确区分已实现能力、源码未发现能力和 Roadmap 候选项。

### Security

- 文档明确生产环境必须覆盖默认管理员凭据。
- 文档明确当前仓库尚未提交许可证。
- 发布依赖固定到已修复的 `Microsoft.OpenApi 2.7.5` 和 `SQLitePCLRaw.lib.e_sqlite3 2.1.12`，阻止高危漏洞进入双平台自包含包。

## 记录规则

每个面向用户或运维人员的变更，至少归入以下一个类别：

- `Added`：新增功能、端点、协议、配置项或运维能力。
- `Changed`：已有行为、默认值、报文或兼容性变化。
- `Deprecated`：仍可使用但计划移除的能力。
- `Removed`：删除功能、接口或配置项。
- `Fixed`：缺陷修复。
- `Security`：认证、授权、依赖、证书或漏洞相关变化。

## 必须记录的工业级变化

- 设备状态机阈值或错误码语义变化。
- PLC 地址解析、字节序或批量读取策略变化。
- MQTT Topic、Payload、QoS、Retain、Last Will 或 Outbox 行为变化。
- SQLite Schema、迁移或持久化目录变化。
- 草稿、发布、回滚和版本删除规则变化。
- 默认端口、健康检查、服务账户或容器权限变化。
- Channel 容量、丢弃策略、重试、退避和超时变化。
- public 类型、Web API 或配置项的不兼容变化。

## 维护要求

发布前应完成：

1. 将 `Unreleased` 中的条目移动到项目所有者确认的版本标题。
2. 使用实际发布日期，不预填计划日期。
3. 更新 [README.md](README.md) 中的能力列表。
4. 更新 [docs/INDEX.md](docs/INDEX.md) 的覆盖统计。
5. 更新受影响的专题文档。
6. 运行 Release 构建和测试。
7. 核对 Docker、Windows Service 和 systemd 部署说明。
8. 核对 `LICENSE` 和第三方依赖授权状态。

## 兼容性声明规则

只有满足下列条件时，才可在本文件中声明某 PLC 型号或部署规模“已验证”：

- 有明确的硬件或仿真环境记录。
- 有协议、固件、网络拓扑和点位规模信息。
- 有持续运行时长和错误统计。
- 有可复现的测试步骤或测试资产。

进程内单元测试、Mock 驱动和容量冒烟测试不能写成现场硬件认证。
