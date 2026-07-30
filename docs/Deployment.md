# EdgeNexus 生产部署

## 1. 文档范围

本文基于当前仓库中的：

- `Dockerfile`
- `docker-compose.yml`
- `deploy/windows/install-service.ps1`
- `deploy/linux/snet-edge.service`
- `deploy/README.md`
- `src/Snet.Edge.WebApi/Start-EdgeNexus.ps1`
- `src/Snet.Edge.WebApi/启动EdgeNexus网关.cmd`

配置字段见 [Configuration.md](Configuration.md)，安全要求见 [../SECURITY.md](../SECURITY.md)，性能容量见 [Performance.md](Performance.md)。

## 2. 生产部署原则

- 使用 Release 发布。
- 使用独立可写数据目录。
- 使用低权限服务账户。
- 覆盖默认管理员凭据。
- 管理链路启用 TLS。
- 不把 PLC 网络直接暴露到公网。
- 将 `/health/ready` 用作就绪探针。
- 给优雅关闭至少 30 秒。
- 备份 SQLite 数据目录。

## 3. 数据目录

默认配置：

```text
Gateway:DataDirectory = data
```

相对路径基于应用 Content Root 解析。

目录包含：

- `gateway.db`
- SQLite WAL/SHM 文件。
- MQTT Outbox 和死信表。
- 配置版本。
- 用户账户。
- 审计日志表。
- `logs/gateway-*.json`。

数据目录必须持久化并限制访问权限。

## 4. 端口

源码默认：

```text
http://127.0.0.1:5088
```

Docker 容器内：

```text
http://+:8080
```

Compose 默认映射：

```text
host 5088 -> container 8080
```

## 5. 发布模式

当前部署示例使用 framework-dependent 发布，需要目标机器安装 .NET 10 Runtime。

GitHub `V1.0` Release 另提供自包含资产：

- `EdgeNexus-V1.0-win-x64.zip`
- `EdgeNexus-V1.0-linux-x64.tar.gz`

自包含资产无需预装 .NET Runtime，但包体更大。两种发布方式使用相同的数据目录、认证和网络配置语义。

### 5.1 Windows V1.0 资产

解压后运行：

```powershell
.\Snet.Edge.WebApi.exe
```

也可使用随发布内容提供的 `启动EdgeNexus网关.cmd`。

### 5.2 Linux V1.0 资产

解压后运行：

```bash
chmod +x Snet.Edge.WebApi
./Snet.Edge.WebApi
```

正式部署应继续使用专用用户和持久化数据目录，不建议长期从临时解压目录运行。

Windows：

```powershell
dotnet publish src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o publish/win-x64
```

Linux：

```powershell
dotnet publish src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj `
  -c Release `
  -r linux-x64 `
  --self-contained false `
  -o publish/linux-x64
```

## 6. Windows 直接运行

进入发布目录：

```powershell
.\Snet.Edge.WebApi.exe
```

打开：

```text
http://127.0.0.1:5088/
```

这种方式适合调试和临时验证，不适合无人值守长期运行。

## 7. Windows 双击启动

发布目录包含启动脚本时，可使用：

```text
启动EdgeNexus网关.cmd
```

它调用 `Start-EdgeNexus.ps1`。

脚本行为：

1. 查找 `Snet.Edge.WebApi.exe`。
2. 启动网关进程。
3. 最多轮询 40 次 `/health/ready`。
4. 每次等待 250 ms。
5. Ready 后打开默认浏览器。
6. 失败时显示明确错误。

## 8. 浏览器没有自动打开

可能原因：

- 网关未 Ready。
- 端口占用。
- exe 不在脚本预期目录。
- PowerShell 执行策略。
- 默认浏览器关联异常。
- 服务环境没有桌面会话。

即使浏览器没有自动打开，只要 `/health/ready` 成功，仍可手工访问地址。

## 9. Windows Service

以管理员 PowerShell 执行：

```powershell
.\deploy\windows\install-service.ps1 `
  -PublishDirectory .\publish\win-x64 `
  -DataDirectory C:\ProgramData\SnetEdge `
  -AdminUsername admin `
  -AdminPassword 'replace-with-strong-password'
```

