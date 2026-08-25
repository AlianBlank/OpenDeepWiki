using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>repo-registry.json 匹配结果：Docs 落位路径 + 语言级展示标题 + 上游溯源。</summary>
public record RepoRegistryMatch(
    string DocPath,
    IReadOnlyDictionary<string, string>? Titles,
    string? Upstream);

/// <summary>
/// 读取 Docs 仓 <c>gfx-config/repo-registry.json</c>，把 OpenDeepWiki 仓库名解析为
/// Docs 落位路径（docPath）与多语言展示标题（packageTitles）。
/// <para>每次匹配都重读文件（registry 小、导出低频）：B佬改 registry 即时生效，无需重启。</para>
/// <para>匹配优先级：thirdPartyPackages（active 才导出，带 upstream 溯源）→
/// 各组 repositories 显式条目（gitUrl 尾段 == 仓库名）→ discovery.repoPatterns
/// （packageTree 规则展开 docPathTemplate）。未命中返回 null（不导出）。</para>
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

    /// <summary>packageTree 规则的前缀（catalog-template.json childrenTemplate 唯一事实源的代码实现）。</summary>
    private const string UnityPackagePrefix = "com.gameframex.unity";

    /// <summary>按仓库名匹配 registry；未命中或 registry 不可读返回 null。</summary>
    public RepoRegistryMatch? Match(string repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName))
        {
            return null;
        }

        var registryPath = _configuration["RspressExport:RegistryPath"]
                           ?? "/host-gfx-docs/gfx-config/repo-registry.json";
        if (!File.Exists(registryPath))
        {
            _logger.LogWarning("repo-registry.json 不存在，跳过 registry 映射。Path: {Path}", registryPath);
            return null;
        }

        JsonDocument registry;
        try
        {
            registry = JsonDocument.Parse(File.ReadAllText(registryPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "repo-registry.json 解析失败。Path: {Path}", registryPath);
            return null;
        }

        using (registry)
        {
            return MatchCore(registry.RootElement, repoName);
        }
    }

    private RepoRegistryMatch? MatchCore(JsonElement root, string repoName)
    {
        var titles = LoadTitles(root);

        if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in groups.EnumerateObject())
            {
                // 1. thirdPartyPackages：第三方重打包，默认停用；active 才按 packageTree 规则导出
                if (group.Value.TryGetProperty("thirdPartyPackages", out var thirdParty) &&
                    thirdParty.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tp in thirdParty.EnumerateArray())
                    {
                        if (tp.GetProperty("repo").GetString() != repoName)
                        {
                            continue;
                        }

                        if (tp.TryGetProperty("active", out var active) && active.GetBoolean())
                        {
                            var upstream = tp.TryGetProperty("upstream", out var up) ? up.GetString() : null;
                            return new RepoRegistryMatch(
                                ComputeDiscoveryDocPath(group.Value, repoName),
                                titles(repoName),
                                upstream);
                        }

                        _logger.LogInformation(
                            "仓库命中 thirdPartyPackages 但未激活，不导出。Repo: {Repo}", repoName);
                        return null;
                    }
                }

                // 2. 显式 repositories 条目：gitUrl 尾段（去 .git）== 仓库名
                if (group.Value.TryGetProperty("repositories", out var repos) &&
                    repos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in repos.EnumerateArray())
                    {
                        if (RepoNameFromGitUrl(r.GetProperty("gitUrl").GetString()) == repoName)
                        {
                            var active = !r.TryGetProperty("active", out var a) || a.GetBoolean();
                            if (!active)
                            {
                                _logger.LogInformation(
                                    "仓库命中 registry 但 active=false，不导出。Repo: {Repo}", repoName);
                                return null;
                            }

                            return new RepoRegistryMatch(r.GetProperty("docPath").GetString()!.Trim('/'), titles(repoName), null);
                        }
                    }
                }

                // 3. discovery.repoPatterns：com.gameframex.unity* 按 packageTree 展开模板
                if (group.Value.TryGetProperty("discovery", out var discovery) &&
                    discovery.ValueKind == JsonValueKind.Object &&
                    MatchesPatterns(discovery, repoName))
                {
                    return new RepoRegistryMatch(
                        ComputeDiscoveryDocPath(group.Value, repoName),
                        titles(repoName),
                        null);
                }
            }
        }

        return null;
    }

    /// <summary>packageTitles 取值器：object 形式按语言键（大小写不敏感），string 形式全语言通用。</summary>
    private static Func<string, IReadOnlyDictionary<string, string>?> LoadTitles(JsonElement root)
    {
        if (!root.TryGetProperty("packageTitles", out var packageTitles) ||
            packageTitles.ValueKind != JsonValueKind.Object)
        {
            return _ => null;
        }

        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var entry in packageTitles.EnumerateObject())
        {
            IReadOnlyDictionary<string, string>? langs = entry.Value.ValueKind switch
            {
                JsonValueKind.String => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["*"] = entry.Value.GetString()!
                },
                JsonValueKind.Object => entry.Value.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase),
                _ => null
            };
            if (langs != null)
            {
                map[entry.Name] = langs;
            }
        }

        return name => map.TryGetValue(name ?? string.Empty, out var t) ? t : null;
    }

    private static bool MatchesPatterns(JsonElement discovery, string repoName)
    {
        if (!discovery.TryGetProperty("repoPatterns", out var patterns) ||
            patterns.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pattern in patterns.EnumerateArray())
        {
            var p = pattern.GetString();
            if (p == repoName)
            {
                return true;
            }

            if (p != null && p.EndsWith("*") &&
                repoName.StartsWith(p[..^1], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>packageTree：剥 com.gameframex.unity 前缀按 '.' 切分（主包落域根，.config → config，.ui.fairygui → ui/fairygui）。</summary>
    private static string ComputePackageTree(string repoName)
    {
        if (!repoName.StartsWith(UnityPackagePrefix, StringComparison.Ordinal))
        {
            return repoName;
        }

        var rest = repoName[UnityPackagePrefix.Length..];
        return rest.StartsWith('.') ? rest[1..].Replace('.', '/') : string.Empty;
    }

    private static string ComputeDiscoveryDocPath(JsonElement group, string repoName)
    {
        var template = ".auto/components/unity/{packageTree}";
        if (group.TryGetProperty("discovery", out var discovery) &&
            discovery.TryGetProperty("docPathTemplate", out var tpl) &&
            tpl.ValueKind == JsonValueKind.String)
        {
            template = tpl.GetString()!;
        }

        return template.Replace("{packageTree}", ComputePackageTree(repoName)).Trim('/');
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
