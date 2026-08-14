using OpenDeepWiki.Entities;


/// <summary>
/// 文档输出端写入器抽象。OpenDeepWiki 生成的 Markdown（目录、页面、翻译、快照）经此接口落盘到
/// 不同后端：本地文件系统、远端 Git 仓（PR）或 S3。
/// 实现需保证：
/// <list type="bullet">
/// <item>幂等：同一相对路径多次写入最终内容一致。</item>
/// <item>目录自动创建。</item>
/// <item>异常向上抛，由调用方决定事务边界。</item>
/// </list>
/// </summary>
public interface IDocsWriter
{
    /// <summary>
    /// 写入器类型
    /// </summary>
    DocsWriterType WriterType { get; }

    /// <summary>
    /// 写入单页文档。<paramref name="relativePath"/> 相对 <see cref="WorkspaceRepoGroup.OutputRoot"/>，
    /// 如 "en/.auto/unity/config/configmanager.md"。
    /// </summary>
    Task WritePageAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入目录文件（通常是 _meta.json 或 index.md）。
    /// </summary>
    Task WriteCatalogAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交本次写入（本地=不操作；Git=push；S3=flush）。
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 解析 <see cref="DocsWriterType"/> + WriterOptionsJson 得到具体 <see cref="IDocsWriter"/> 实例的工厂。
/// 实现通过 DI 注入，根据 WorkspaceRepoGroup.WriterType 返回对应实现。
/// </summary>
public interface IDocsWriterFactory
{
    /// <summary>
    /// 根据组配置创建写入器
    /// </summary>
    IDocsWriter Create(WorkspaceRepoGroup group);
}