默认服务名：

```text
SnetEdgeGateway
```

默认监听：

```text
http://127.0.0.1:5088
```

## 10. Windows Service 安全设置

安装脚本当前：

- 创建 Automatic 服务。
- 将账户改为 `NT AUTHORITY\LocalService`。
- 给数据目录授予 Modify。
- 设置失败恢复：5 秒、15 秒、60 秒重启。
- 24 小时后重置失败计数。
- 写入服务级环境变量。
- 关闭 Swagger。

服务只监听回环时，应由同机反向代理或本机浏览器访问。

## 11. Windows Service 环境变量

脚本写入：

- `ASPNETCORE_ENVIRONMENT=Production`
- `Gateway__DataDirectory`
- `Authentication__AdminUsername`
- `Authentication__AdminPassword`
- `Authentication__RequireHttps=false`
- `Swagger__Enabled=false`

如果没有反向代理而由 Kestrel 直接提供 TLS，需要自行配置证书并调整 RequireHttps。

## 12. Windows Service 运维

```powershell
Get-Service SnetEdgeGateway
Start-Service SnetEdgeGateway
Stop-Service SnetEdgeGateway
Restart-Service SnetEdgeGateway
```

日志位于配置的数据目录 `logs`。

## 13. Windows Service 卸载

当前仓库没有专用卸载脚本。

卸载前：

1. 停止服务。
2. 备份数据目录。
3. 删除服务注册。
4. 确认是否保留数据目录。

不要把删除服务和删除现场数据绑定成一个不可恢复操作。

## 14. Linux 发布目录

推荐：

```text
/opt/snet-edge
```

数据目录：

```text
/var/lib/snet-edge
```

服务用户：

```text
snet-edge
```

## 15. Linux 基础准备

示例步骤：

```bash
sudo useradd --system --home /var/lib/snet-edge --shell /usr/sbin/nologin snet-edge
sudo mkdir -p /opt/snet-edge /var/lib/snet-edge
sudo chown -R snet-edge:snet-edge /var/lib/snet-edge
```

将发布内容复制到 `/opt/snet-edge` 并确保服务用户可读取。

## 16. Linux 环境文件

创建：

```text
/etc/snet-edge/environment
```

至少包含：

```text
Authentication__AdminUsername=admin
Authentication__AdminPassword=replace-with-strong-password
Authentication__RequireHttps=false
```

设置权限：

```bash
sudo chmod 0600 /etc/snet-edge/environment
```

## 17. systemd 单元

仓库单元文件：

```text
deploy/linux/snet-edge.service
```

安装：

```bash
sudo cp deploy/linux/snet-edge.service /etc/systemd/system/snet-edge.service
sudo systemctl daemon-reload
sudo systemctl enable --now snet-edge
```

## 18. systemd 运行设置

当前单元：

- `User=snet-edge`
- `Group=snet-edge`
- `WorkingDirectory=/opt/snet-edge`
- `ExecStart=/usr/bin/dotnet /opt/snet-edge/Snet.Edge.WebApi.dll`
- `ASPNETCORE_URLS=http://127.0.0.1:5088`
- `Gateway__DataDirectory=/var/lib/snet-edge`
- `Restart=always`
- `RestartSec=5s`
- `TimeoutStartSec=60s`
- `TimeoutStopSec=30s`
- `KillSignal=SIGINT`

## 19. systemd 加固

当前单元包含：

- `NoNewPrivileges=true`
- `PrivateTmp=true`
- `ProtectSystem=strict`
- `ProtectHome=true`
- `ReadWritePaths=/var/lib/snet-edge`
- `LockPersonality=true`
- `RestrictSUIDSGID=true`
- `RestrictRealtime=true`
- `SystemCallArchitectures=native`
- `UMask=0027`

修改目录时必须同步 `ReadWritePaths`。

## 20. systemd 运维

```bash
systemctl status snet-edge
journalctl -u snet-edge -f
systemctl restart snet-edge
curl http://127.0.0.1:5088/health/ready
```

