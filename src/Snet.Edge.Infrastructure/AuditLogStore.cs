using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class AuditLogStore(
    string connectionString,
    ILogger<AuditLogStore> logger) : BackgroundService, IAuditLogStore
{
    private readonly Channel<AuditEvent> _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS audit_log (
                id TEXT NOT NULL PRIMARY KEY,
                trace_id TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                status_code INTEGER NOT NULL,
                actor TEXT NOT NULL,
                remote_address TEXT NULL,
                elapsed_ms INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_log_created_at ON audit_log(created_at_utc DESC);
            """,
            cancellationToken: cancellationToken));
    }

    public bool TryWrite(AuditEvent auditEvent)
    {
        var accepted = _channel.Writer.TryWrite(auditEvent);
        if (!accepted)
        {
            logger.LogError("审计日志队列已满，拒绝事件 {TraceId} {Method} {Path}", auditEvent.TraceId, auditEvent.Method, auditEvent.Path);
        }

        return accepted;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        try
        {
            if (ExecuteTask is not null)
            {
                await ExecuteTask.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("网关关闭窗口已到期，正在取消审计日志后台任务");
        }

        await base.StopAsync(CancellationToken.None);
    }

    public async ValueTask<IReadOnlyList<AuditEvent>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT id, trace_id AS TraceId, method, path, status_code AS StatusCode,
                   actor, remote_address AS RemoteAddress, elapsed_ms AS ElapsedMilliseconds,
                   created_at_utc AS CreatedAtUtc
            FROM audit_log
            ORDER BY created_at_utc DESC
            LIMIT @Take;
            """,
            new { Take = Math.Clamp(take, 1, 1_000) },
            cancellationToken: cancellationToken));
        return rows.Select(row => new AuditEvent(
            Guid.Parse(row.Id),
            row.TraceId,
            row.Method,
            row.Path,
            checked((int)row.StatusCode),
            row.Actor,
            row.RemoteAddress,
            row.ElapsedMilliseconds,
            DateTimeOffset.Parse(row.CreatedAtUtc))).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEvent>(100);
        try
        {
            await foreach (var auditEvent in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                batch.Add(auditEvent);
                while (batch.Count < 100 && _channel.Reader.TryRead(out var next))
                {
                    batch.Add(next);
                }

                var persisted = false;
                while (!persisted && !stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await PersistBatchAsync(batch, stoppingToken);
                        persisted = true;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogError(exception, "审计日志批量写入失败，将在1秒后重试");
                        await Task.Delay(1_000, stoppingToken);
                    }
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask PersistBatchAsync(IReadOnlyCollection<AuditEvent> batch, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_log(
                id, trace_id, method, path, status_code, actor,
                remote_address, elapsed_ms, created_at_utc)
            VALUES (
                @Id, @TraceId, @Method, @Path, @StatusCode, @Actor,
                @RemoteAddress, @ElapsedMilliseconds, @CreatedAtUtc);
            """,
            batch.Select(item => new
            {
                Id = item.Id.ToString("N"),
                item.TraceId,
                item.Method,
                item.Path,
                item.StatusCode,
                item.Actor,
                item.RemoteAddress,
                item.ElapsedMilliseconds,
                CreatedAtUtc = item.CreatedAt.ToString("O")
            }),
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record AuditRow(
        string Id,
        string TraceId,
        string Method,
        string Path,
        long StatusCode,
        string Actor,
        string? RemoteAddress,
        long ElapsedMilliseconds,
        string CreatedAtUtc);
}
