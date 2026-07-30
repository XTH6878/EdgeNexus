# 生产部署

## Linux / systemd

1. 发布：`dotnet publish src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj -c Release -r linux-x64 --self-contained false -o publish/linux-x64`。
2. 将发布目录复制到 `/opt/snet-edge`，创建系统用户 `snet-edge` 和数据目录 `/var/lib/snet-edge`。
3. 创建权限为 `0600` 的 `/etc/snet-edge/environment`，至少填写：
   `Authentication__AdminUsername=...`、`Authentication__AdminPassword=...`、`Authentication__RequireHttps=false`。
4. 安装 `deploy/linux/snet-edge.service`，执行 `systemctl daemon-reload && systemctl enable --now snet-edge`。
5. 生产网络应由 Nginx、HAProxy 或受控负载均衡器终止 TLS；若 Kestrel 直接提供证书，将 `Authentication__RequireHttps` 改为 `true`。

## Windows Service

以管理员 PowerShell 执行：

```powershell
dotnet publish src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
.\deploy\windows\install-service.ps1 -PublishDirectory .\publish\win-x64 -DataDirectory C:\ProgramData\SnetEdge -AdminUsername admin -AdminPassword 'replace-with-strong-password'
```

脚本使用低权限 `LocalService` 账户，只授予数据目录修改权限，并配置三段式重启退避。反向代理终止 TLS 时保持服务仅监听回环地址。

## Docker Compose

设置 `SNET_ADMIN_USERNAME` 和 `SNET_ADMIN_PASSWORD` 后执行 `docker compose up -d --build`。容器以非 root 用户、只读根文件系统、全能力删除模式运行；SQLite、Outbox 和日志仅写入命名卷。

## 运维探针

- `/health/live`：仅判断进程事件循环可响应，适合作为存活探针。
- `/health/ready`：检查初始化、资源保护级别和 SQLite 读取，适合作为流量就绪探针。
- `/health`：兼容 Watchdog，语义等同就绪探针。
- `/metrics`：Prometheus 文本格式，包含设备状态、采集、MQTT Outbox、CPU、内存、磁盘和 SignalR 指标。