应用自己的 JSON 日志仍在 `/var/lib/snet-edge/logs`。

## 21. Linux 浏览器访问

无桌面 Linux 不会自动打开页面。

如果只监听 `127.0.0.1`：

- 在本机使用文本探针。
- 使用 SSH 隧道。
- 或配置受控反向代理。

不要简单把管理端口暴露到所有网卡而不增加 TLS、认证和防火墙。

## 22. Nginx / HAProxy

当前仓库没有提供具体反向代理配置模板，因此本文不虚构指令。

反向代理至少需要：

- TLS 证书。
- WebSocket 转发，供 SignalR 使用。
- 原始 Host 和客户端 IP 策略。
- 请求体大小满足 Excel 导入。
- 仅允许管理网络访问。

## 23. Docker 构建

```powershell
docker build -t snet-edge-gateway:local .
```

Dockerfile 使用：

- `mcr.microsoft.com/dotnet/sdk:10.0`
- `mcr.microsoft.com/dotnet/aspnet:10.0`

## 24. Docker 运行用户

运行镜像使用 .NET 基础镜像的 `app` 用户，不以 root 运行。

数据目录：

```text
/var/lib/snet-edge
```

## 25. Docker Healthcheck

当前 Healthcheck：

```text
interval 15s
timeout 3s
start-period 30s
retries 4
GET http://127.0.0.1:8080/health/ready
```

镜像安装 `curl` 仅用于探针。

## 26. Docker Compose

设置环境变量：

```powershell
$env:SNET_ADMIN_USERNAME = "admin"
$env:SNET_ADMIN_PASSWORD = "replace-with-strong-password"
docker compose up -d --build
```

Compose 会要求这两个变量存在，否则启动配置解析失败。

## 27. Compose 安全设置

当前 Compose：

- `restart: unless-stopped`
- `read_only: true`
- `init: true`
- 命名卷保存数据。
- `/tmp` 使用 64 MB tmpfs。
- `no-new-privileges:true`
- `cap_drop: ALL`
- `stop_grace_period: 30s`

## 28. Compose 数据卷

```text
gateway-data:/var/lib/snet-edge
```

删除容器不会自动删除命名卷。执行带 volume 删除的命令前必须确认备份。

## 29. Compose 访问

默认：

```text
http://127.0.0.1:5088/
```

Compose 文件端口映射语法本身可能绑定所有主机接口，具体以 Docker 平台为准。生产应通过防火墙或修改 ports 绑定限制管理入口。

## 30. Watchdog 部署

Watchdog 是独立项目：

```powershell
dotnet run --project src/Snet.Edge.Watchdog/Snet.Edge.Watchdog.csproj
```

生产应独立发布和托管，不与主网关放在同一进程。

## 31. Watchdog 数据目录

默认：

```text
watchdog-data
```

可由：

```text
SNET_WATCHDOG_DATA
```

覆盖。

日志为 JSON，每日滚动，保留 30。

## 32. Watchdog 健康边界

Watchdog 通过 HTTP 观察：

```text
/health
```

主网关的 `/health` 语义等同 Ready，包含初始化、资源保护和 SQLite 读取检查。

## 33. 健康端点选择

| 端点 | 用途 |
|---|---|
| `/health/live` | 进程存活 |
| `/health/ready` | 流量就绪、服务编排 |
| `/health` | Watchdog 兼容 |
| `/metrics` | Prometheus 指标 |

## 34. 生产环境变量

