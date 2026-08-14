using OpenDeepWiki.Entities;


/// <summary>
/// S3 写入器占位实现（Phase 5 / C6 启用）。
/// 当前不真正落盘，所有写入丢弃；CommitAsync 抛 <see cref="NotImplementedException"/>。
/// 留接口给后续 Phase 接入 <c>AWSSDK.S3</c> 后填充实现。
/// </summary>
public class S3Writer : IDocsWriter
{
    public DocsWriterType WriterType => DocsWriterType.S3;

    public Task WritePageAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        // Phase 5 启用前：no-op
        return Task.CompletedTask;
    }

    public Task WriteCatalogAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        // Phase 5 启用前：no-op
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("S3Writer 将在 Phase 5 / C6 接入 AWSSDK.S3 后实现。");
}
