using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 根据组的 <see cref="WorkspaceRepoGroup.WriterType"/> 创建对应的 <see cref="IDocsWriter"/>。
/// </summary>
public sealed class DocsWriterFactory : IDocsWriterFactory
{
    public IDocsWriter Create(WorkspaceRepoGroup group)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));

        return group.WriterType switch
        {
            DocsWriterType.Local => new LocalDocsWriter(group.OutputRoot),
            DocsWriterType.RemoteGit => new RemoteGitWriter(group.WriterOptionsJson
                ?? throw new InvalidOperationException(
                    $"WorkspaceRepoGroup '{group.Id}' WriterType=RemoteGit 但 WriterOptionsJson 为空")),
            DocsWriterType.S3 => new S3Writer(),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group.WriterType, "未知的 DocsWriterType")
        };
    }
}
