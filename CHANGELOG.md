# Changelog

本文件记录 EdgeNexus 对外可见的行为变化。格式参考 Keep a Changelog，但当前工作区没有可用 Git 历史，因此不反向虚构旧版本号、发布日期或提交记录。

完整文档入口见 [docs/INDEX.md](docs/INDEX.md)，规划项见 [docs/Roadmap.md](docs/Roadmap.md)。

## [Unreleased]

当前没有待发布变更。

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
