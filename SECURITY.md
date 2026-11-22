# Security Policy

EdgeNexus 连接 PLC、工业网络和 MQTT Broker。安全问题可能影响生产网络、设备数据和远程运维，因此应谨慎披露和验证。

系统安全架构与配置项见 [内部架构](docs/Internal-Architecture.md)、[部署](docs/Deployment.md) 和 [配置参考](docs/Configuration.md)。

## 支持范围

当前工作区没有正式版本标签和维护分支信息，因此本文件不声明具体受支持版本号。项目所有者发布版本后，应在此增加受支持版本矩阵。

在此之前，安全修复应以当前主开发版本为目标，并在 `CHANGELOG.md` 的 `Security` 分类记录。

## 报告安全问题

当前仓库没有配置公开安全邮箱或 GitHub Security Advisory 地址。请使用项目所有者提供的私有联系方式报告；在私有渠道未建立前，不要公开提交包含可利用细节的 Issue。

报告建议包含：

- 受影响模块和版本或构建时间。
- 部署方式：Windows、Linux、Docker 或 Windows Service。
- 复现条件和最小步骤。
- 实际影响和最坏影响。
- 是否需要 PLC、Broker 或管理员权限。
- 已采取的临时缓解措施。
- 脱敏后的日志、请求和堆栈。

不要附带真实密码、私钥、证书、生产数据库或完整现场网络拓扑。

## 当前源码安全控制

### Web 认证

- Cookie 身份验证。
- ASP.NET Identity 密码哈希存储。
- 管理员角色策略。
- 首次登录强制改密。
- 连续失败锁定。
- Cookie `HttpOnly`。
- Cookie `SameSite=Strict`。
- 可按 `Authentication:RequireHttps` 强制 Secure Cookie、HSTS 和 HTTPS 重定向。

### 请求保护

- 非只读 API 使用防伪令牌头 `X-CSRF-TOKEN`。
- 匿名 API 返回 401。
- 权限不足返回 403。
- `UseExceptionHandler` 统一异常处理。
- 安全响应头包含 `nosniff`、`DENY`、Referrer Policy、CSP 和 Permissions Policy。
- SignalR Hub 要求认证。

### 数据与运行保护

- SQLite 数据目录可独立配置。
- 日志按天滚动并保留 30 个文件。
- Docker 以非 root、只读根文件系统、删除全部 capabilities 方式运行。
- systemd 单元使用 `NoNewPrivileges`、`ProtectSystem=strict` 和专用用户。
- Windows Service 安装脚本使用 `LocalService`，只授予数据目录修改权限。
- 资源达到 Critical 时停止新的采集和持久化入队。

## 生产部署必须完成

1. 覆盖默认 `admin/123456`。
2. 启用 HTTPS，或通过受控反向代理终止 TLS。
3. 限制 Web 管理端口的访问网段。
4. 使用独立 MQTT 账号和最小 Topic ACL。
5. 使用受信任 CA 和有效 Broker 证书。
6. 将数据目录权限限制给网关服务账户。
7. 保护 `gateway.db`、日志、Outbox 和备份。
8. 禁止把 PLC 网络直接暴露到办公网或互联网。
9. 使用操作系统防火墙限制出入站端口。
10. 定期查看审计日志、死信和登录锁定事件。

## 默认凭据风险

`src/Snet.Edge.WebApi/appsettings.json` 包含开发引导账号 `admin/123456`。它只应服务于首次本地启动。

生产环境首次初始化前设置：

```text
Authentication__AdminUsername
Authentication__AdminPassword
Authentication__RequireHttps=true
```

数据库完成初始化后，引导密码不再替代数据库中的密码。复制已有数据目录也会复制已有用户凭据。

## MQTT 安全

- MQTT TLS 是否启用由发布配置控制。
- 用户名和密码属于敏感配置。
- Last Will Topic 不等同于分组数据 Topic。
- Topic 模板可包含设备、网关和产线标识，可能暴露生产结构。
- Outbox 保存已经构建的 Topic 与 Payload，磁盘访问者可能读取业务数据。
- 死信表也可能保留发送失败的完整 Payload。

因此数据目录必须按敏感业务数据保护，不能作为普通临时目录共享。

## PLC 与 OPC UA 安全

- 网关当前以读取为主，公共驱动接口没有写入方法。
- PLC 凭据、OPC UA 用户名和密码不应写入示例或日志。
- OPC UA `AutoAcceptUntrusted` 适合受控调试，不应无审查地用于生产。
- Siemens PUT/GET、FINS、MC、CIP 和 ADS 的开放范围应限制在必要网络。
- 网络诊断会主动发起 Ping、TCP、MQTT 或 PLC 连接，只应由管理员操作。

## 依赖安全

主要依赖版本由 `Directory.Packages.props` 集中管理。升级依赖时必须：

- 阅读发行说明和安全公告。
- 运行完整测试。
- 复核 PLC 地址和字节序兼容性。
- 复核 MQTT TLS、QoS 和重连行为。
- 复核 SQLite Schema 和事务行为。
- 更新 `CHANGELOG.md`。

所有第三方依赖均应保持开源许可证合规，不引入闭源收费或含运行时长限制的组件。

## 日志脱敏

新增日志时禁止记录：

- 密码、Token、Cookie 和 CSRF 值。
- 私钥和证书内容。
- 完整连接字符串中的秘密。
- 大量完整 MQTT Payload。
- 未脱敏的生产设备清单。

日志应保留 TraceId、模块、设备内部标识、错误类型和堆栈，以便排查而不泄露凭据。

## 安全修复要求

- 先建立可复现测试。
- 修复根因，不只隐藏错误信息。
- 不降低认证、CSRF 或 TLS 默认保护以换取方便。
- 明确数据兼容和回滚方法。
- 更新安全文档、专题文档和 Changelog。
- 对认证、授权、输入校验和路径处理执行针对性回归。

## 已知治理缺口

- 当前仓库没有 `LICENSE`。
- 当前没有公开安全联系人。
- 当前没有可见的 CI 依赖扫描配置。
- 当前没有证书生命周期管理模块。
- 当前没有多用户细粒度 RBAC，源码仅提供管理员角色路径。

这些内容属于治理或 Roadmap，不应被描述为已实现能力。候选规划见 [docs/Roadmap.md](docs/Roadmap.md)。
