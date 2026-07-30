using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed record ResourceProtectionOptions
{
    public double DiskWarningPercent { get; init; } = 75;
    public double DiskRestrictedPercent { get; init; } = 85;
    public double DiskCriticalPercent { get; init; } = 95;
    public long MemoryWarningMb { get; init; } = 2_048;
    public long MemoryRestrictedMb { get; init; } = 3_072;
    public long MemoryCriticalMb { get; init; } = 4_096;
}

public sealed class ResourceGuard : IResourceGuard
{
    private ResourceSnapshot _current = new(
        ResourceProtectionLevel.Normal,
        0,
        0,
        0,
        0,
        long.MaxValue,
        0,
        DateTimeOffset.UtcNow,
        null);

    public ResourceSnapshot Current => Volatile.Read(ref _current);

    public bool AllowAcquisition => Current.Level != ResourceProtectionLevel.Critical;

    public bool AllowPersistentEnqueue => Current.Level != ResourceProtectionLevel.Critical;

    public void Update(ResourceSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}

public sealed class ResourceMonitorWorker(
    ResourceGuard resourceGuard,
    ResourceProtectionOptions options,
    string dataDirectory,
    ILogger<ResourceMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var previousCpu = process.TotalProcessorTime;
        var previousAt = Stopwatch.GetTimestamp();
        var previousLevel = ResourceProtectionLevel.Normal;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    process.Refresh();
                    var nowAt = Stopwatch.GetTimestamp();
                    var totalCpu = process.TotalProcessorTime;
                    var elapsedSeconds = Stopwatch.GetElapsedTime(previousAt, nowAt).TotalSeconds;
                    var cpuPercent = elapsedSeconds <= 0
                        ? 0
                        : (totalCpu - previousCpu).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100;
                    previousCpu = totalCpu;
                    previousAt = nowAt;

                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDirectory))!);
                    var usedPercent = drive.TotalSize == 0
                        ? 0
                        : (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
                    var workingSetMb = process.WorkingSet64 / 1024 / 1024;
                    var level = ResolveLevel(usedPercent, workingSetMb);
                    var reason = ResolveReason(level, usedPercent, workingSetMb);
                    var snapshot = new ResourceSnapshot(
                        level,
                        Math.Round(Math.Clamp(cpuPercent, 0, 100), 2),
                        workingSetMb,
                        GC.GetGCMemoryInfo().HeapSizeBytes / 1024 / 1024,
                        Math.Round(usedPercent, 2),
                        drive.AvailableFreeSpace / 1024 / 1024,
                        process.Threads.Count,
                        DateTimeOffset.UtcNow,
                        reason);
                    resourceGuard.Update(snapshot);

                    if (level != previousLevel)
                    {
                        logger.LogWarning(
                            "资源保护等级从 {PreviousLevel} 切换为 {CurrentLevel}: {Reason}",
                            previousLevel,
                            level,
                            reason ?? "资源恢复正常");
                        previousLevel = level;
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "资源指标采样失败，本周期保持上一次保护状态");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private ResourceProtectionLevel ResolveLevel(double diskPercent, long memoryMb)
    {
        if (diskPercent >= options.DiskCriticalPercent || memoryMb >= options.MemoryCriticalMb)
        {
            return ResourceProtectionLevel.Critical;
        }

        if (diskPercent >= options.DiskRestrictedPercent || memoryMb >= options.MemoryRestrictedMb)
        {
            return ResourceProtectionLevel.Restricted;
        }

        return diskPercent >= options.DiskWarningPercent || memoryMb >= options.MemoryWarningMb
            ? ResourceProtectionLevel.Warning
            : ResourceProtectionLevel.Normal;
    }

    private string? ResolveReason(ResourceProtectionLevel level, double diskPercent, long memoryMb) => level switch
    {
        ResourceProtectionLevel.Critical => $"资源临界：磁盘 {diskPercent:F1}%，内存 {memoryMb}MB",
        ResourceProtectionLevel.Restricted => $"资源受限：磁盘 {diskPercent:F1}%，内存 {memoryMb}MB",
        ResourceProtectionLevel.Warning => $"资源预警：磁盘 {diskPercent:F1}%，内存 {memoryMb}MB",
        _ => null
    };
}
