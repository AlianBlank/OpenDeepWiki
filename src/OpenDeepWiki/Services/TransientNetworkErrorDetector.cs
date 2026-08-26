using System.Net.Sockets;
using LibGit2Sharp;

namespace OpenDeepWiki.Services;

/// <summary>
/// 瞬态网络错误识别：clone / pull 或 HTTP 请求因连接超时、连接被拒、DNS 解析失败等
/// 网络抖动失败时，生成任务应保持 Pending 等待下轮重试（而非标 Failed）。
/// </summary>
public static class TransientNetworkErrorDetector
{
    // ponytail: LibGit2Sharp 把原生 git 错误统一包成 LibGit2SharpException，
    // 只能按消息关键字识别瞬态网络错误；遇到新的网络错误措辞需在此补关键字。
    private static readonly string[] TransientMessageKeywords = new string[]
    {
        "timed out",
        "connection refused",
        "failed to connect",
        "failed to resolve",
        "could not resolve host",
        "name or service not known",
        "connection reset",
        "unexpected disconnect",
        "early eof"
    };

    /// <summary>异常（含内部异常链）是否为瞬态网络错误。</summary>
    public static bool IsTransient(Exception exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            if (ex is SocketException)
            {
                return true;
            }

            if (ex is LibGit2SharpException && MatchesKeyword(ex.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesKeyword(string message)
    {
        foreach (var keyword in TransientMessageKeywords)
        {
            if (message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
