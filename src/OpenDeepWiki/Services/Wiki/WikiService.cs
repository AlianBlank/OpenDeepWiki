using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Infrastructure;
using OpenDeepWiki.Models;

namespace OpenDeepWiki.Services.Wiki;

[MiniApi(Route = "/api/v1/wiki")]
[Tags("Wiki")]
public class WikiService(IContext context)
{
    private const string DefaultLanguageCode = "zh";

    /// <summary>
    /// 获取 Wiki 目录结构
    /// </summary>
    [HttpGet("/{org}/{repo}/catalog")]
    public async Task<WikiCatalogResponse> GetCatalogAsync(string org, string repo)
    {
        (org, repo) = RepositoryRouteDecoder.DecodeOwnerAndRepo(org, repo);
        var repository = await GetRepositoryAsync(org, repo);
        var branch = await GetDefaultBranchAsync(repository.Id);
        var language = await GetDefaultLanguageAsync(branch.Id);

        var catalogs = await context.DocCatalogs
            .AsNoTracking()
            .Where(c => c.BranchLanguageId == language.Id)
            .OrderBy(c => c.Order)
            .ToListAsync();

        if (catalogs.Count == 0)
        {
            throw new InvalidOperationException("未找到 Wiki 目录");
        }

        var rootItems = BuildCatalogTree(catalogs);
        var defaultPath = FindFirstPath(rootItems);

        return new WikiCatalogResponse
        {
            Owner = repository.OrgName,
            Repo = repository.RepoName,
            DefaultPath = defaultPath,
            Items = rootItems
        };
    }

    /// <summary>
    /// 获取 Wiki 文档内容
    /// </summary>
    [HttpGet("/{org}/{repo}/doc/{*path}")]
    public async Task<WikiDocResponse> GetDocAsync(string org, string repo, string path)
    {
        (org, repo) = RepositoryRouteDecoder.DecodeOwnerAndRepo(org, repo);
        var repository = await GetRepositoryAsync(org, repo);
        var branch = await GetDefaultBranchAsync(repository.Id);
        var language = await GetDefaultLanguageAsync(branch.Id);
        var normalizedPath = NormalizePath(path);

        var catalog = await context.DocCatalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BranchLanguageId == language.Id && c.Path == normalizedPath);

        if (catalog is null)
        {
            throw new KeyNotFoundException($"文档路径 '{normalizedPath}' 不存在");
        }

        if (string.IsNullOrEmpty(catalog.DocFileId))
        {
            throw new KeyNotFoundException($"文档路径 '{normalizedPath}' 没有关联的文档内容");
        }

        var docFile = await context.DocFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == catalog.DocFileId);

        if (docFile is null)
        {
            throw new KeyNotFoundException($"文档内容不存在");
        }

        return new WikiDocResponse
        {
            Path = normalizedPath,
            Title = catalog.Title,
            Content = docFile.Content
        };
    }

    private async Task<Repository> GetRepositoryAsync(string org, string repo)
    {
        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrgName == org && r.RepoName == repo);

        if (repository is null)
        {
            throw new KeyNotFoundException($"仓库 '{org}/{repo}' 不存在");
        }

        return repository;
    }

    private async Task<RepositoryBranch> GetDefaultBranchAsync(string repositoryId)
    {
        var branches = await context.RepositoryBranches
            .AsNoTracking()
            .Where(b => b.RepositoryId == repositoryId)
            .ToListAsync();

        if (branches.Count == 0)
        {
            throw new KeyNotFoundException("仓库分支不存在");
        }

        return branches.FirstOrDefault(b => string.Equals(b.BranchName, "main", StringComparison.OrdinalIgnoreCase))
               ?? branches.FirstOrDefault(b => string.Equals(b.BranchName, "master", StringComparison.OrdinalIgnoreCase))
               ?? branches.OrderBy(b => b.CreatedAt).First();
    }

    private async Task<BranchLanguage> GetDefaultLanguageAsync(string branchId)
    {
        var languages = await context.BranchLanguages
            .AsNoTracking()
            .Where(l => l.RepositoryBranchId == branchId)
            .ToListAsync();

        if (languages.Count == 0)
        {
            throw new KeyNotFoundException("仓库语言不存在");
        }

        // 优先使用默认语言，但如果默认语言没有目录，则使用第一个有目录的语言
        var defaultLanguage = languages.FirstOrDefault(l => string.Equals(l.LanguageCode, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase));

        if (defaultLanguage != null)
        {
            // 检查默认语言是否有目录
            var hasCatalogs = await context.DocCatalogs
                .AsNoTracking()
                .AnyAsync(c => c.BranchLanguageId == defaultLanguage.Id);

            if (hasCatalogs)
            {
                return defaultLanguage;
            }
        }

        // 默认语言没有目录，查找第一个有目录的语言
        foreach (var lang in languages.OrderBy(l => l.CreatedAt))
        {
            var hasCatalogs = await context.DocCatalogs
                .AsNoTracking()
                .AnyAsync(c => c.BranchLanguageId == lang.Id);

            if (hasCatalogs)
            {
                return lang;
            }
        }

        // 没有任何语言有目录，返回第一个语言
        return languages.OrderBy(l => l.CreatedAt).First();
    }

    private static List<WikiCatalogItemResponse> BuildCatalogTree(List<DocCatalog> catalogs)
    {
        // 注意: 数据库中根目录的 ParentId 可能是空字符串 "" 而非 NULL
        var lookup = catalogs.ToLookup(c => c.ParentId ?? string.Empty);
        return BuildChildren(lookup, null);
    }

    private static List<WikiCatalogItemResponse> BuildChildren(ILookup<string, DocCatalog> lookup, string? parentId)
    {
        // 处理 null 和空字符串的情况
        var effectiveParentId = parentId ?? string.Empty;
        return lookup[effectiveParentId]
            .OrderBy(c => c.Order)
            .Select(c => new WikiCatalogItemResponse
            {
                Title = c.Title,
                Path = c.Path,
                Order = c.Order,
                HasContent = !string.IsNullOrEmpty(c.DocFileId),
                Children = BuildChildren(lookup, c.Id)
            })
            .ToList();
    }

    private static string FindFirstPath(List<WikiCatalogItemResponse> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var first = items.OrderBy(i => i.Order).First();
        return first.HasContent ? first.Path : FindFirstPath(first.Children);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Trim('/');
    }

}
