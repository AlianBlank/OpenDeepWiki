using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>repo-registry.json 匹配结果：Docs 落位路径 + 语言级展示标题 + 上游溯源。</summary>
public record RepoRegistryMatch(
    string DocPath,
    IReadOnlyDictionary<string, string>? Titles,
    string? Upstream);

/// <summary>registry 活跃仓库条目（RepoName 为 gitUrl 尾段，已剥 .git）。</summary>
public record RepoRegistryEntry(string RepoName, string GitUrl);

/// <summary>
/// 读取 Docs 仓 <c>gfx-config/repo-registry.json</c>（v4 目录树结构），把 OpenDeepWiki 仓库名解析为
/// Docs 落位路径（docPath）与多语言展示标题。
/// <para>每次匹配都重读文件（registry 小、导出低频）：改 registry 即时生效，无需重启。</para>
/// <para>v4 匹配规则：groups 为节点数组递归遍历（组 → 子组 → 仓库），节点 active=false 时停用
/// 整棵子树；全部仓显式登记，条目以 gitUrl 尾段 == 仓库名命中；docPath / titles / upstream 均取自
/// 条目本身（titles 支持 object 按语言键与 string 全语言简写）。未命中返回 null（不导出）。</para>
/// </summary>
public sealed class RepoRegistryProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RepoRegistryProvider> _logger;

    public RepoRegistryProvider(IConfiguration configuration, ILogger<RepoRegistryProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>按仓库名匹配 registry；未命中或 registry 不可读返回 null。</summary>
    public RepoRegistryMatch? Match(string repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName))
        {
            return null;
        }

        var registry = TryLoadRegistry();
        if (registry is null)
        {
            return null;
        }

        using (registry)
        {
            return MatchCore(registry.RootElement, repoName);
        }
    }

    /// <summary>
    /// 收集全部活跃仓库条目（节点/条目 active=false 时整树剪枝，与 <see cref="Match"/> 同一套停用语义）。
    /// registry 不可读或缺少 groups 数组时返回空列表。
    /// </summary>
    public IReadOnlyList<RepoRegistryEntry> CollectActiveEntries()
    {
        var registry = TryLoadRegistry();
        if (registry is null)
        {
            return Array.Empty<RepoRegistryEntry>();
        }

        using (registry)
        {
            if (!registry.RootElement.TryGetProperty("groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("repo-registry.json 缺少 groups 数组，无法收集活跃仓库。");
                return Array.Empty<RepoRegistryEntry>();
            }

            var entries = new List<RepoRegistryEntry>();
            CollectNodes(groups, entries);
            return entries;
        }
    }

    /// <summary>读取并解析 registry 文件；不存在或解析失败返回 null（只记日志）。</summary>
    private JsonDocument? TryLoadRegistry()
    {
        var registryPath = _configuration["RspressExport:RegistryPath"]
                           ?? "/host-gfx-docs/gfx-config/repo-registry.json";
        if (!File.Exists(registryPath))
        {
            _logger.LogWarning("repo-registry.json 不存在，跳过 registry 映射。Path: {Path}", registryPath);
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(registryPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "repo-registry.json 解析失败。Path: {Path}", registryPath);
            return null;
        }
    }

    /// <summary>递归收集活跃条目；节点 active=false 剪枝整棵子树，条目 active=false 跳过自身。</summary>
    private static void CollectNodes(JsonElement nodes, List<RepoRegistryEntry> entries)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("active", out var nodeActive) && !nodeActive.GetBoolean())
            {
                continue;
            }

            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                CollectNodes(children, entries);
            }

            if (node.TryGetProperty("repositories", out var repos) && repos.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in repos.EnumerateArray())
                {
                    if (r.TryGetProperty("active", out var active) && !active.GetBoolean())
                    {
                        continue;
                    }

                    var gitUrl = r.GetProperty("gitUrl").GetString();
                    var repoName = RepoNameFromGitUrl(gitUrl);
                    if (repoName is null)
                    {
                        continue;
                    }

                    entries.Add(new RepoRegistryEntry(repoName, gitUrl!));
                }
            }
        }
    }

    private RepoRegistryMatch? MatchCore(JsonElement root, string repoName)
    {
        if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            return WalkNodes(groups, repoName);
        }

        return null;
    }

    /// <summary>递归遍历 v4 目录树节点；节点 active=false 时剪枝整棵子树（含子分组与全部仓库）。</summary>
    private RepoRegistryMatch? WalkNodes(JsonElement nodes, string repoName)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("active", out var nodeActive) && !nodeActive.GetBoolean())
            {
                continue;
            }

            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var matched = WalkNodes(children, repoName);
                if (matched != null)
                {
                    return matched;
                }
            }

            if (node.TryGetProperty("repositories", out var repos) && repos.ValueKind == JsonValueKind.Array)
            {
                var matched = MatchRepositories(repos, repoName);
                if (matched != null)
                {
                    return matched;
                }
            }
        }

        return null;
    }

    /// <summary>节点 repositories 条目匹配：gitUrl 尾段（去 .git）== 仓库名；命中但停用返回 null（不导出）。</summary>
    private RepoRegistryMatch? MatchRepositories(JsonElement repos, string repoName)
    {
        foreach (var r in repos.EnumerateArray())
        {
            if (RepoNameFromGitUrl(r.GetProperty("gitUrl").GetString()) != repoName)
            {
                continue;
            }

            if (r.TryGetProperty("active", out var active) && !active.GetBoolean())
            {
                _logger.LogInformation("仓库命中 registry 但 active=false，不导出。Repo: {Repo}", repoName);
                return null;
            }

            var titles = r.TryGetProperty("titles", out var t) ? ParseTitles(t) : null;
            var upstream = r.TryGetProperty("upstream", out var up) ? up.GetString() : null;
            return new RepoRegistryMatch(
                r.GetProperty("docPath").GetString()!.Trim('/'),
                titles,
                upstream);
        }

        return null;
    }

    /// <summary>单条标题定义 → 语言字典：object 按语言键（大小写不敏感），string 视为全语言通用（"*"）。</summary>
    private static IReadOnlyDictionary<string, string>? ParseTitles(JsonElement entry)
    {
        return entry.ValueKind switch
        {
            JsonValueKind.String => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["*"] = entry.GetString()!
            },
            JsonValueKind.Object when entry.EnumerateObject().Any() => entry.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase),
            _ => null
        };
    }

    private static string? RepoNameFromGitUrl(string? gitUrl)
    {
        if (string.IsNullOrWhiteSpace(gitUrl))
        {
            return null;
        }

        var lastSegment = gitUrl.TrimEnd('/').Split('/').Last();
        return lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? lastSegment[..^4]
            : lastSegment;
    }
}
