namespace OpenDeepWiki.Tests.Services.Workspaces;

/// <summary>
/// v4 目录树形状的 repo-registry.json 测试样本：模拟真实 registry 的关键形态——
/// 组 → 子组 → 仓库三层树、节点级 active 剪枝（paused 子组）、条目级停用（inactive-repo /
/// payment.google）、titles object 与 string 双形式、upstream 溯源、docPath 子目录展开。
/// </summary>
public static class TestRegistryJson
{
    public static readonly string V4Tree = @"
{
  ""version"": ""4.0.0"",
  ""groups"": [
    {
      ""name"": ""gfx-test"",
      ""displayName"": ""Test Group"",
      ""ingestMode"": ""git-url"",
      ""note"": ""测试组"",
      ""children"": [
        {
          ""name"": ""client"",
          ""displayName"": ""客户端"",
          ""repositories"": [
            {
              ""alias"": ""unity"",
              ""active"": true,
              ""gitUrl"": ""https://github.com/GameFrameX/GameFrameX.Unity.git"",
              ""docPath"": "".auto/components/unity"",
              ""titles"": { ""zh"": ""Unity 工程"", ""en"": ""Unity"" }
            },
            {
              ""alias"": ""inactive-repo"",
              ""active"": false,
              ""gitUrl"": ""https://github.com/GameFrameX/inactive-repo.git"",
              ""docPath"": "".auto/components/unity""
            }
          ]
        },
        {
          ""name"": ""shared"",
          ""displayName"": ""共享"",
          ""repositories"": [
            {
              ""alias"": ""foundation"",
              ""active"": true,
              ""gitUrl"": ""https://github.com/GameFrameX/GameFrameX.Foundation.git"",
              ""docPath"": "".auto/components/foundation""
            }
          ]
        },
        {
          ""name"": ""paused"",
          ""displayName"": ""已停用子组"",
          ""active"": false,
          ""repositories"": [
            {
              ""alias"": ""paused-repo"",
              ""active"": true,
              ""gitUrl"": ""https://github.com/GameFrameX/paused-repo.git"",
              ""docPath"": "".auto/components/godot""
            }
          ]
        }
      ]
    },
    {
      ""name"": ""gfx-pkgs"",
      ""displayName"": ""Test Packages"",
      ""ingestMode"": ""git-url"",
      ""repositories"": [
        {
          ""alias"": ""com.gameframex.unity.config"",
          ""active"": true,
          ""gitUrl"": ""https://github.com/GameFrameX/com.gameframex.unity.config.git"",
          ""docPath"": "".auto/components/unity/config"",
          ""titles"": ""配置包"",
          ""upstream"": ""GameFrameX/GameFrameX.Config""
        },
        {
          ""alias"": ""com.gameframex.unity.payment.google"",
          ""active"": false,
          ""gitUrl"": ""https://github.com/GameFrameX/com.gameframex.unity.payment.google.git"",
          ""docPath"": "".auto/components/unity/payment/google"",
          ""upstream"": ""google/billing""
        }
      ]
    }
  ]
}";

    /// <summary>写入临时目录并返回 registry 文件路径。</summary>
    public static string WriteToTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "odw_reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "repo-registry.json");
        File.WriteAllText(path, V4Tree);
        return path;
    }
}
