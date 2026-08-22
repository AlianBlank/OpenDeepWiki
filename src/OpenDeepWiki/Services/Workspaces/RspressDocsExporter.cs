using System.Runtime.CompilerServices;
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
}

/// <inheritdoc />
public sealed class RspressDocsExporter : IRspressDocsExporter
{
    private readonly IContext _context;
    private readonly RspressPathMapper _mapper;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RspressDocsExporter> _logger;

    public RspressDocsExporter(
        IContext context,
        RspressPathMapper mapper,
        IConfiguration configuration,
        ILogger<RspressDocsExporter> logger)
    {
        _context = context;
        _mapper = mapper;
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

        // 4. 输出根
        outputRoot ??= _configuration["RspressExport:OutputRoot"] ?? "/data/rspress-output";
        outputRoot = Path.GetFullPath(outputRoot);
        var repoSlug = _mapper.NormalizeRepoSlug(repo.RepoName);

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

            var site = _mapper.Map(rootCatalogs, repoSlug, bl.LanguageCode, repo.RepoName);
            languageCodes.Add(bl.LanguageCode);

            foreach (var page in site.Pages)
            {
                await writer.WritePageAsync(page.RelativePath, page.Content, cancellationToken);
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
