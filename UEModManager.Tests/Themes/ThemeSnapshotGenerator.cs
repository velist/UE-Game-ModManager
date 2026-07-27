using System;
using System.IO;
using System.Text;

namespace UEModManager.Tests.Themes;

/// <summary>
/// 快照生成器。平时跳过，只在**有意变更主题取值**后手动放开跑一次重新生成。
/// 输出写到 %TEMP%/uemm-theme-snapshot.cs，人工核对差异后再替换 ThemeSnapshot.cs。
/// </summary>
public class ThemeSnapshotGenerator
{
    [Fact(Skip = "手动运行：有意变更主题取值后重新生成快照")]
    public void 生成快照()
    {
        var capture = ThemeResourceLoader.CaptureMerged();

        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace UEModManager.Tests.Themes;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// 主题资源快照。由 ThemeSnapshotGenerator 生成，请勿手工编辑。");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static partial class ThemeSnapshot");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly string[] Keys =");
        sb.AppendLine("    {");
        foreach (var k in capture.Keys)
            sb.AppendLine($"        \"{k}\",");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public static readonly Dictionary<string, string> Values = new()");
        sb.AppendLine("    {");
        foreach (var k in capture.Keys)
            sb.AppendLine($"        [\"{k}\"] = \"{capture.Values[k]}\",");
        sb.AppendLine("    };");
        sb.AppendLine("}");

        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "uemm-theme-snapshot.cs"),
            sb.ToString(), Encoding.UTF8);
    }
}
