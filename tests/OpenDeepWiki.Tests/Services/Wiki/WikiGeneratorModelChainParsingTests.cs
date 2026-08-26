using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki;

/// <summary>
/// 备选模型链配置解析单测：providerId:modelId 逗号分隔，无效项跳过。
/// </summary>
public class WikiGeneratorModelChainParsingTests
{
    [Fact]
    public void ParseModelPairs_标准格式_全部解析()
    {
        var pairs = WikiGenerator.ParseModelPairs("zhipu:glm-5, minimax:MiniMax-M3").ToList();

        Assert.Equal(2, pairs.Count);
        Assert.Equal(("zhipu", "glm-5"), pairs[0]);
        Assert.Equal(("minimax", "MiniMax-M3"), pairs[1]);
    }

    [Fact]
    public void ParseModelPairs_无效项_跳过不抛()
    {
        // 缺 provider、缺 model、空段
        var pairs = WikiGenerator.ParseModelPairs(":glm-5,zhipu:,no-separator, ,zhipu:glm-5").ToList();

        var pair = Assert.Single(pairs);
        Assert.Equal(("zhipu", "glm-5"), pair);
    }

    [Fact]
    public void ParseModelPairs_空与空白_返回空()
    {
        Assert.Empty(WikiGenerator.ParseModelPairs(null));
        Assert.Empty(WikiGenerator.ParseModelPairs(string.Empty));
        Assert.Empty(WikiGenerator.ParseModelPairs("  "));
    }
}
