using System.Text.Json;
using Octokit;
using OpenDeepWiki.Entities;


namespace OpenDeepWiki.Services.Workspaces;

/// <summary>
/// 通过 GitHub API 提 PR 的 <see cref="IDocsWriter"/> 实现。
/// 用例：CI / 定时任务中，生成的 Markdown 自动开 PR 到 GameFrameX.Docs 仓，由人工 review 合并。
///
/// WriterOptionsJson 约定：
/// <code>
/// {
///   "owner": "GameFrameX",
///   "repo": "Docs",
///   "branch": "main",                  // 目标分支
///   "headBranch": "docs/auto-xxx",     // PR 源分支（会自动创建/重置）
///   "token": "ghp_xxx",                // PAT，必须有 repo 权限
///   "commitMessage": "docs: auto update",
///   "prTitle": "Auto-generated docs",
///   "prBody": "by OpenDeepWiki"
/// }
/// </code>
/// </summary>
public class RemoteGitWriter : IDocsWriter
{
    private readonly RemoteGitWriterOptions _opts;
    private readonly Dictionary<string, string> _buffer = new(StringComparer.OrdinalIgnoreCase);

    public DocsWriterType WriterType => DocsWriterType.RemoteGit;

    public RemoteGitWriter(string writerOptionsJson)
    {
        _opts = string.IsNullOrWhiteSpace(writerOptionsJson)
            ? throw new ArgumentException("RemoteGitWriter 需要 WriterOptionsJson 配置", nameof(writerOptionsJson))
            : JsonSerializer.Deserialize<RemoteGitWriterOptions>(writerOptionsJson)!
              ?? throw new ArgumentException("WriterOptionsJson 解析失败", nameof(writerOptionsJson));

        _opts.Validate();
    }

    public Task WritePageAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        _buffer[Normalize(relativePath)] = content;
        return Task.CompletedTask;
    }

    public Task WriteCatalogAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        _buffer[Normalize(relativePath)] = content;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把内存中缓写的文件批量 push 到远端 head 分支，并（若 head != base）开 PR。
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var github = new GitHubClient(new ProductHeaderValue("OpenDeepWiki-RemoteGitWriter"))
        {
            Credentials = new Credentials(_opts.Token!)
        };

        // 1. 取 base 分支的最新 commit SHA + tree SHA 作为基准
        var baseRef = await github.Git.Reference.Get(_opts.Owner!, _opts.Repo!, $"heads/{_opts.Branch}");
        var baseCommitSha = baseRef.Object.Sha;
        var baseTreeSha = (await github.Git.Commit.Get(_opts.Owner!, _opts.Repo!, baseCommitSha)).Tree.Sha;

        // 2. 为每个文件创建 blob，组装 new tree（base = baseTreeSha）
        var treeItems = new List<NewTreeItem>();
        foreach (var (path, content) in _buffer)
        {
            var blob = new NewBlob { Content = content, Encoding = EncodingType.Utf8 };
            var blobSha = await github.Git.Blob.Create(_opts.Owner!, _opts.Repo!, blob);
            treeItems.Add(new NewTreeItem
            {
                Path = path,
                Mode = "100644",
                Type = TreeType.Blob,
                Sha = blobSha.Sha
            });
        }

        var newTree = new NewTree { BaseTree = baseTreeSha };
        foreach (var item in treeItems) newTree.Tree.Add(item);
        var treeResp = await github.Git.Tree.Create(_opts.Owner!, _opts.Repo!, newTree);

        // 3. 创建 commit（parent = baseCommitSha）
        var newCommit = new NewCommit(_opts.CommitMessage!, treeResp.Sha, baseCommitSha);
        var commitResp = await github.Git.Commit.Create(_opts.Owner!, _opts.Repo!, newCommit);

        // 4. 创建/更新 head 分支引用
        var headRefName = $"heads/{_opts.HeadBranch}";
        try
        {
            var existing = await github.Git.Reference.Get(_opts.Owner!, _opts.Repo!, headRefName);
            await github.Git.Reference.Update(_opts.Owner!, _opts.Repo!, headRefName,
                new ReferenceUpdate(commitResp.Sha));
        }
        catch (NotFoundException)
        {
            await github.Git.Reference.Create(_opts.Owner!, _opts.Repo!,
                new NewReference(headRefName, commitResp.Sha));
        }

        // 5. head != base 时开 PR（head 已存在则跳过）
        if (!string.Equals(_opts.HeadBranch, _opts.Branch, StringComparison.OrdinalIgnoreCase))
        {
            var headRefForPr = $"{_opts.Owner}:{_opts.HeadBranch}";
            try
            {
                await github.PullRequest.Create(_opts.Owner!, _opts.Repo!,
                    new NewPullRequest(_opts.PrTitle!, headRefForPr, _opts.Branch!) { Body = _opts.PrBody });
            }
            catch (ApiException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // PR 已开，本轮更新 head 分支即可
            }
        }

        _buffer.Clear();
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/').TrimStart('/');
}

/// <summary>
/// RemoteGitWriter 的配置（从 WorkspaceRepoGroup.WriterOptionsJson 反序列化）
/// </summary>
public sealed class RemoteGitWriterOptions
{
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; } = "main";
    public string? HeadBranch { get; set; } = "docs/auto-update";
    public string? Token { get; set; }
    public string? CommitMessage { get; set; } = "docs: auto update by OpenDeepWiki";
    public string? PrTitle { get; set; } = "Auto-generated docs";
    public string? PrBody { get; set; } = "Generated by OpenDeepWiki RemoteGitWriter.";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Owner)) throw new InvalidOperationException("RemoteGitWriter 缺 Owner");
        if (string.IsNullOrWhiteSpace(Repo)) throw new InvalidOperationException("RemoteGitWriter 缺 Repo");
        if (string.IsNullOrWhiteSpace(Branch)) throw new InvalidOperationException("RemoteGitWriter 缺 Branch");
        if (string.IsNullOrWhiteSpace(HeadBranch)) throw new InvalidOperationException("RemoteGitWriter 缺 HeadBranch");
        if (string.IsNullOrWhiteSpace(Token)) throw new InvalidOperationException("RemoteGitWriter 缺 Token");
    }
}