常用：

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5088
Gateway__DataDirectory=/path/to/data
Authentication__AdminUsername=...
Authentication__AdminPassword=...
Authentication__RequireHttps=true|false
Swagger__Enabled=false
```

采集防抖字段见 [Configuration.md](Configuration.md)。

## 35. TLS

两种方式：

1. Kestrel 直接 TLS。
2. 反向代理 TLS，Kestrel 监听受控回环地址。

当前仓库没有提交生产证书配置。不要把证书和私钥放入源码。

## 36. WebSocket

SignalR 路径：

```text
/hubs/status
```

反向代理必须支持 WebSocket 升级，否则页面实时值会回退或失去实时推送。

## 37. 数据备份

备份对象：

- 整个数据目录。
- `gateway.db`。
- WAL/SHM 一致性。
- 日志按需要保留。

推荐在服务停止或使用 SQLite 一致备份机制时执行，不能只在高写入期间复制一个数据库主文件后假定完整。

## 38. 恢复演练

1. 停止网关。
2. 备份当前数据目录。
3. 恢复测试副本。
4. 启动到隔离端口。
5. 检查用户、配置版本、模板、网络绑定和 Outbox。
6. 检查 `/health/ready`。
7. 不连接生产 Broker 时避免意外补发。

## 39. 升级流程

1. 导出工程。
2. 备份数据目录。
3. 阅读 `CHANGELOG.md`。
4. 在测试机验证新版本。
5. 停止旧服务。
6. 替换程序文件，不覆盖数据目录。
7. 启动并检查 Ready。
8. 检查配置、采集、MQTT 和 Outbox。
9. 保留旧发布目录用于回退。

## 40. 回退流程

程序回退与配置版本回滚不同：

- 程序回退：恢复旧发布目录。
- 配置回滚：在 Web 版本管理中创建新的活动版本。

数据库 Schema 变化时，程序回退前必须确认旧程序能读取新数据库。

## 41. 日志排查

网关日志：

```text
{DataDirectory}/logs/gateway-YYYYMMDD.json
```

优先查看：

- Host 启动失败。
- SQLite 初始化。
- 端口占用。
- PLC 连接和读取。
- MQTT 连接和发送。
- 资源保护级别。
- BackgroundService 异常。

## 42. 端口占用排查

如果 5088 打开的是其他程序：

- 确认 EdgeNexus 实际监听日志。
- 检查端口占用进程。
- 停止冲突服务，或为 EdgeNexus 指定新端口。
- 启动脚本的浏览器地址也要同步。

## 43. Linux 特有注意

- 文件名大小写敏感。
- 数据目录权限必须正确。
- 桌面 HSL DLL 若依赖 .NET Framework/WinForms，不能用于 Linux。
- 当前项目使用 HslCommunication 12.9.1 netstandard 兼容包。
- OPC UA 证书目录权限必须允许服务用户访问。

## 44. Docker 特有注意

- 根文件系统只读，所有写入必须在卷或 tmpfs。
- 不要把 SQLite 放在不可靠的网络文件系统而未经测试。
- 容器重建后数据卷仍可能包含旧 Outbox，并恢复补发。
- 变更管理员引导密码不会覆盖已初始化数据库用户。

## 45. Windows 特有注意

- Windows Service 无交互桌面，不会自动打开浏览器。
- LocalService 只能访问授予权限的目录和网络资源。
- 防火墙规则和网卡用途绑定是两件事。
- 启动脚本适合交互运行，不代替服务托管。

## 46. 上线检查表

- [ ] Release 构建和测试通过。
- [ ] NuGet 还原未报告已知高危漏洞，且未关闭警告即错误门禁。
- [ ] `win-x64` 与 `linux-x64` 自包含产物均完成独立发布验证。
- [ ] 默认密码已覆盖。
- [ ] 首次登录已改密。
- [ ] TLS 已启用。
- [ ] 管理端口受限。
- [ ] 数据目录持久化且权限正确。
- [ ] 磁盘容量满足 Outbox 和日志。
- [ ] PLC/MQTT/Web 网卡用途已验证。
- [ ] `/health/ready` 正常。
- [ ] `/metrics` 已接入监控。
- [ ] PLC 断线恢复已演练。
- [ ] MQTT 断线补发已演练。
- [ ] 配置导出和数据备份已完成。
- [ ] 回退方案已验证。
- [ ] HSL 和第三方授权已核对。

## 47. 相关文档

- [Getting Started](Getting-Started.md)
- [Configuration](Configuration.md)
- [Performance](Performance.md)
- [Runtime Flows](Runtime-Flows.md)
- [FAQ](FAQ.md)
- [Security](../SECURITY.md)
- [文档中心](INDEX.md)
