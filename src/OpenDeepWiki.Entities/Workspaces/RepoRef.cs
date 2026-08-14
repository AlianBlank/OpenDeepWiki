using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 工作区仓库组内的单个仓库引用。一个 RepoRef 描述如何摄取某个仓库（Git URL 或本地路径）
/// 以及它的领域归属（unity / server / foundation / admin / godot / protobuf / config / tools / specification）。
/// </summary>
public class RepoRef : AggregateRoot<string>
{

    /// <summary>
    /// 所属工作区组的 Id
    /// </summary>
    [Required]
    [StringLength(64)]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// 组内唯一标识（如 "unity"、"server"）
    /// </summary>
    [Required]
    [StringLength(64)]
    public string RepoKey { get; set; } = string.Empty;

    /// <summary>
    /// Git URL（可选，ingestMode=GitUrl 时使用）
    /// </summary>
    [StringLength(500)]
    public string? GitUrl { get; set; }

    /// <summary>
    /// 本地路径（相对 WorkspaceRepoGroup.BasePath；ingestMode=LocalPath 时使用）
    /// </summary>
    [StringLength(1000)]
    public string? LocalPath { get; set; }

    /// <summary>
    /// 领域归属（unity / server / foundation / admin / godot / protobuf / config / tools / specification）
    /// </summary>
    [Required]
    [StringLength(32)]
    public string Domain { get; set; } = "tools";

    /// <summary>
    /// 默认分支（默认 main）
    /// </summary>
    [StringLength(100)]
    public string? Branch { get; set; } = "main";

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// 在组内的展示顺序（小的在前）
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// 所属工作区组导航属性
    /// </summary>
    [ForeignKey(nameof(GroupId))]
    public virtual WorkspaceRepoGroup? Group { get; set; }
}
