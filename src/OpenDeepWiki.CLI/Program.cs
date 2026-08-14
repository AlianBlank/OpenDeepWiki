using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Workspaces;
using OpenDeepWiki.Sqlite;
using Serilog;

namespace OpenDeepWiki.CLI;

/// <summary>
/// OpenDeepWiki CLI 入口。提供 GameFrameX 工作区的 bootstrap / ingest / generate-* / translate / snapshot
/// 子命令，便于在 CI 或本地脚本中无 Web UI 地驱动文档生成。
///
/// 命令格式：
///   odw bootstrap --registry &lt;path&gt; --group &lt;id&gt; [--db &lt;path&gt;]
///   odw ingest|generate-catalog|generate-content|translate|snapshot --group &lt;id&gt; [--db &lt;path&gt;]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CLI 执行失败");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var opts = ParseOptions(args.AsSpan(1));

        if (!opts.TryGetValue("db", out var dbPath)) dbPath = "odw.db";

        switch (command)
        {
            case "bootstrap":
            {
                if (!opts.TryGetValue("group", out var group) || string.IsNullOrWhiteSpace(group))
                {
                    Log.Error("bootstrap 需要 --group &lt;id&gt;");
                    return 1;
                }
                if (!opts.TryGetValue("registry", out var registry))
                    registry = "gfx-config/repo-registry.json";

                using var host = BuildHost(dbPath);
                using var scope = host.Services.CreateScope();
                // 先确保 schema 存在（上游 migration 链有历史不同步，EnsureCreated 直接基于模型建表）
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SqliteDbContext>>();
                await using (var ensureCtx = factory.CreateDbContext())
                {
                    await ensureCtx.Database.EnsureCreatedAsync();
                }
                var svc = scope.ServiceProvider.GetRequiredService<IWorkspaceService>();
                return await BootstrapAsync(svc, registry, group);
            }
            case "ingest":
                Log.Warning("ingest 未实现（Phase 2 / C3）");
                return 2;
            case "generate-catalog":
                Log.Warning("generate-catalog 未实现（Phase 2 / C3）");
                return 2;
            case "generate-content":
                Log.Warning("generate-content 未实现（Phase 3 / C4）");
                return 2;
            case "translate":
                Log.Warning("translate 未实现（Phase 4 / C5）");
                return 2;
            case "snapshot":
                Log.Warning("snapshot 未实现（Phase 5 / C6）");
                return 2;
            default:
                Log.Error("未知命令：{Command}（用 --help 查看支持的命令）", command);
                return 1;
        }
    }

    private static async Task<int> BootstrapAsync(IWorkspaceService svc, string registry, string group)
    {
        var registryPath = Path.GetFullPath(registry);
        if (!File.Exists(registryPath))
        {
            Log.Error("repo-registry.json 不存在：{Path}", registryPath);
            return 1;
        }

        Log.Information("Bootstrapping group {Group} from {Registry}", group, registryPath);
        var g = await svc.UpsertFromRegistryAsync(registryPath, group);
        Log.Information("OK. Group '{GroupId}' / Name='{Name}' / Repos={Count}",
            g.Id, g.Name, g.Repos.Count);
        return 0;
    }

    private static IHost BuildHost(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddPooledDbContextFactory<SqliteDbContext>(
                    options => options
                        .UseSqlite($"DataSource={fullPath}")
                        .ConfigureWarnings(w => w.Ignore(
                            Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

                services.AddScoped<IContext>(sp =>
                    sp.GetRequiredService<IDbContextFactory<SqliteDbContext>>().CreateDbContext());

                services.AddScoped<IWorkspaceService, WorkspaceService>();
            })
            .Build();
    }

    /// <summary>
    /// 极简 --key value 解析器。支持 --key value / --key=value 两种形式。
    /// </summary>
    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;

            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                var key = a[2..eq];
                var val = a[(eq + 1)..];
                dict[key] = val;
            }
            else
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    dict[key] = args[i + 1];
                    i++;
                }
                else
                {
                    dict[key] = "true";
                }
            }
        }
        return dict;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        OpenDeepWiki CLI — GameFrameX 工作区驱动

        用法：
          odw <command> [options]

        命令：
          bootstrap           从 repo-registry.json 导入或更新工作区仓组（Phase 1 已实现）
          ingest              拉取/同步仓组内仓库到本地（Phase 2 / C3）
          generate-catalog    生成 catalog（Phase 2 / C3）
          generate-content    生成内容（Phase 3 / C4）
          translate           翻译生成内容（Phase 4 / C5）
          snapshot            生成版本快照（Phase 5 / C6）

        通用选项：
          --db <path>          SQLite 数据库文件路径（默认 odw.db）
          --registry <path>    repo-registry.json 路径（bootstrap 必填，默认 gfx-config/repo-registry.json）
          --group <id>         组 Id（如 gfx-main）

        示例：
          odw bootstrap --registry gfx-config/repo-registry.json --group gfx-main
        """);
    }
}
