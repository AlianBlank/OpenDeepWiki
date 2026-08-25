using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.Services.Workspaces;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Workspaces;

/// <summary>
/// RepoRegistryProvider v4 目录树遍历单测：docPath 落位、titles 双形式、upstream 溯源、
/// 条目级停用与节点级整树剪枝。
/// </summary>
public class RepoRegistryProviderTests
{
    [Fact]
    public void Match_ActiveEntry_ReturnsDocPathAndObjectTitles()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        var match = provider.Match("GameFrameX.Unity");

        Assert.NotNull(match);
        Assert.Equal(".auto/components/unity", match.DocPath);
        Assert.NotNull(match.Titles);
        Assert.Equal("Unity 工程", match.Titles["zh"]);
        Assert.Equal("Unity 工程", match.Titles["ZH"]); // 语言键大小写不敏感
        Assert.Equal("Unity", match.Titles["en"]);
        Assert.Null(match.Upstream);
    }

    [Fact]
    public void Match_PackageRepo_ReturnsSubDirectoryDocPathStringTitlesAndUpstream()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        var match = provider.Match("com.gameframex.unity.config");

        Assert.NotNull(match);
        // components 域下的 packageTreeRule 展开子目录
        Assert.Equal(".auto/components/unity/config", match.DocPath);
        // titles string 简写 → "*" 全语言通用键
        Assert.NotNull(match.Titles);
        Assert.Equal("配置包", match.Titles["*"]);
        Assert.Equal("GameFrameX/GameFrameX.Config", match.Upstream);
    }

    [Fact]
    public void Match_InactiveEntry_ReturnsNull()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        Assert.Null(provider.Match("inactive-repo"));
    }

    [Fact]
    public void Match_StoppedNodeSubtree_ReturnsNull()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        // paused 子组节点 active=false，其下条目即使自身 active=true 也整树停用
        Assert.Null(provider.Match("paused-repo"));
    }

    [Fact]
    public void Match_UnregisteredRepo_ReturnsNull()
    {
        var provider = CreateProvider(TestRegistryJson.WriteToTempFile());

        Assert.Null(provider.Match("some-other-repo"));
    }

    [Fact]
    public void Match_MissingRegistryFile_ReturnsNull()
    {
        var provider = CreateProvider(Path.Combine(Path.GetTempPath(), "odw_no_such_registry.json"));

        Assert.Null(provider.Match("GameFrameX.Unity"));
    }

    /// <summary>
    /// 真实 registry 冒烟（可选）：设置 GFX_REGISTRY_PATH 指向 Docs 仓的 repo-registry.json 时运行，
    /// 未设置时跳过（CI 无该文件）。锁定 v4 当前 9 个 active 仓的落位。
    /// </summary>
    [Fact]
    public void Match_RealRegistry_ActiveReposResolveExpectedDocPaths()
    {
        var registryPath = Environment.GetEnvironmentVariable("GFX_REGISTRY_PATH");
        if (string.IsNullOrWhiteSpace(registryPath) || !File.Exists(registryPath))
        {
            return; // 环境未提供真实 registry，跳过
        }

        var provider = CreateProvider(registryPath);

        // 9 个 active 仓的 docPath（v4.0.0 快照）
        var expected = new Dictionary<string, string>
        {
            ["GameFrameX.Unity"] = ".auto/components/unity",
            ["GameFrameX.Server"] = ".auto/components/server",
            ["GameFrameX.Server.Source"] = ".auto/components/server",
            ["GameFrameX.Foundation"] = ".auto/components/foundation",
            ["GameFrameX.Config"] = ".auto/data-and-config",
            ["GameFrameX.Protobuf"] = ".auto/data-and-config",
            ["com.gameframex.unity"] = ".auto/components/unity",
            ["com.gameframex.unity.config"] = ".auto/components/unity/config",
            ["OpenDeepWiki"] = ".auto/tools-and-specification"
        };
        foreach (var pair in expected)
        {
            var match = provider.Match(pair.Key);
            Assert.NotNull(match);
            Assert.Equal(pair.Value, match.DocPath);
        }

        // 默认停用的包仓：命中条目但 active=false → 不导出
        Assert.Null(provider.Match("com.gameframex.unity.ui"));
        // 未登记仓 → 不导出
        Assert.Null(provider.Match("not-registered-repo"));
    }

    private static RepoRegistryProvider CreateProvider(string registryPath)
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["RspressExport:RegistryPath"]).Returns(registryPath);
        return new RepoRegistryProvider(config.Object, NullLogger<RepoRegistryProvider>.Instance);
    }
}
