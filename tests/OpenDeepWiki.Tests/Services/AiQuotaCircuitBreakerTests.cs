using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.Services;
using Xunit;

namespace OpenDeepWiki.Tests.Services;

/// <summary>
/// AiQuotaCircuitBreaker 单测：配额类错误判定（HTTP 429/402、消息特征、嵌套异常）、
/// 熔断打开/冷却恢复/指数退避/成功重置、未启用时维持原语义。
/// </summary>
public class AiQuotaCircuitBreakerTests
{
    [Fact]
    public void IsQuotaError_429或402的请求失败异常_判定为配额错误()
    {
        Assert.True(AiQuotaCircuitBreaker.IsQuotaError(QuotaException(429, "rate limited")));
        Assert.True(AiQuotaCircuitBreaker.IsQuotaError(QuotaException(402, "payment required")));
        Assert.False(AiQuotaCircuitBreaker.IsQuotaError(QuotaException(500, "server error")));
    }

    [Fact]
    public void IsQuotaError_消息特征与嵌套异常_判定为配额错误()
    {
        Assert.True(AiQuotaCircuitBreaker.IsQuotaError(
            new InvalidOperationException("OpenAI API error: insufficient_quota, please add credits")));
        Assert.True(AiQuotaCircuitBreaker.IsQuotaError(
            new InvalidOperationException("余额不足，请充值")));
        Assert.False(AiQuotaCircuitBreaker.IsQuotaError(
            new InvalidOperationException("clone failed: repository not found")));

        // 配额错误藏在 InnerException 链深处也能识别
        Assert.True(AiQuotaCircuitBreaker.IsQuotaError(
            new InvalidOperationException("生成失败", QuotaException(429, "rate limited"))));
    }

    [Fact]
    public void Trip_配额错误打开熔断_冷却结束自动恢复()
    {
        var (breaker, advance) = CreateBreaker(
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"), initialCooldownSeconds: 300);

        Assert.True(breaker.Trip(QuotaException(429, "rate limited")));
        Assert.True(breaker.IsOpen); // 冷却期内

        advance(TimeSpan.FromMinutes(4));
        Assert.True(breaker.IsOpen);

        advance(TimeSpan.FromSeconds(60)); // 5 分钟冷却结束
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Trip_连续触发指数退避_成功后重置()
    {
        var (breaker, advance) = CreateBreaker(
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            initialCooldownSeconds: 300, maxCooldownSeconds: 3600);

        breaker.Trip(QuotaException(429, "rate limited")); // 5min
        advance(TimeSpan.FromMinutes(6));
        Assert.False(breaker.IsOpen);

        breaker.Trip(QuotaException(402, "payment required")); // 退避翻倍 → 10min
        advance(TimeSpan.FromMinutes(9));
        Assert.True(breaker.IsOpen);
        advance(TimeSpan.FromMinutes(2));
        Assert.False(breaker.IsOpen);

        breaker.Trip(QuotaException(402, "payment required")); // → 20min
        advance(TimeSpan.FromMinutes(1));
        breaker.RecordSuccess(); // 任一次成功全部重置

        breaker.Trip(QuotaException(402, "payment required")); // 重置后回到初始 5min
        advance(TimeSpan.FromMinutes(4));
        Assert.True(breaker.IsOpen);
        advance(TimeSpan.FromMinutes(1));
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Trip_非配额错误_不打开熔断()
    {
        var (breaker, _) = CreateBreaker(DateTimeOffset.UtcNow);

        Assert.False(breaker.Trip(new InvalidOperationException("clone failed")));
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void Trip_未启用_维持原失败语义()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["GameFrameX:QuotaBreaker:Enabled"]).Returns("false");
        var breaker = new AiQuotaCircuitBreaker(config.Object, NullLogger<AiQuotaCircuitBreaker>.Instance);

        Assert.False(breaker.Trip(QuotaException(429, "rate limited")));
        Assert.False(breaker.IsOpen);
    }

    /// <summary>构造带指定 HTTP 状态码的 ClientResultException（其构造器只接受 PipelineResponse）。</summary>
    private static ClientResultException QuotaException(int status, string message)
    {
        var response = new Mock<PipelineResponse>();
        response.SetupGet(r => r.Status).Returns(status);
        return new ClientResultException(message, response.Object);
    }

    /// <summary>注入可控时钟：advance 推进时间，breaker 内部读取同一变量。</summary>
    private static (AiQuotaCircuitBreaker Breaker, Action<TimeSpan> Advance) CreateBreaker(
        DateTimeOffset startTime,
        int initialCooldownSeconds = 300,
        int maxCooldownSeconds = 3600)
    {
        var now = startTime;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameFrameX:QuotaBreaker:Enabled"] = "true",
                ["GameFrameX:QuotaBreaker:InitialCooldownSeconds"] = initialCooldownSeconds.ToString(),
                ["GameFrameX:QuotaBreaker:MaxCooldownSeconds"] = maxCooldownSeconds.ToString()
            })
            .Build();
        return (new AiQuotaCircuitBreaker(
            config,
            NullLogger<AiQuotaCircuitBreaker>.Instance,
            () => now), delta => now += delta);
    }
}
