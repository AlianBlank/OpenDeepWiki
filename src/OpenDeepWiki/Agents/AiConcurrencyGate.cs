using System.Collections.Concurrent;

namespace OpenDeepWiki.Agents;

/// <summary>
/// 按 AI 服务商 host 的全局并发闸门：目录/内容/翻译/思维导图/Chat 等多路 worker
/// 共享同一渠道（如 open.bigmodel.cn）时统一限并发，防止叠加打满触发 429。
/// 每个 HTTP attempt 持有一个名额，重试等待期间释放，Retry-After 由 LoggingHttpHandler 处理。
/// <para>配置 <c>GameFrameX:AiGate:MaxConcurrencyPerProvider</c>（env
/// GFX_AI_GATE_MAX_CONCURRENCY，默认 2；&lt;=0 不限制）。</para>
/// </summary>
public static class AiConcurrencyGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _maxConcurrencyPerProvider = LoadDefault();

    /// <summary>每渠道最大并发请求数（&lt;=0 表示不限制）。</summary>
    public static int MaxConcurrencyPerProvider
    {
        get => _maxConcurrencyPerProvider;
        set => _maxConcurrencyPerProvider = value;
    }

    /// <summary>
    /// 获取一个渠道并发名额；返回的 IDisposable 释放名额。
    /// 等待超过 1 秒时记一条日志，便于观察闸门是否过紧。
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string? host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || _maxConcurrencyPerProvider <= 0)
        {
            return NoopReleaser.Instance;
        }

        var gate = Gates.GetOrAdd(
            host,
            _ => new SemaphoreSlim(_maxConcurrencyPerProvider, _maxConcurrencyPerProvider));

        var waitStart = DateTime.UtcNow;
        await gate.WaitAsync(cancellationToken);
        var waited = DateTime.UtcNow - waitStart;
        if (waited > TimeSpan.FromSeconds(1))
        {
            Serilog.Log.Information(
                "AI 并发闸等待 {WaitMs}ms（host={Host}，当前上限 {Limit}），请求已放行",
                waited.TotalMilliseconds, host, _maxConcurrencyPerProvider);
        }

        return new Releaser(gate);
    }

    private static int LoadDefault()
    {
        var raw = Environment.GetEnvironmentVariable("GFX_AI_GATE_MAX_CONCURRENCY");
        return int.TryParse(raw, out var value) ? value : 2;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Release();
        }
    }

    private sealed class NoopReleaser : IDisposable
    {
        public static readonly NoopReleaser Instance = new NoopReleaser();

        public void Dispose()
        {
        }
    }
}
