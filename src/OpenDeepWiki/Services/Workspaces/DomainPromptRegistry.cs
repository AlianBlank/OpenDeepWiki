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
/// repo-registry.json 中的根对象结构。groups 为节点数组（v4 目录树）。
/// 与 GameFrameX/Docs/gfx-config/repo-registry.json 的 v4 schema 对齐。
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
    /// 顶层组节点数组
    /// </summary>
    [JsonPropertyName("groups")]
    public List<RegistryNode> Groups { get; set; } = new();
}

/// <summary>
/// v4 目录树节点（组 / 子组同构递归，约定最多三层：组 → 子组 → 仓库）
/// </summary>
public sealed class RegistryNode
{
    /// <summary>
    /// 节点标识（kebab-case，同级唯一）；groupId 按 '/' 分隔的节点路径寻址（如 "gfx-core/client"）
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// 摄取方式；子节点可省略，省略时继承组级声明
    /// </summary>
    [JsonPropertyName("ingestMode")]
    public string? IngestMode { get; set; }

    /// <summary>
    /// 节点开关；false 时停用该节点整棵子树（缺省启用）
    /// </summary>
    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("children")]
    public List<RegistryNode> Children { get; set; } = new();

    [JsonPropertyName("repositories")]
    public List<RepoEntry> Repositories { get; set; } = new();

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// v4 registry 单仓条目（全部仓显式登记，含包仓与第三方重打包）。
/// 不映射 titles：真实 registry 存在 string 简写形式（64 条），映射为字典会解析失败，且 workspace 管道不消费标题（标题由导出链路 RepoRegistryProvider 实时解析）。
/// </summary>
public sealed class RepoEntry
{
    /// <summary>
    /// 仓标识；包仓直接用包名（含点），普通仓用 kebab-case 短名
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("gitUrl")]
    public string? GitUrl { get; set; }

    /// <summary>
    /// 自动生成文档落盘目录（相对 docs/&lt;lang&gt;/），components 域下可含 packageTreeRule 展开的子目录
    /// </summary>
    [JsonPropertyName("docPath")]
    public string? DocPath { get; set; }

    /// <summary>
    /// 第三方重打包的上游仓溯源；仅重打包仓使用
    /// </summary>
    [JsonPropertyName("upstream")]
    public string? Upstream { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// 规范化后的 RepoKey：v4 无独立 repoKey 字段，直接用 alias。
    /// </summary>
    [JsonIgnore]
    public string? NormalizedRepoKey => Alias;
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
