using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 根据工作区组配置（BasePath / DomainPromptsPath）解析对应领域的 prompt 文本。
/// 领域标识固定集合：<c>unity | server | foundation | admin | godot | protobuf | config | tools | specification</c>。
/// </summary>
public sealed class DomainPromptRegistry
{
    /// <summary>
    /// 支持的领域标识（小写）
    /// </summary>
    public static readonly HashSet<string> SupportedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "unity", "server", "foundation", "admin", "godot",
        "protobuf", "config", "tools", "specification"
    };

    private readonly string? _domainPromptsPath;

    public DomainPromptRegistry(string? domainPromptsPath)
    {
        _domainPromptsPath = string.IsNullOrWhiteSpace(domainPromptsPath)
            ? null
            : Path.GetFullPath(domainPromptsPath);
    }

    /// <summary>
    /// 加载指定领域的 prompt。如 domainPromptsPath = "/gfx-doc/dev/domain-prompts"，domain = "unity"，
    /// 则读取 "/gfx-doc/dev/domain-prompts/unity.md"。
    /// </summary>
    /// <returns>prompt 全文；若文件不存在或未配置 DomainPromptsPath，返回 null。</returns>
    public string? GetPrompt(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("domain 不能为空", nameof(domain));

        if (!SupportedDomains.Contains(domain))
            throw new ArgumentOutOfRangeException(nameof(domain),
                $"不支持的领域标识：{domain}。支持：{string.Join("|", SupportedDomains)}");

        if (_domainPromptsPath is null || !Directory.Exists(_domainPromptsPath))
            return null;

        var file = Path.Combine(_domainPromptsPath, $"{domain.ToLowerInvariant()}.md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>
    /// 列出 DomainPromptsPath 下已存在的领域 prompt 文件（不含扩展名）
    /// </summary>
    public IReadOnlyList<string> ListAvailableDomains()
    {
        if (_domainPromptsPath is null || !Directory.Exists(_domainPromptsPath))
            return Array.Empty<string>();

        return Directory.GetFiles(_domainPromptsPath, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => SupportedDomains.Contains(name!))
            .OrderBy(name => name)
            .ToList()!;
    }
}

/// <summary>
/// repo-registry.json 中的根对象结构。groups 是字典（key = groupId）。
/// 与 GameFrameX/Docs/gfx-config/repo-registry.json 的实际 schema 对齐。
/// </summary>
public sealed class RepoRegistryDocument
{
    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// groups 是 "groupId -> 组配置" 的字典
    /// </summary>
    [JsonPropertyName("groups")]
    public Dictionary<string, WorkspaceGroupConfig> Groups { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 单个仓组配置（对应 repo-registry.json groups 下的一项）
/// </summary>
public sealed class WorkspaceGroupConfig
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("ingestMode")]
    public string? IngestMode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 工作区根路径（用于解析 repositories[].localPath 的相对根；可选）
    /// </summary>
    [JsonPropertyName("basePath")]
    public string? BasePath { get; set; }

    [JsonPropertyName("catalogTemplatePath")]
    public string? CatalogTemplatePath { get; set; }

    [JsonPropertyName("domainPromptsPath")]
    public string? DomainPromptsPath { get; set; }

    [JsonPropertyName("outputRoot")]
    public string? OutputRoot { get; set; }

    [JsonPropertyName("languagesCsv")]
    public string? LanguagesCsv { get; set; }

    [JsonPropertyName("repositories")]
    public List<RepoEntry> Repos { get; set; } = new();
}

/// <summary>
/// 单个仓条目
/// </summary>
public sealed class RepoEntry
{
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("repoKey")]
    public string? RepoKey { get; set; }

    [JsonPropertyName("gitUrl")]
    public string? GitUrl { get; set; }

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// 规范化后的 RepoKey：优先 repoKey 字段，否则 alias。
    /// </summary>
    [JsonIgnore]
    public string? NormalizedRepoKey => string.IsNullOrWhiteSpace(RepoKey) ? Alias : RepoKey;
}

/// <summary>
/// 从 repo-registry.json 加载配置的入口
/// </summary>
public static class DomainPromptRegistryLoader
{
    /// <summary>
    /// 从 repo-registry.json 解析全部仓组配置
    /// </summary>
    public static RepoRegistryDocument LoadFromConfig(string registryPath)
    {
        if (!File.Exists(registryPath))
            throw new FileNotFoundException($"repo-registry.json 不存在：{registryPath}", registryPath);

        var json = File.ReadAllText(registryPath);
        var doc = JsonSerializer.Deserialize<RepoRegistryDocument>(json)
                  ?? throw new InvalidOperationException($"repo-registry.json 解析失败：{registryPath}");
        return doc;
    }
}
