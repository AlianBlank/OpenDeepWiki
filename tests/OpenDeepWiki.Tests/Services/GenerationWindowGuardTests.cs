using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.Services;
using Xunit;

namespace OpenDeepWiki.Tests.Services;

/// <summary>
/// GenerationWindowGuard 时间窗口判定单测：跨午夜/同日窗口、时区转换（UTC → Asia/Shanghai）、
/// 未启用与配置无效时不限制。
/// </summary>
public class GenerationWindowGuardTests
{
    // Asia/Shanghai = UTC+8：上海 23:30 = UTC 15:30；上海 09:59 = UTC 01:59；上海 10:00 = UTC 02:00；上海 22:59 = UTC 14:59
    [Theory]
    [InlineData("23:00", "10:00", "2026-08-26T15:30:00Z", true)]   // 上海 23:30，跨午夜窗口内
    [InlineData("23:00", "10:00", "2026-08-26T01:59:00Z", true)]   // 上海 09:59，窗口内
    [InlineData("23:00", "10:00", "2026-08-26T02:00:00Z", false)]  // 上海 10:00，窗口外（End 开区间）
    [InlineData("23:00", "10:00", "2026-08-26T14:59:00Z", false)]  // 上海 22:59，窗口外
    [InlineData("10:00", "18:00", "2026-08-26T04:00:00Z", true)]   // 同日窗口：上海 12:00，窗口内
    [InlineData("10:00", "18:00", "2026-08-26T01:59:00Z", false)]  // 上海 09:59，窗口外
    [InlineData("10:00", "18:00", "2026-08-26T10:00:00Z", false)]  // 上海 18:00，窗口外
    public void IsWithinWindow_按窗口与时区判定(string start, string end, string utcNow, bool expected)
    {
        var guard = CreateGuard(new Dictionary<string, string?>
        {
            ["GameFrameX:GenerationWindow:Enabled"] = "true",
            ["GameFrameX:GenerationWindow:Start"] = start,
            ["GameFrameX:GenerationWindow:End"] = end,
            ["GameFrameX:GenerationWindow:TimeZone"] = "Asia/Shanghai"
        });

        var result = guard.IsWithinWindow(DateTime.Parse(
            utcNow,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsWithinWindow_未启用_恒允许()
    {
        var guard = CreateGuard(new Dictionary<string, string?>
        {
            ["GameFrameX:GenerationWindow:Enabled"] = "false",
            ["GameFrameX:GenerationWindow:Start"] = "23:00",
            ["GameFrameX:GenerationWindow:End"] = "10:00"
        });

        Assert.True(guard.IsWithinWindow(DateTime.Parse("2026-08-26T04:00:00Z")));
    }

    [Fact]
    public void IsWithinWindow_Start配置无效_恒允许()
    {
        var guard = CreateGuard(new Dictionary<string, string?>
        {
            ["GameFrameX:GenerationWindow:Enabled"] = "true",
            ["GameFrameX:GenerationWindow:Start"] = "25:99",
            ["GameFrameX:GenerationWindow:End"] = "10:00"
        });

        Assert.True(guard.IsWithinWindow(DateTime.Parse("2026-08-26T04:00:00Z")));
    }

    private static GenerationWindowGuard CreateGuard(Dictionary<string, string?> settings)
    {
        var config = new Mock<IConfiguration>();
        foreach (var pair in settings)
        {
            config.Setup(c => c[pair.Key]).Returns(pair.Value);
        }

        return new GenerationWindowGuard(config.Object, NullLogger<GenerationWindowGuard>.Instance);
    }
}
