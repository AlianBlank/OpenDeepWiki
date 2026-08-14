using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 后台轮询 <see cref="WorkspaceRepoGroup"/> 中 LastRunStatus = Running 的组，
/// 触发其 catalog/content 生成（C2 阶段只做骨架，实际生成复用 IWikiGenerator，留给 C3/C4 填充）。
/// </summary>
public class WorkspaceProcessingWorker : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkspaceProcessingWorker> _logger;

    public WorkspaceProcessingWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkspaceProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkspaceProcessingWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkspaceProcessingWorker iteration failed.");
            }

            try
            {
                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        _logger.LogInformation("WorkspaceProcessingWorker stopped.");
    }

    private async Task ProcessOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IContext>();

        // 找出标记为 Running 的组
        var pending = await ctx.WorkspaceRepoGroups
            .Where(g => !g.IsDeleted && g.LastRunStatus == WorkspaceGroupStatus.Running)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync(stoppingToken);

        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Workspace group {GroupId} ({Name}) pending processing (骨架阶段，跳过实际生成).",
                item.Id, item.Name);
            // C2 阶段：仅把 Running 状态推进到 Succeeded，实际生成留给 C3/C4 接 IWikiGenerator。
            try
            {
                var group = await ctx.WorkspaceRepoGroups.FindAsync([item.Id], stoppingToken);
                if (group is null) continue;
                group.LastRunStatus = WorkspaceGroupStatus.Succeeded;
                group.UpdateTimestamp();
                await ctx.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mark group {GroupId} succeeded failed.", item.Id);
            }
        }
    }
}
