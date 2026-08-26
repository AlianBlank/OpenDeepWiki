using System.Diagnostics;
using OpenDeepWiki.Agents;
using Xunit;

namespace OpenDeepWiki.Tests.Agents;

/// <summary>
/// AI 渠道并发闸单测：同名额受限、释放后再入、host 间独立、上限<=0 不限制。
/// </summary>
public class AiConcurrencyGateTests : IDisposable
{
    private readonly int _originalLimit = AiConcurrencyGate.MaxConcurrencyPerProvider;

    public AiConcurrencyGateTests()
    {
        AiConcurrencyGate.MaxConcurrencyPerProvider = 2;
    }

    public void Dispose()
    {
        AiConcurrencyGate.MaxConcurrencyPerProvider = _originalLimit;
    }

    [Fact]
    public async Task AcquireAsync_同host_并发达上限即阻塞()
    {
        var held = await AiConcurrencyGate.AcquireAsync("api.test-a.local", CancellationToken.None);
        var held2 = await AiConcurrencyGate.AcquireAsync("api.test-a.local", CancellationToken.None);

        // 第 3 个名额在释放前不可获得
        var third = AiConcurrencyGate.AcquireAsync("api.test-a.local", CancellationToken.None);
        var timeout = Task.Delay(300);
        var finished = await Task.WhenAny(third, timeout);
        Assert.NotSame(third, finished);

        held.Dispose();
        var done = await Task.WhenAny(third, Task.Delay(2000));
        Assert.Same(third, done);
        (await third).Dispose();
        held2.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_不同host_互不影响()
    {
        using var a = await AiConcurrencyGate.AcquireAsync("api.host-a.local", CancellationToken.None);
        using var b = await AiConcurrencyGate.AcquireAsync("api.host-b.local", CancellationToken.None);
        using var b2 = await AiConcurrencyGate.AcquireAsync("api.host-b.local", CancellationToken.None);
        // b 的 host 用满 2 个名额也不影响 a 再拿
        using var a2 = await AiConcurrencyGate.AcquireAsync("api.host-a.local", CancellationToken.None);
    }

    [Fact]
    public async Task AcquireAsync_上限关闭_不阻塞()
    {
        AiConcurrencyGate.MaxConcurrencyPerProvider = 0;
        var releasers = new List<IDisposable>();
        for (var i = 0; i < 10; i++)
        {
            releasers.Add(await AiConcurrencyGate.AcquireAsync("api.test-c.local", CancellationToken.None));
        }

        foreach (var r in releasers)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task AcquireAsync_大量并发_峰值不超上限()
    {
        var concurrent = 0;
        var peak = 0;
        var locker = new object();
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var gate = await AiConcurrencyGate.AcquireAsync("api.test-peak.local", CancellationToken.None);
            lock (locker)
            {
                concurrent++;
                peak = Math.Max(peak, concurrent);
            }

            await Task.Delay(50);
            lock (locker)
            {
                concurrent--;
            }
        }).ToArray();

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        Assert.True(sw.ElapsedMilliseconds >= 400); // 20 个请求 / 2 并发 × 50ms ≈ 至少 5 批
        Assert.True(peak <= 2, $"峰值并发 {peak} 超过上限 2");
    }
}
