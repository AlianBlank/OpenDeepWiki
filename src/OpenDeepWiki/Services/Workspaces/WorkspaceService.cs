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
    /// groupId 为 v4 目录树的节点路径（'/' 分隔，如 "gfx-core" 或 "gfx-core/client"），聚合该节点子树全部活跃仓。
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
        var node = FindNode(doc.Groups, groupId);
        if (node is null)
            throw new InvalidOperationException(
                $"repo-registry.json 中找不到组 '{groupId}'（groupId 为 v4 节点路径，如 'gfx-core/client'）。可用：{string.Join(", ", ListNodePaths(doc.Groups))}");

        // upsert group
        var existing = await context.WorkspaceRepoGroups
            .Include(g => g.Repos)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        var group = existing ?? new WorkspaceRepoGroup { Id = groupId };
        group.Name = node.DisplayName ?? node.Name;
        group.Description = node.Note ?? group.Description;
        group.WriterType = DocsWriterType.Local; // Phase 1 默认 Local
        group.UpdateTimestamp();

        if (existing is null)
            await context.WorkspaceRepoGroups.AddAsync(group, cancellationToken);

        // upsert repos：以 (GroupId, NormalizedRepoKey) 为准，聚合命中节点子树全部活跃仓
        var existingRepoKeys = group.Repos.ToDictionary(r => r.RepoKey, StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var entry in CollectActiveRepositories(node).Where(r => r.Active))
        {
            var key = entry.NormalizedRepoKey;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var displayOrder = order++;

            if (existingRepoKeys.TryGetValue(key, out var repo))
            {
                repo.GitUrl = entry.GitUrl ?? repo.GitUrl;
                repo.Active = entry.Active;
                repo.DisplayOrder = displayOrder;
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
                    Active = entry.Active,
                    DisplayOrder = displayOrder
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

    /// <summary>按 '/' 分隔的节点路径寻址（如 "gfx-core"、"gfx-core/client"），在 v4 目录树中找节点。</summary>
    private static RegistryNode? FindNode(List<RegistryNode> nodes, string groupId)
    {
        var segments = groupId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : FindNodeCore(nodes, segments, 0);
    }

    private static RegistryNode? FindNodeCore(List<RegistryNode> nodes, string[] segments, int index)
    {
        foreach (var node in nodes)
        {
            if (!string.Equals(node.Name, segments[index], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index == segments.Length - 1)
            {
                return node;
            }

            var child = FindNodeCore(node.Children, segments, index + 1);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>聚合节点子树全部 repository 条目；节点 active=false 的子树整棵跳过（v4 停用语义）。</summary>
    private static IEnumerable<RepoEntry> CollectActiveRepositories(RegistryNode node)
    {
        if (node.Active == false)
        {
            yield break;
        }

        foreach (var entry in node.Repositories)
        {
            yield return entry;
        }

        foreach (var child in node.Children)
        {
            foreach (var entry in CollectActiveRepositories(child))
            {
                yield return entry;
            }
        }
    }

    /// <summary>列出全部可寻址节点路径（含中间节点），用于错误提示。</summary>
    private static IEnumerable<string> ListNodePaths(List<RegistryNode> nodes, string prefix = "")
    {
        foreach (var node in nodes)
        {
            var path = prefix.Length == 0 ? node.Name : prefix + "/" + node.Name;
            yield return path;
            foreach (var childPath in ListNodePaths(node.Children, path))
            {
                yield return childPath;
            }
        }
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
