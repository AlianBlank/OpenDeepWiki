using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 后台同步 repo-registry.json 活跃仓库：周期对比 registry active 集合与已入库仓库（按 RepoName 匹配，
/// 与导出侧 <see cref="RepoRegistryProvider.Match"/> 一致），新活跃仓自动入库（Status=Pending）→
/// RepositoryProcessingWorker 生成 → TranslationWorker 翻译并自动 Rspress 导出。
/// registry 转 inactive 只是不再入库新仓，已入库仓库不动（导出侧 Match 会跳过停用仓）。
/// <para>配置 <c>GameFrameX:AutoSync:Enabled</c>（默认 false）/
/// <c>IntervalSeconds</c>（默认 3600）/ <c>MaxNewReposPerCycle</c>（默认 3，防 registry
/// 大批量激活时生成风暴）/ <c>DefaultLanguage</c>（默认 zh）。</para>
/// </summary>
public class RepoRegistrySyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RepoRegistryProvider _registryProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RepoRegistrySyncWorker> _logger;

    public RepoRegistrySyncWorker(
        IServiceScopeFactory scopeFactory,
        RepoRegistryProvider registryProvider,
        IConfiguration configuration,
        ILogger<RepoRegistrySyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _registryProvider = registryProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bool.TryParse(_configuration["GameFrameX:AutoSync:Enabled"], out var enabled) || !enabled)
        {
            _logger.LogInformation("RepoRegistrySyncWorker 未启用（GameFrameX:AutoSync:Enabled），退出。");
            return;
        }

        var intervalSeconds = int.TryParse(_configuration["GameFrameX:AutoSync:IntervalSeconds"], out var seconds) && seconds > 0
            ? seconds
            : 3600;
        var maxPerCycle = int.TryParse(_configuration["GameFrameX:AutoSync:MaxNewReposPerCycle"], out var max) && max > 0
            ? max
            : 3;
        var defaultLanguage = _configuration["GameFrameX:AutoSync:DefaultLanguage"];
        if (string.IsNullOrWhiteSpace(defaultLanguage))
        {
            defaultLanguage = "zh";
        }

        _logger.LogInformation(
            "RepoRegistrySyncWorker started：每 {Seconds}s 同步一次，每轮最多入库 {Max} 仓。",
            intervalSeconds, maxPerCycle);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(maxPerCycle, defaultLanguage, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RepoRegistrySyncWorker 同步失败。");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("RepoRegistrySyncWorker stopped.");
    }

    private async Task SyncOnceAsync(int maxPerCycle, string defaultLanguage, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();

        var entries = _registryProvider.CollectActiveEntries();
        if (entries.Count == 0)
        {
            _logger.LogWarning("repo-registry.json 无活跃仓库或不可读，本轮跳过。");
            return;
        }

        // 注意不过滤软删除：唯一索引 (OrgName, RepoName) 含软删行，过滤会导致删后重新入库撞索引
        var existingNames = await context.Repositories
            .AsNoTracking()
            .Select(r => r.RepoName)
            .ToListAsync(cancellationToken);

        var toCreate = SelectReposToCreate(entries, existingNames, maxPerCycle);
        if (toCreate.Count == 0)
        {
            _logger.LogDebug(
                "registry 活跃仓库均已入库（活跃 {Active}，已有 {Existing}），无新增。",
                entries.Count, existingNames.Count);
            return;
        }

        var ownerUserId = await ResolveOwnerUserIdAsync(context, cancellationToken);
        if (ownerUserId is null)
        {
            _logger.LogError("registry 同步跳过：找不到 Admin 用户作为自动入库仓库的 Owner。");
            return;
        }

        _logger.LogInformation(
            "registry 同步：活跃 {Active} 仓，本轮入库 {Count} 仓（每轮上限 {Max}）。",
            entries.Count, toCreate.Count, maxPerCycle);

        foreach (var entry in toCreate)
        {
            var repositoryId = Guid.NewGuid().ToString();
            var branchId = Guid.NewGuid().ToString();
            context.Repositories.Add(new Repository
            {
                Id = repositoryId,
                OwnerUserId = ownerUserId,
                GitUrl = entry.GitUrl,
                RepoName = entry.RepoName,
                OrgName = TryResolveOrg(entry.GitUrl) ?? string.Empty,
                IsPublic = true,
                Status = RepositoryStatus.Pending
            });
            context.RepositoryBranches.Add(new RepositoryBranch
            {
                Id = branchId,
                RepositoryId = repositoryId,
                BranchName = "main"
            });
            context.BranchLanguages.Add(new BranchLanguage
            {
                Id = Guid.NewGuid().ToString(),
                RepositoryBranchId = branchId,
                LanguageCode = defaultLanguage,
                IsDefault = true
            });
            _logger.LogInformation("registry 新仓入库，等待生成：{Repo}（{GitUrl}）", entry.RepoName, entry.GitUrl);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>挑选待入库仓库：按 RepoName 去重（忽略大小写）、排除已入库、截断到每轮上限。纯函数供单测。</summary>
    internal static List<RepoRegistryEntry> SelectReposToCreate(
        IReadOnlyList<RepoRegistryEntry> entries,
        IEnumerable<string> existingNames,
        int maxPerCycle)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RepoRegistryEntry>();
        foreach (var entry in entries)
        {
            if (result.Count >= maxPerCycle)
            {
                break;
            }

            if (!seen.Add(entry.RepoName) || existing.Contains(entry.RepoName))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>取第一个 Admin 角色用户 Id 作为自动入库仓库的 Owner。</summary>
    private static async Task<string?> ResolveOwnerUserIdAsync(IContext context, CancellationToken cancellationToken)
    {
        var adminUserId = await (from user in context.Users
                                 join userRole in context.UserRoles on user.Id equals userRole.UserId
                                 join role in context.Roles on userRole.RoleId equals role.Id
                                 where role.Name == "Admin" && !user.IsDeleted
                                 select user.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return adminUserId;
    }

    /// <summary>https://github.com/GameFrameX/repo.git → GameFrameX；解析失败返回 null。</summary>
    private static string? TryResolveOrg(string gitUrl)
    {
        var trimmed = gitUrl.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var segments = trimmed.Split('/');
        return segments.Length >= 2 ? segments[^2] : null;
    }
}
