using System.ClientModel;
using System.Net;

namespace OpenDeepWiki.Services;

/// <summary>
/// AI 配额熔断器：生成类 worker 遇到 token 不足 / 限流（HTTP 429、402 或 insufficient quota 等错误）
/// 时打开熔断，冷却期内不再领取新任务，冷却结束后自动恢复领取 —— 即"等待 token 恢复后继续"。
/// 冷却时间指数退避（默认 5min 起步、翻倍递增、上限 1h），任一次 AI 调用成功即全部重置。
/// <para>配置 <c>GameFrameX:QuotaBreaker:{Enabled,InitialCooldownSeconds,MaxCooldownSeconds}</c>；
/// 未启用时 <see cref="Trip"/> 恒返回 false（维持原有失败语义）。</para>
/// </summary>
public class AiQuotaCircuitBreaker
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiQuotaCircuitBreaker> _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();
    private DateTimeOffset _reopensAtUtc = DateTimeOffset.MinValue;
    private TimeSpan _currentCooldown = TimeSpan.Zero;

    public AiQuotaCircuitBreaker(IConfiguration configuration, ILogger<AiQuotaCircuitBreaker> logger)
        : this(configuration, logger, static () => DateTimeOffset.UtcNow)
    {
    }

    internal AiQuotaCircuitBreaker(
        IConfiguration configuration,
        ILogger<AiQuotaCircuitBreaker> logger,
        Func<DateTimeOffset> utcNow)
    {
        _configuration = configuration;
        _logger = logger;
        _utcNow = utcNow;
    }

    /// <summary>熔断中（冷却期未结束）。</summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _utcNow() < _reopensAtUtc;
            }
        }
    }

    /// <summary>
    /// 异常为配额类错误时打开熔断并返回 true（指数退避）；否则不动作返回 false。
    /// 调用方据返回值决定是否保持任务待重试（而非标 Failed）。
    /// </summary>
    public bool Trip(Exception exception)
    {
        if (!bool.TryParse(_configuration["GameFrameX:QuotaBreaker:Enabled"], out var enabled) || !enabled)
        {
            return false;
        }

        if (!IsQuotaError(exception))
        {
            return false;
        }

        var initial = TimeSpan.FromSeconds(ParsePositive("InitialCooldownSeconds", 300));
        var max = TimeSpan.FromSeconds(ParsePositive("MaxCooldownSeconds", 3600));

        lock (_lock)
        {
            // ponytail: 简单倍增退避，不区分限流（秒级恢复）与余额耗尽（人工充值才恢复）
            _currentCooldown = _currentCooldown == TimeSpan.Zero
                ? initial
                : TimeSpan.FromTicks(Math.Min(_currentCooldown.Ticks * 2, max.Ticks));
            _reopensAtUtc = _utcNow() + _currentCooldown;
            var cooldown = _currentCooldown;
            _logger.LogWarning(
                "AI 配额不足，打开熔断：{Cooldown} 后自动恢复领取生成任务。Error: {Error}",
                cooldown, exception.Message);
        }

        return true;
    }

    /// <summary>任一次生成成功即关闭熔断并重置退避。</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_currentCooldown != TimeSpan.Zero)
            {
                _currentCooldown = TimeSpan.Zero;
                _reopensAtUtc = DateTimeOffset.MinValue;
                _logger.LogInformation("AI 调用成功，配额熔断已重置。");
            }
        }
    }

    /// <summary>
    /// 判断异常是否为 AI 配额/限流类错误：异常链上存在 HTTP 429 / 402 的请求失败异常
    /// （OpenAI SDK 的 <see cref="ClientResultException"/> / BCL 的 <see cref="HttpRequestException"/>），
    /// 或消息含 insufficient quota / balance / 余额不足等特征。
    /// </summary>
    internal static bool IsQuotaError(Exception exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            if (ex is ClientResultException requestFailed && (requestFailed.Status == 429 || requestFailed.Status == 402))
            {
                return true;
            }

            if (ex is HttpRequestException http &&
                (http.StatusCode == HttpStatusCode.TooManyRequests || http.StatusCode == HttpStatusCode.PaymentRequired))
            {
                return true;
            }

            if (MatchesQuotaMessage(ex.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesQuotaMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        var m = message.ToLowerInvariant();
        return m.Contains("insufficient_quota")
               || m.Contains("quota") && m.Contains("exceed")
               || m.Contains("insufficient") && m.Contains("balance")
               || m.Contains("rate limit")
               || m.Contains("余额不足")
               || m.Contains("欠费");
    }

    private int ParsePositive(string key, int fallback)
        => int.TryParse(_configuration[$"GameFrameX:QuotaBreaker:{key}"], out var value) && value > 0
            ? value
            : fallback;
}
