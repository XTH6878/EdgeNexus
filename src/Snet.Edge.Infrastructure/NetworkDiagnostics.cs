using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class NetworkDiagnosticsService(
    INetworkConfigurationService networkConfiguration,
    IDeviceDriverFactory driverFactory,
    ILogger<NetworkDiagnosticsService> logger) : INetworkDiagnosticsService
{
    public async ValueTask<NetworkDiagnosticResult> PingAsync(
        string host,
        NetworkUsage usage,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var timeout = Math.Clamp(timeoutMs, 100, 10_000);
        var sourceAddress = networkConfiguration.ResolveLocalIpv4(usage);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (sourceAddress is null)
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(
                    host,
                    TimeSpan.FromMilliseconds(timeout),
                    cancellationToken: cancellationToken);
                return new NetworkDiagnosticResult(
                    reply.Status == IPStatus.Success,
                    reply.Status.ToString(),
                    stopwatch.ElapsedMilliseconds,
                    null,
                    host,
                    reply.Status == IPStatus.Success ? null : reply.Status.ToString());
            }

            return await PingFromSourceAsync(host, usage, sourceAddress, timeout, stopwatch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "网络Ping诊断失败，目标 {Host}", host);
            return Failure("PingError", stopwatch, sourceAddress, host, exception.Message);
        }
    }

    public async ValueTask<NetworkDiagnosticResult> TestTcpAsync(
        string host,
        int port,
        NetworkUsage usage,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var sourceAddress = networkConfiguration.ResolveLocalIpv4(usage);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 100, 30_000));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (sourceAddress is not null)
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse(sourceAddress), 0));
            }

            await socket.ConnectAsync(host, port, timeout.Token);
            return Success("TcpConnected", stopwatch, sourceAddress, $"{host}:{port}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure("TcpFailed", stopwatch, sourceAddress, $"{host}:{port}", NormalizeException(exception));
        }
    }

    public async ValueTask<NetworkDiagnosticResult> TestMqttAsync(
        MqttOptions options,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var sourceAddress = networkConfiguration.ResolveLocalIpv4(NetworkUsage.MqttUpload);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 500, 30_000));
            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();
            var clientOptions = MqttNetworkOptionsFactory.Build(
                options with { ClientId = $"{options.ClientId}_diag_{Guid.NewGuid():N}"[..Math.Min(options.ClientId.Length + 14, 64)] },
                sourceAddress,
                includeWill: false);
            var result = await client.ConnectAsync(clientOptions, timeout.Token);
            if (client.IsConnected)
            {
                await client.DisconnectAsync(cancellationToken: timeout.Token);
            }

            return result.ResultCode == MqttClientConnectResultCode.Success
                ? Success("MqttConnected", stopwatch, sourceAddress, $"{options.Host}:{options.Port}")
                : Failure("MqttRejected", stopwatch, sourceAddress, $"{options.Host}:{options.Port}", result.ResultCode.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure("MqttFailed", stopwatch, sourceAddress, $"{options.Host}:{options.Port}", NormalizeException(exception));
        }
    }

    public async ValueTask<NetworkDiagnosticResult> TestPlcAsync(
        DeviceDefinition device,
        IReadOnlyList<TagDefinition> tags,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var sourceAddress = networkConfiguration.ResolveLocalIpv4(NetworkUsage.PlcAcquisition);
        var stopwatch = Stopwatch.StartNew();
        var steps = new List<NetworkDiagnosticStep>(3);
        var isMitsubishiUdp = device.Protocol == ProtocolKind.MitsubishiMc
            && device.MitsubishiTransport == MitsubishiMcTransport.Udp;
        if (!isMitsubishiUdp)
        {
            var tcpResult = await TestTcpAsync(
                device.Host,
                device.Port,
                NetworkUsage.PlcAcquisition,
                timeoutMs,
                cancellationToken);
            steps.Add(new NetworkDiagnosticStep(
                "TCP端口",
                tcpResult.Success,
                tcpResult.Success ? $"{device.Host}:{device.Port} 可达" : tcpResult.Error ?? tcpResult.Status,
                tcpResult.DurationMs));
            if (!tcpResult.Success)
            {
                return new NetworkDiagnosticResult(
                    false,
                    "TcpPortUnreachable",
                    stopwatch.ElapsedMilliseconds,
                    sourceAddress,
                    $"{device.Name} ({device.Host}:{device.Port})",
                    tcpResult.Error,
                    steps,
                    "检查PLC IP、端口、网线、防火墙以及PLC采集网络绑定；西门子 S7 TCP 默认端口为102。");
            }
        }
        else
        {
            steps.Add(new NetworkDiagnosticStep(
                "UDP传输",
                true,
                "UDP为无连接传输，不执行TCP端口探测",
                0));
        }

        await using var driver = driverFactory.Create(device, tags);
        var handshakeStopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 500, 30_000));
            await driver.ConnectAsync(timeout.Token);
            steps.Add(new NetworkDiagnosticStep(
                isMitsubishiUdp ? "UDP套接字" : "协议握手",
                true,
                isMitsubishiUdp ? "本地UDP套接字已就绪" : $"{device.Protocol} 握手成功",
                handshakeStopwatch.ElapsedMilliseconds));
            if (tags.Count > 0)
            {
                var readStopwatch = Stopwatch.StartNew();
                var value = await driver.ReadAsync(tags[0], timeout.Token);
                steps.Add(new NetworkDiagnosticStep(
                    "点位读取",
                    true,
                    $"{tags[0].Name} ({tags[0].Address}) 读取成功，值：{value}",
                    readStopwatch.ElapsedMilliseconds));
            }
            else if (isMitsubishiUdp)
            {
                steps.Add(new NetworkDiagnosticStep(
                    "点位读取",
                    true,
                    "未配置启用点位，UDP无握手，当前只能确认本地套接字可用",
                    0));
            }
            await driver.DisconnectAsync(timeout.Token);
            return new NetworkDiagnosticResult(
                true,
                isMitsubishiUdp && tags.Count == 0 ? "UdpSocketReady" : "PlcConnected",
                stopwatch.ElapsedMilliseconds,
                sourceAddress,
                $"{device.Name} ({device.Host}:{device.Port})",
                Steps: steps,
                Recommendation: device.Protocol == ProtocolKind.SiemensS7
                    ? SiemensConnectionProfile.Resolve(device).Recommendation
                    : isMitsubishiUdp && tags.Count == 0
                        ? "UDP无连接握手，当前仅确认本机套接字可用；请保存点位后再次测试，以PLC返回报文作为最终通信依据。"
                        : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var error = NormalizeException(exception);
            steps.Add(new NetworkDiagnosticStep(
                tags.Count > 0 ? "点位读取" : isMitsubishiUdp ? "UDP套接字" : "协议握手",
                false,
                error,
                handshakeStopwatch.ElapsedMilliseconds));
            return new NetworkDiagnosticResult(
                false,
                device.Protocol == ProtocolKind.SiemensS7 ? "S7HandshakeFailed" : "PlcHandshakeFailed",
                stopwatch.ElapsedMilliseconds,
                sourceAddress,
                $"{device.Name} ({device.Host}:{device.Port})",
                Limit(error, 1_000),
                steps,
                BuildPlcRecommendation(device));
        }
    }

    private static string BuildPlcRecommendation(DeviceDefinition device)
    {
        if (device.Protocol == ProtocolKind.BeckhoffAds)
        {
            return "TCP链路已建立但ADS握手失败；确认TwinCAT处于RUN、目标AMS端口为TC2的801或TC3的851，并核对AMS Route及双方Net ID。";
        }

        if (device.Protocol == ProtocolKind.AllenBradleyEtherNetIp)
        {
            return "TCP链路已建立但AB EtherNet/IP会话失败；核对CPU槽号、CIP路由、标签External Access以及控制器连接资源。";
        }

        if (device.Protocol == ProtocolKind.MitsubishiMc && device.MitsubishiTransport == MitsubishiMcTransport.Udp)
        {
            return "核对PLC侧开放方式为UDP、端口及Binary/ASCII 3E格式完全一致；UDP无握手，必须以有效点位读取结果判断通信是否正常。";
        }

        if (device.Protocol != ProtocolKind.SiemensS7)
        {
            return "TCP链路已建立，请核对PLC协议类型、站号及控制器侧通讯权限。";
        }

        var profile = SiemensConnectionProfile.Resolve(device);
        return $"TCP端口已连通但S7握手失败。{profile.Recommendation}；核对PLC系列、连接角色、TSAP。S7-1200/1500还需在TIA Portal启用PUT/GET通信并允许当前访问级别。";
    }

    private async ValueTask<NetworkDiagnosticResult> PingFromSourceAsync(
        string host,
        NetworkUsage usage,
        string sourceAddress,
        int timeoutMs,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ping",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(sourceAddress);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-W");
            startInfo.ArgumentList.Add(Math.Max(1, (int)Math.Ceiling(timeoutMs / 1_000D)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(networkConfiguration.ResolveInterfaceName(usage) ?? sourceAddress);
        }

        startInfo.ArgumentList.Add(host);
        using var process = Process.Start(startInfo) ?? throw new IOException("无法启动系统Ping工具。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs + 1_000);
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = await process.StandardError.ReadToEndAsync(timeout.Token);
        return process.ExitCode == 0
            ? Success("Success", stopwatch, sourceAddress, host)
            : Failure("PingFailed", stopwatch, sourceAddress, host, Limit(string.IsNullOrWhiteSpace(error) ? output : error, 500));
    }

    private static NetworkDiagnosticResult Success(string status, Stopwatch stopwatch, string? source, string target) =>
        new(true, status, stopwatch.ElapsedMilliseconds, source, target);

    private static NetworkDiagnosticResult Failure(string status, Stopwatch stopwatch, string? source, string? target, string error) =>
        new(false, status, stopwatch.ElapsedMilliseconds, source, target, Limit(error, 1_000));

    private static string NormalizeException(Exception exception) => exception switch
    {
        OperationCanceledException => "连接超时。",
        SocketException socketException => $"Socket错误 {socketException.SocketErrorCode}: {socketException.Message}",
        _ => exception.Message
    };

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}

internal static class MqttNetworkOptionsFactory
{
    public static MqttClientOptions Build(MqttOptions options, string? localIpv4, bool includeWill = true)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(options.ClientId)
            .WithTcpServer(options.Host, options.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
            .WithCleanSession(false);

        if (includeWill)
        {
            builder
                .WithWillTopic(options.LastWillTopic)
                .WithWillPayload(options.LastWillPayload)
                .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain(true);
        }

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            builder.WithCredentials(options.Username, options.Password);
        }

        if (options.UseTls)
        {
            builder.WithTlsOptions(new MqttClientTlsOptions { UseTls = true });
        }

        var clientOptions = builder.Build();
        if (localIpv4 is not null && clientOptions.ChannelOptions is MqttClientTcpOptions tcpOptions)
        {
            tcpOptions.LocalEndpoint = new IPEndPoint(IPAddress.Parse(localIpv4), 0);
        }

        return clientOptions;
    }
}
