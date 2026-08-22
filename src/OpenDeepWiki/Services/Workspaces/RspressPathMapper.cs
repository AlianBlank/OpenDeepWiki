using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 单个待写入的 Rspress 文件（相对输出根的路径 + 内容）。
/// </summary>
public record RspressPage(string RelativePath, string Content);

/// <summary>
/// 一次映射产生的全部文件集合。
/// </summary>
public sealed class RspressSite
{
    public List<RspressPage> Pages { get; } = new();
    public List<string> Warnings { get; } = new();

    public void Add(RspressPage page) => Pages.Add(page);
    public void Warn(string message) => Warnings.Add(message);
}

/// <summary>
/// 把 OpenDeepWiki 的 <see cref="DocCatalog"/> 树映射为 Rspress 文档站文件结构。
/// <para>输出全部落在 <c>{lang}/{repoSlug}/</c> 子目录下，多仓库共存互不覆盖；
/// 顶层 <c>_nav.json</c>/<c>index.md</c> 由调用方按需另行维护。</para>
/// <para>纯函数，不接触文件系统与数据库，可单测。</para>
/// </summary>
public sealed class RspressPathMapper
{
    /// <summary>OpenDeepWiki LanguageCode → Rspress lang 目录。</summary>
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh-CN",
        ["zh-CN"] = "zh-CN",
        ["zh-TW"] = "zh-TW",
        ["en"] = "en",
        ["ja"] = "ja",
        ["ko"] = "ko",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 剥离开头的数字前缀：1- / 1. / 1.1- / 1.2.3_ / 1_ 等
    private static readonly Regex NumberPrefixRegex =
        new(@"^\d+(\.\d+)*[-_.]", RegexOptions.Compiled);

    // 残留的纯前导数字（如 "1overview" 极少见，兜底）
    private static readonly Regex LeadingDigitsRegex =
        new(@"^\d+", RegexOptions.Compiled);

    /// <summary>将 OpenDeepWiki 语言代码映射为 Rspress 目录名（未匹配原样返回）。</summary>
    public string MapLanguage(string languageCode)
        => LangMap.TryGetValue(languageCode ?? string.Empty, out var mapped) ? mapped : (languageCode ?? "en");

