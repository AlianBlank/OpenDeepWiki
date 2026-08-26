using System.Net.Http;
using System.Net.Sockets;
using LibGit2Sharp;
using OpenDeepWiki.Services;
using Xunit;

namespace OpenDeepWiki.Tests.Services;

/// <summary>
/// 瞬态网络错误识别单测：网络抖动类异常保持 Pending 重试，永久性错误不误判。
/// </summary>
public class TransientNetworkErrorDetectorTests
{
    [Fact]
    public void IsTransient_github连接超时_识别为瞬态()
    {
        // 实际日志：LibGit2SharpException: failed to connect to github.com: Connection timed out
        var ex = new LibGit2SharpException("failed to connect to github.com: Connection timed out");

        Assert.True(TransientNetworkErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_dns解析失败_识别为瞬态()
    {
        var ex = new LibGit2SharpException("failed to resolve address for github.com: Name or service not known");

        Assert.True(TransientNetworkErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_http内层socket异常_识别为瞬态()
    {
        var ex = new HttpRequestException("An error occurred while sending the request", new SocketException());

        Assert.True(TransientNetworkErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_永久性git错误_不识别()
    {
        var ex = new LibGit2SharpException("unsupported URL protocol");

        Assert.False(TransientNetworkErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_普通异常_不识别()
    {
        Assert.False(TransientNetworkErrorDetector.IsTransient(new InvalidOperationException("boom")));
        Assert.False(TransientNetworkErrorDetector.IsTransient(new HttpRequestException("500 Internal Server Error")));
    }
}
