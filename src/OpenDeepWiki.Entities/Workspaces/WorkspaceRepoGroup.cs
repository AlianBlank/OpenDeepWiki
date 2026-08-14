using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 工作区仓库组的运行状态
/// </summary>
public enum WorkspaceGroupStatus
{
    Idle = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>
/// 文档输出端写入器类型
/// </summary>
public enum DocsWriterType
{
    Local = 0,
    RemoteGit = 1,
    S3 = 2
}

/// <summary>
/// 工作区仓库组。一个 GameFrameX 工作区由多个仓库组成（如 unity/server/foundation/admin 等），
/// 此实体是组的聚合根，组内的具体仓库引用通过 <see cref="RepoRef"/> 关联。
/// </summary>
public class WorkspaceRepoGroup : AggregateRoot<string>
{
    /// <summary>
    /// 显示名（如 "GameFrameX 主仓组"）
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 描述
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// 本地工作区根路径（ingestMode=LocalPath 时，组内各 RepoRef 的本地路径相对此根）
    /// </summary>
    [StringLength(1000)]
    public string? BasePath { get; set; }

    /// <summary>
    /// 多语言代码（CSV，如 "en,zh,zh-CN,zh-TW,ja,ko"）
    /// </summary>
    [Required]
    [StringLength(200)]
    public string LanguagesCsv { get; set; } = "en";

    /// <summary>
    /// Catalog 模板路径（相对 BasePath 或绝对路径）
    /// </summary>
    [StringLength(500)]
    public string? CatalogTemplatePath { get; set; }

    /// <summary>
    /// 领域 prompt 目录（相对 BasePath 或绝对路径，内含 unity.md / server.md 等）
    /// </summary>
    [StringLength(500)]
    public string? DomainPromptsPath { get; set; }

    /// <summary>
    /// 文档输出端的写入器类型
    /// </summary>
    public DocsWriterType WriterType { get; set; } = DocsWriterType.Local;

    /// <summary>
    /// 写入器特定配置（JSON，如 LocalDocsWriter 的根路径、RemoteGitWriter 的仓地址与 token 等）
    /// </summary>
    [StringLength(4000)]
    public string? WriterOptionsJson { get; set; }

    /// <summary>
    /// 文档输出根路径（writer 写入的位置，如 GameFrameX.Docs 仓的 docs/ 目录）
    /// </summary>
    [Required]
    [StringLength(1000)]
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// 上次运行时间（UTC）
    /// </summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>
    /// 上次运行状态
    /// </summary>
    public WorkspaceGroupStatus LastRunStatus { get; set; } = WorkspaceGroupStatus.Idle;

    /// <summary>
    /// 上次运行的错误信息（运行失败时填写）
    /// </summary>
    [StringLength(2000)]
    public string? LastRunError { get; set; }

    /// <summary>
    /// 组内的仓库引用
    /// </summary>
    public virtual ICollection<RepoRef> Repos { get; set; } = new List<RepoRef>();
}