    /// <summary>
    /// 把仓库名规范化为 URL 友好的目录 slug（多仓库共存前缀）。
    /// 规则：去 <c>com.gameframex.</c> 公共前缀，点/空格/下划线转连字符，小写。
    /// </summary>
    public string NormalizeRepoSlug(string repoName)
    {
        var s = (repoName ?? string.Empty).Trim();
        // 去常见 GameFrameX 包名前缀
        const string prefix = "com.gameframex.";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..];
        return NormalizeSlugCore(s);
    }

    /// <summary>
    /// 映射一棵 DocCatalog 树为 Rspress 文件集合。
    /// </summary>
    /// <param name="rootCatalogs">根级 DocCatalog（ParentId=null），需已 Include Children 与 DocFile。</param>
    /// <param name="repoSlug">仓库目录 slug（见 <see cref="NormalizeRepoSlug"/>）。</param>
    /// <param name="languageCode">OpenDeepWiki 语言代码（会被 <see cref="MapLanguage"/> 转换）。</param>
    /// <param name="repoTitle">仓库展示名，用于 repo 目录 index.md 的标题。</param>
    public RspressSite Map(
        IEnumerable<DocCatalog> rootCatalogs,
        string repoSlug,
        string languageCode,
        string repoTitle)
    {
        var site = new RspressSite();
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            repoSlug = "untitled";
            site.Warn("repoSlug 为空，回退为 'untitled'。");
        }

        var langDir = MapLanguage(languageCode);
        var repoDir = $"{langDir}/{repoSlug}";

        var roots = (rootCatalogs ?? Enumerable.Empty<DocCatalog>())
            .Where(c => c != null)
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .ToList();

        // 递归处理每个根节点
        var rootEntries = new List<(string Slug, bool IsDir, string Label)>();
        var usedRootSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var catalog in roots)
        {
            var (slug, isDir) = ProcessCatalog(catalog, repoDir, usedRootSlugs, site);
            rootEntries.Add((slug, isDir, catalog.Title));
        }

        // repo 目录 index.md：若有内容用内容，否则生成子链接列表
        WriteRepoIndex(repoDir, repoTitle, rootEntries, roots, site);
        // repo 目录 _meta.json
        WriteMetaJson(repoDir, rootEntries, site);

        return site;
    }

    /// <summary>递归处理单个 catalog 节点，返回 (slug, isDir)。</summary>
    private (string Slug, bool IsDir) ProcessCatalog(
        DocCatalog catalog,
        string parentDir,
        HashSet<string> usedSlugs,
        RspressSite site)
    {
        var hasChildren = catalog.Children != null && catalog.Children.Count != 0;
        var slug = ResolveUniqueSlug(catalog.Path, usedSlugs, site, catalog.Title);

        if (hasChildren)
        {
            var dir = $"{parentDir}/{slug}";
            var childUsed = new HashSet<string>(StringComparer.Ordinal);
            var childEntries = new List<(string Slug, bool IsDir, string Label)>();

            foreach (var child in catalog.Children.OrderBy(c => c.Order)
                                                .ThenBy(c => c.Title, StringComparer.Ordinal))
            {
                var (childSlug, childIsDir) = ProcessCatalog(child, dir, childUsed, site);
                childEntries.Add((childSlug, childIsDir, child.Title));
            }

            // 目录 index.md
            WriteDirIndex(dir, catalog, childEntries, site);
            // 目录 _meta.json
            WriteMetaJson(dir, childEntries, site);

            return (slug, IsDir: true);
        }
        else
        {
            // 叶子页：{parentDir}/{slug}.md
            var relPath = $"{parentDir}/{slug}.md";
            var content = catalog.DocFile?.Content;
            if (string.IsNullOrEmpty(content))
            {
                site.Warn($"叶子节点缺内容，跳过文件写入：{catalog.Path}（{catalog.Title}）。_meta.json 仍会列出。");
                // 仍写入一个占位页，保证 _meta.json 引用的文件存在，避免 Rspress 死链
                content = $"# {catalog.Title}\n\n> 本页内容尚未生成。\n";
            }
            site.Add(new RspressPage(relPath, content!));
            return (slug, IsDir: false);
        }
    }

    /// <summary>剥数字前缀 + 规范化的核心 slug 转换。</summary>
    private static string NormalizeSlugCore(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        // 1. 剥数字前缀（1- / 1.1. / 1_ 等，含分隔符）
        s = NumberPrefixRegex.Replace(s, string.Empty);
        // 2. 若 path 用点分层级（如 ai.svc），取最后一段作本节点 slug（树形已表达层级）
        var lastSep = s.LastIndexOfAny(new[] { '.', '/' });
        if (lastSep >= 0 && lastSep < s.Length - 1)
            s = s[(lastSep + 1)..];
        // 3. 再次清掉残留前导数字（兜底）
        s = LeadingDigitsRegex.Replace(s, string.Empty);
        // 4. 规范化：空格/下划线/点 → 连字符，小写，非 [a-z0-9-] → 连字符
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            if (ch is ' ' or '_' or '.') sb.Append('-');
            else if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9') sb.Append(ch);
            else if (ch == '-') sb.Append('-');
            else sb.Append('-'); // 非 ASCII/特殊字符 → 连字符
        }
        s = sb.ToString();
        // 5. 合并连续连字符，去首尾
        s = Regex.Replace(s, "-{2,}", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>同级 slug 去重：冲突时追加 -2、-3…</summary>
    private static string ResolveUniqueSlug(string path, HashSet<string> used, RspressSite site, string title)
    {
        var baseSlug = NormalizeSlugCore(path);
        var slug = baseSlug;
        var n = 2;
        while (used.Contains(slug))
        {
            slug = $"{baseSlug}-{n}";
            n++;
        }
        if (slug != baseSlug)
            site.Warn($"slug 冲突：'{path}'（{title}）已存在，改用 '{slug}'。");
        used.Add(slug);
        return slug;
    }

    /// <summary>repo 目录 index.md：有内容用内容，否则生成标题 + 子项链接。</summary>
    private static void WriteRepoIndex(
        string repoDir,
        string repoTitle,
        List<(string Slug, bool IsDir, string Label)> entries,
        List<DocCatalog> roots,
        RspressSite site)
    {
        // 若根级有内容（极少见），优先用其内容
        var rootWithContent = roots.FirstOrDefault(r => r.DocFile != null && !string.IsNullOrEmpty(r.DocFile.Content));
        string content;
        if (rootWithContent != null)
        {
            content = rootWithContent.DocFile!.Content!;
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# {repoTitle}");
            sb.AppendLine();
            foreach (var (slug, isDir, label) in entries)
            {
                var link = isDir ? $"./{slug}/" : $"./{slug}.md";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [{label}]({link})");
            }
            content = sb.ToString();
        }
        site.Add(new RspressPage($"{repoDir}/index.md", content));
    }

    /// <summary>目录 index.md：有 DocFile 内容用内容，否则标题 + 子项链接列表。</summary>
    private static void WriteDirIndex(
        string dir,
        DocCatalog catalog,
        List<(string Slug, bool IsDir, string Label)> entries,
        RspressSite site)
    {
        string content;
        if (catalog.DocFile != null && !string.IsNullOrEmpty(catalog.DocFile.Content))
        {
            content = catalog.DocFile.Content!;
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# {catalog.Title}");
            sb.AppendLine();
            foreach (var (slug, isDir, label) in entries)
            {
                var link = isDir ? $"./{slug}/" : $"./{slug}.md";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [{label}]({link})");
            }
            content = sb.ToString();
        }
        site.Add(new RspressPage($"{dir}/index.md", content));
    }

    /// <summary>生成 _meta.json：["index", "leaf", {"type":"dir","name":"x","label":"Y"}]。</summary>
    private static void WriteMetaJson(
        string dir,
        List<(string Slug, bool IsDir, string Label)> entries,
        RspressSite site)
    {
        var items = new List<object> { "index" };
        foreach (var (slug, isDir, label) in entries)
        {
            if (isDir)
                items.Add(new { type = "dir", name = slug, label });
            else
                items.Add(slug);
        }
        var json = JsonSerializer.Serialize(items, JsonOpts);
        site.Add(new RspressPage($"{dir}/_meta.json", json));
    }
}
