using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 工作区仓组业务服务：从 repo-registry.json 导入配置、按 groupId 取组、记录运行状态。
/// </summary>
public interface IWorkspaceService
{
    /// <summary>
    /// 从 repo-registry.json 导入或更新一个组（upsert Group + Repos）。
    /// </summary>
    Task<WorkspaceRepoGroup> UpsertFromRegistryAsync(
        string registryPath,
        string groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取组（含 Repos）。
    /// </summary>
    Task<WorkspaceRepoGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出所有未软删除的组。
    /// </summary>
    Task<List<WorkspaceRepoGroup>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记本次运行开始（LastRunStatus = Running, LastRunAt = now）。
    /// </summary>
    Task MarkRunningAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记本次运行成功。
    /// </summary>
    Task MarkSucceededAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记本次运行失败。
    /// </summary>
    Task MarkFailedAsync(string groupId, string error, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceService(IContext context, ILogger<WorkspaceService> logger) : IWorkspaceService
{
    public async Task<WorkspaceRepoGroup> UpsertFromRegistryAsync(
        string registryPath,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var doc = DomainPromptRegistryLoader.LoadFromConfig(registryPath);
        if (!doc.Groups.TryGetValue(groupId, out var cfg))
            throw new InvalidOperationException(
                $"repo-registry.json groups 中找不到 '{groupId}'。可用：{string.Join(", ", doc.Groups.Keys)}");

        // upsert group
        var existing = await context.WorkspaceRepoGroups
            .Include(g => g.Repos)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        var group = existing ?? new WorkspaceRepoGroup { Id = groupId };
        group.Name = cfg.DisplayName ?? group.Name;
        group.Description = cfg.Description;
        group.BasePath = cfg.BasePath ?? group.BasePath;
        group.LanguagesCsv = cfg.LanguagesCsv ?? "en";
        group.CatalogTemplatePath = cfg.CatalogTemplatePath;
        group.DomainPromptsPath = cfg.DomainPromptsPath;
        group.OutputRoot = cfg.OutputRoot ?? group.OutputRoot;
        group.WriterType = DocsWriterType.Local; // Phase 1 默认 Local
        group.UpdateTimestamp();

        if (existing is null)
            await context.WorkspaceRepoGroups.AddAsync(group, cancellationToken);

        // upsert repos：以 (GroupId, NormalizedRepoKey) 为准
        var existingRepoKeys = group.Repos.ToDictionary(r => r.RepoKey, StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var entry in cfg.Repos.Where(r => r.Active))
        {
            var key = entry.NormalizedRepoKey;
            if (string.IsNullOrWhiteSpace(key)) continue;
            entry.DisplayOrder = entry.DisplayOrder > 0 ? entry.DisplayOrder : order++;

            if (existingRepoKeys.TryGetValue(key, out var repo))
            {
                repo.GitUrl = entry.GitUrl ?? repo.GitUrl;
                repo.LocalPath = entry.LocalPath ?? repo.LocalPath;
                repo.Domain = entry.Domain ?? repo.Domain;
                repo.Branch = entry.Branch ?? repo.Branch;
                repo.Active = entry.Active;
                repo.DisplayOrder = entry.DisplayOrder;
                repo.UpdateTimestamp();
            }
            else
            {
                var newRepo = new RepoRef
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GroupId = groupId,
                    RepoKey = key,
                    GitUrl = entry.GitUrl,
                    LocalPath = entry.LocalPath,
                    Domain = entry.Domain ?? "tools",
                    Branch = entry.Branch ?? "main",
                    Active = entry.Active,
                    DisplayOrder = entry.DisplayOrder
                };
                group.Repos.Add(newRepo);
                await context.RepoRefs.AddAsync(newRepo, cancellationToken);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Upserted workspace group {GroupId} with {RepoCount} repos",
            groupId, group.Repos.Count);

        return group;
    }

    public Task<WorkspaceRepoGroup?> GetAsync(string groupId, CancellationToken cancellationToken = default)
        => context.WorkspaceRepoGroups
            .Include(g => g.Repos.Where(r => !r.IsDeleted).OrderBy(r => r.DisplayOrder))
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsDeleted, cancellationToken);

    public Task<List<WorkspaceRepoGroup>> ListAsync(CancellationToken cancellationToken = default)
        => context.WorkspaceRepoGroups
            .Where(g => !g.IsDeleted)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);

    public async Task MarkRunningAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var g = await context.WorkspaceRepoGroups.FindAsync([groupId], cancellationToken);
        if (g is null) return;
        g.LastRunAt = DateTime.UtcNow;
        g.LastRunStatus = WorkspaceGroupStatus.Running;
        g.LastRunError = null;
        g.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSucceededAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var g = await context.WorkspaceRepoGroups.FindAsync([groupId], cancellationToken);
        if (g is null) return;
        g.LastRunStatus = WorkspaceGroupStatus.Succeeded;
        g.LastRunError = null;
        g.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(string groupId, string error, CancellationToken cancellationToken = default)
    {
        var g = await context.WorkspaceRepoGroups.FindAsync([groupId], cancellationToken);
        if (g is null) return;
        g.LastRunStatus = WorkspaceGroupStatus.Failed;
        g.LastRunError = error.Length > 2000 ? error[..2000] : error;
        g.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
    }
}
