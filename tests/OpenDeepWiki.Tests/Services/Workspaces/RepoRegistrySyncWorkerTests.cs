using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.Services.Workspaces;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Workspaces;

/// <summary>
/// RepoRegistrySyncWorker 核心逻辑单测：待入库挑选（去重/排除已入库/每轮上限）与
/// RepoRegistryProvider.CollectActiveEntries 的停用剪枝收集。
/// </summary>
public class RepoRegistrySyncWorkerTests
{
    [Fact]
    public void SelectReposToCreate_去重排除已有并截断到上限()
    {
        var entries = new List<RepoRegistryEntry>
        {
            new("repo-a", "https://github.com/GameFrameX/repo-a.git"),
            new("repo-b", "https://github.com/GameFrameX/repo-b.git"),
            new("Repo-A", "https://github.com/GameFrameX/repo-a.git"), // 重复（忽略大小写）
            new("existing", "https://github.com/GameFrameX/existing.git"),
            new("repo-c", "https://github.com/GameFrameX/repo-c.git") // 超出上限被截断
        };
        var existing = new[] { "existing" };

        var result = RepoRegistrySyncWorker.SelectReposToCreate(entries, existing, maxPerCycle: 2);

        Assert.Equal(new[] { "repo-a", "repo-b" }, result.Select(r => r.RepoName).ToArray());
    }

    [Fact]
    public void SelectReposToCreate_全部已存在_返回空()
    {
        var entries = new List<RepoRegistryEntry> { new("repo-a", "url-a") };

        var result = RepoRegistrySyncWorker.SelectReposToCreate(entries, new[] { "REPO-A" }, 3);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectActiveEntries_剪枝停用子树与停用条目()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        var entries = provider.CollectActiveEntries();

        // fixture：client 子组（unity 活跃 / inactive-repo 条目停用）、shared 子组（foundation 活跃）、
        // paused 子组整树停用（paused-repo 不收集）、gfx-pkgs 组（config 活跃 / payment.google 条目停用）
        Assert.Equal(
            new[] { "GameFrameX.Unity", "GameFrameX.Foundation", "com.gameframex.unity.config" },
            entries.Select(e => e.RepoName).ToArray());
        Assert.Contains(entries, e => e.GitUrl == "https://github.com/GameFrameX/com.gameframex.unity.config.git");
    }

    private static RepoRegistryProvider CreateProvider(string registryPath)
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["RspressExport:RegistryPath"]).Returns(registryPath);
        return new RepoRegistryProvider(config.Object, NullLogger<RepoRegistryProvider>.Instance);
    }
}
