using OpenDeepWiki.Entities;


/// <summary>
/// 写到本地文件系统的 <see cref="IDocsWriter"/> 实现。
/// 产物落盘到 <see cref="WorkspaceRepoGroup.OutputRoot"/>（绝对路径）下的相对子路径。
/// 用例：开发期把生成的 Markdown 直接写到 GameFrameX.Docs 仓的 docs/ 目录，本地 diff 复核后人工 commit。
/// </summary>
public class LocalDocsWriter : IDocsWriter
{
    private readonly string _root;

    public DocsWriterType WriterType => DocsWriterType.Local;

    /// <summary>
    /// 构造本地写入器。
    /// </summary>
    /// <param name="outputRoot">输出根目录（绝对路径）。如不存在会在首次写入时创建。</param>
    public LocalDocsWriter(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("outputRoot 不能为空", nameof(outputRoot));

        _root = Path.GetFullPath(outputRoot);
    }

    public Task WritePageAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        => WriteAsync(relativePath, content, cancellationToken);

    public Task WriteCatalogAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        => WriteAsync(relativePath, content, cancellationToken);

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        // 本地写入器：所有写入即最终态，无需提交
        return Task.CompletedTask;
    }

    private async Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(relativePath);
        var dir = Path.GetDirectoryName(fullPath)
                  ?? throw new InvalidOperationException($"无法解析目录：{fullPath}");

        Directory.CreateDirectory(dir);

        await using var stream = File.Create(fullPath);
        await using var writer = new StreamWriter(stream);
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 解析相对路径到绝对路径，禁止越界（防止 ../ 跳根）。
    /// </summary>
    internal string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("relativePath 不能为空", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        var normalizedRoot = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;

        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"相对路径越界：{relativePath}（解析后 {full} 不在根 {_root} 内）");

        return full;
    }
}
