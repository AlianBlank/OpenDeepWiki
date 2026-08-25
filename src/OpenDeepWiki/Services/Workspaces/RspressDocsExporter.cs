using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>导出结果。</summary>
public record RspressExportResult(
    string RepositoryId,
    string RepoName,
    string RepoSlug,
    string OutputRoot,
    int LanguageCount,
    int FileCount,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Warnings)
{
    public bool Success => true;
}

/// <summary>
/// 把已生成好的 <see cref="Repository"/> 文档（DocCatalog/DocFile）按 Rspress 规范导出为 Markdown 文件。
/// </summary>
public interface IRspressDocsExporter
{
    /// <summary>
    /// 导出指定仓库的全部语言文档到 <paramref name="outputRoot"/>。
    /// </summary>
    /// <param name="repositoryId">仓库 Id。</param>
    /// <param name="outputRoot">输出根目录（容器内绝对路径，对应 Rspress 的 docs/ 目录）。为空则用配置默认值。</param>
    /// <param name="languageFilter">可选：只导出指定语言（如 "zh"）；为空导出全部语言。</param>
    Task<RspressExportResult> ExportAsync(
        string repositoryId,
        string? outputRoot = null,
        string? languageFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按配置自动导出（供 Worker 在生成/翻译完成后调用）：仅当 RspressExport:AutoExport:Enabled
    /// 为 true、仓库配置了 RepoPathMap 且输出根目录存在时执行；条件不满足或导出失败返回 null（只记日志）。
    /// </summary>
    Task<RspressExportResult?> TryAutoExportAsync(
        Repository repository,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RspressDocsExporter : IRspressDocsExporter
{
    private static readonly JsonSerializerOptions MetaJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IContext _context;
    private readonly RspressPathMapper _mapper;
    private readonly RepoRegistryProvider _registryProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RspressDocsExporter> _logger;

    public RspressDocsExporter(
        IContext context,
        RspressPathMapper mapper,
        RepoRegistryProvider registryProvider,
        IConfiguration configuration,
        ILogger<RspressDocsExporter> logger)
    {
        _context = context;
        _mapper = mapper;
        _registryProvider = registryProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RspressExportResult> ExportAsync(
        string repositoryId,
        string? outputRoot = null,
        string? languageFilter = null,
        CancellationToken cancellationToken = default)
    {
        // 1. 仓库
        var repo = await _context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"仓库 '{repositoryId}' 不存在。");

        // 2. 分支（取第一个）
        var branch = await _context.RepositoryBranches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.RepositoryId == repositoryId && !b.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"仓库 '{repo.RepoName}' 无分支。");

        // 3. 语言
        var languageQuery = _context.BranchLanguages
            .AsNoTracking()
            .Where(bl => bl.RepositoryBranchId == branch.Id && !bl.IsDeleted);
        if (!string.IsNullOrWhiteSpace(languageFilter))
            languageQuery = languageQuery.Where(bl => bl.LanguageCode == languageFilter);

        var languages = await languageQuery
            .OrderByDescending(bl => bl.IsDefault)
            .ThenBy(bl => bl.LanguageCode)
            .ToListAsync(cancellationToken);
        if (languages.Count == 0)
            throw new InvalidOperationException($"仓库 '{repo.RepoName}' 无可用语言文档。");

        // 4. 输出根与 registry 映射（docPath 落位 + 多语言标题 + upstream 溯源）
        outputRoot ??= _configuration["RspressExport:OutputRoot"] ?? "/data/rspress-output";
        outputRoot = Path.GetFullPath(outputRoot);
        var registryMatch = _registryProvider.Match(repo.RepoName);
        var repoSlug = registryMatch?.DocPath ?? _mapper.NormalizeRepoSlug(repo.RepoName);
        var pageMeta = registryMatch != null
            ? new RspressPageMeta(repo.RepoName, branch.LastCommitId, registryMatch.Upstream)
            : null;

        _logger.LogInformation(
            "Rspress 导出开始：{Repo} (slug={Slug})，语言 {Langs}，输出根 {Root}",
            repo.RepoName, repoSlug, string.Join(",", languages.Select(l => l.LanguageCode)), outputRoot);

        // 5. 写入器
        var writer = new LocalDocsWriter(outputRoot);
        var allWarnings = new List<string>();
        var languageCodes = new List<string>();
        var fileCount = 0;

        // 6. 逐语言导出
        foreach (var bl in languages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootCatalogs = await LoadCatalogTreeAsync(bl.Id, cancellationToken);
            if (rootCatalogs.Count == 0)
            {
                var warn = $"语言 '{bl.LanguageCode}' 无 DocCatalog，跳过。";
                allWarnings.Add(warn);
                _logger.LogWarning(warn);
                continue;
            }

            var repoTitle = ResolveTitle(registryMatch?.Titles, bl.LanguageCode, repo.RepoName);
            var site = _mapper.Map(rootCatalogs, repoSlug, bl.LanguageCode, repoTitle, pageMeta);
            languageCodes.Add(bl.LanguageCode);

            foreach (var page in site.Pages)
            {
                // _meta.json 合并磁盘上已有条目：域根层（如 .auto/components/unity/）会被多个包共享，
                // 重导一个包不能把其它包的 dir 条目冲掉
                var content = page.RelativePath.EndsWith("_meta.json", StringComparison.OrdinalIgnoreCase)
                    ? MergeMetaJsonIfExists(writer.ResolvePath(page.RelativePath), page.Content, repo.RepoName)
                    : page.Content;
                await writer.WritePageAsync(page.RelativePath, content, cancellationToken);
                fileCount++;
            }
            allWarnings.AddRange(site.Warnings);

            _logger.LogInformation(
                "语言 {Lang} 导出完成：{Pages} 个文件。",
                bl.LanguageCode, site.Pages.Count);
        }

        await writer.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Rspress 导出完成：{Repo}，共 {Files} 文件，{Langs} 语言。",
            repo.RepoName, fileCount, languageCodes.Count);

        return new RspressExportResult(
            repo.Id,
            repo.RepoName,
            repoSlug,
            outputRoot,
            languageCodes.Count,
            fileCount,
            languageCodes,
            allWarnings);
    }

    /// <summary>仓库展示名：registry packageTitles 按语言键（大小写不敏感）取值，回退 "*"（全语言通用）与仓库名。</summary>
    private static string ResolveTitle(
        IReadOnlyDictionary<string, string>? titles,
        string languageCode,
        string fallback)
    {
        if (titles != null &&
            (titles.TryGetValue(languageCode, out var title) ||
             titles.TryGetValue("*", out title)))
        {
            return title;
        }

        return fallback;
    }

    /// <summary>
    /// _meta.json 落盘前合并磁盘上已有内容：新条目在前（保持本次顺序），
    /// 旧条目中键（string 条目=自身；对象条目=name）不与新条目重复的按原顺序追加在尾部。
    /// <para>旧条目中的 "index" 刻意丢弃：现行规范 _meta.json 不列 index（目录自动关联 index.md）。</para>
    /// </summary>
    private string MergeMetaJsonIfExists(string fullPath, string incomingJson, string repoName)
    {
        if (!File.Exists(fullPath))
        {
            return incomingJson;
        }

        try
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(fullPath));
            using var incoming = JsonDocument.Parse(incomingJson);
            if (existing.RootElement.ValueKind != JsonValueKind.Array ||
                incoming.RootElement.ValueKind != JsonValueKind.Array)
            {
                return incomingJson;
            }

            var incomingItems = incoming.RootElement.EnumerateArray().ToList();
            var incomingKeys = incomingItems.Select(MetaItemKey)
                .Where(k => k != null)
                .ToHashSet()!;

            var merged = new List<JsonElement>(incomingItems);
            foreach (var item in existing.RootElement.EnumerateArray())
            {
                var key = MetaItemKey(item);
                if (key == null || key == "index" || incomingKeys.Contains(key))
                {
                    continue;
                }

                merged.Add(item);
            }

            return JsonSerializer.Serialize(merged, MetaJsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "合并 _meta.json 失败，改用新内容覆盖。Repo: {Repo}, Path: {Path}", repoName, fullPath);
            return incomingJson;
        }
    }

    private static string? MetaItemKey(JsonElement item)
        => item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : item.ValueKind == JsonValueKind.Object &&
              item.TryGetProperty("name", out var name) &&
              name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

    /// <inheritdoc />
    public async Task<RspressExportResult?> TryAutoExportAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        if (!bool.TryParse(_configuration["RspressExport:AutoExport:Enabled"], out var isEnabled) || !isEnabled)
        {
            return null;
        }

        // 只导出 repo-registry.json 登记的仓库（含 active=false 的条目——命中但停用同样不导出）
        if (_registryProvider.Match(repository.RepoName) == null)
        {
            return null;
        }

        var outputRoot = _configuration["RspressExport:OutputRoot"];
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            _logger.LogWarning(
                "自动导出 Rspress 跳过：输出根目录不存在或未配置。Repository: {Repo}, OutputRoot: {Root}",
                repository.RepoName, outputRoot ?? "(未配置)");
            return null;
        }

        try
        {
            var result = await ExportAsync(repository.Id, outputRoot, languageFilter: null, cancellationToken);
            _logger.LogInformation(
                "自动导出 Rspress 完成。Repository: {Repo}, Files: {Files}, Languages: {Langs}",
                repository.RepoName, result.FileCount, string.Join(",", result.Languages));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动导出 Rspress 失败（不影响生成结果）。Repository: {Repo}", repository.RepoName);
            return null;
        }
    }

    /// <summary>加载某语言下的 DocCatalog 树（含 DocFile 内容），在内存中按 ParentId 建树。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task<List<DocCatalog>> LoadCatalogTreeAsync(string branchLanguageId, CancellationToken cancellationToken)
    {
        // 一次性拉取该语言全部 catalog + 关联 DocFile（避免递归查询）
        var flat = await _context.DocCatalogs
            .AsNoTracking()
            .Include(c => c.DocFile)
            .Where(c => c.BranchLanguageId == branchLanguageId && !c.IsDeleted)
            .ToListAsync(cancellationToken);

        if (flat.Count == 0) return new List<DocCatalog>();

        // 内存建树
        var byId = flat.ToDictionary(c => c.Id);
        foreach (var c in flat)
            c.Children = new List<DocCatalog>(); // 重置，避免 EF fixup 残留

        foreach (var c in flat)
        {
            if (!string.IsNullOrEmpty(c.ParentId) && byId.TryGetValue(c.ParentId, out var parent))
                parent.Children.Add(c);
        }

        return flat
            .Where(c => string.IsNullOrEmpty(c.ParentId))
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .ToList();
    }
}
