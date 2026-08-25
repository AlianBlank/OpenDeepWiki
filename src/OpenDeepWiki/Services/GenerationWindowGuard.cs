using System.Globalization;

namespace OpenDeepWiki.Services;

/// <summary>
/// 文档生成时间窗口守卫：只在配置的每日时间段内允许生成类 worker 领取新任务
/// （RepositoryProcessing / BranchGeneration / Translation / IncrementalUpdate 共用）。
/// 支持跨午夜窗口（如 23:00 → 次日 10:00）；窗口外已开始的任务不中断，只是不再领取新任务。
/// <para>配置 <c>GameFrameX:GenerationWindow:{Enabled,Start,End,TimeZone}</c>；
/// 未启用或 Start/End 配置无效时恒返回 true（不挡生成）。TimeZone 默认 Asia/Shanghai。</para>
/// </summary>
public class GenerationWindowGuard
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<GenerationWindowGuard> _logger;
    private int _lastWithin = -1; // -1 未知；0 窗口外；1 窗口内

    public GenerationWindowGuard(IConfiguration configuration, ILogger<GenerationWindowGuard> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>当前时刻是否在生成窗口内；窗口切换时记一条 Info 日志。</summary>
    public bool IsWithinWindow()
    {
        var within = IsWithinWindow(DateTime.UtcNow);
        var withinFlag = within ? 1 : 0;
        var last = Interlocked.Exchange(ref _lastWithin, withinFlag);
        if (last != withinFlag)
        {
            if (within)
            {
                _logger.LogInformation("进入文档生成时间窗口，恢复领取生成任务。");
            }
            else
            {
                _logger.LogInformation("超出文档生成时间窗口，暂停领取新任务；进行中的生成会在当前文档完成后暂停，下个窗口自动续跑。");
            }
        }

        return within;
    }

    /// <summary>可测入口：给定 UTC 时刻是否在生成窗口内（Kind 未指定的按 UTC 处理）。</summary>
    internal bool IsWithinWindow(DateTime utcNow)
    {
        if (utcNow.Kind == DateTimeKind.Unspecified)
        {
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        }

        if (!bool.TryParse(_configuration["GameFrameX:GenerationWindow:Enabled"], out var enabled) || !enabled)
        {
            return true;
        }

        if (!TimeOnly.TryParse(_configuration["GameFrameX:GenerationWindow:Start"], CultureInfo.InvariantCulture, out var start) ||
            !TimeOnly.TryParse(_configuration["GameFrameX:GenerationWindow:End"], CultureInfo.InvariantCulture, out var end))
        {
            _logger.LogWarning("生成时间窗口配置无效（Start/End 需为 HH:mm），不限制生成。");
            return true;
        }

        var timeZone = ResolveTimeZone();
        var localNow = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));

        // start <= end 为同日窗口（如 10:00-18:00），否则为跨午夜窗口（如 23:00-10:00）
        return start <= end
            ? localNow >= start && localNow < end
            : localNow >= start || localNow < end;
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        var id = _configuration["GameFrameX:GenerationWindow:TimeZone"];
        if (string.IsNullOrWhiteSpace(id))
        {
            id = "Asia/Shanghai";
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            if (id == "Asia/Shanghai")
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); // Windows 命名
                }
                catch (Exception)
                {
                    // 继续回退
                }
            }

            _logger.LogWarning("时区 {TimeZoneId} 不可用，回退服务器本地时区。", id);
            return TimeZoneInfo.Local;
        }
    }
}
