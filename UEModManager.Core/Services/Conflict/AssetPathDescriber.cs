using System;
using System.Collections.Generic;
using System.Linq;

namespace UEModManager.Services.Conflict.Model;

/// <summary>
/// 资产类别。按文件名前缀判定 —— UE 的资产命名约定是工程内强制的，
/// 读文件名比读资产头便宜得多，且容器索引里本来就没有资产头。
/// </summary>
public enum AssetCategory
{
    Texture,
    Mesh,
    Material,
    Blueprint,
    Animation,
    Audio,
    Data,
    Other
}

/// <summary>
/// 把资产键翻译成玩家能读的类别与短名。
/// </summary>
public static class AssetPathDescriber
{
    /// <summary>按文件名前缀归类。</summary>
    public static AssetCategory Categorize(string assetKey)
    {
        if (string.IsNullOrWhiteSpace(assetKey)) return AssetCategory.Other;

        var name = FileNameOf(assetKey);
        var bare = StripVariantSuffix(StripPrefix(name));

        return name switch
        {
            _ when HasPrefix(name, "T_") => AssetCategory.Texture,
            _ when HasPrefix(name, "SK_") || HasPrefix(name, "SM_")
                || HasPrefix(name, "SKEL_") || HasPrefix(name, "PA_") => AssetCategory.Mesh,
            _ when HasPrefix(name, "M_") || HasPrefix(name, "MI_") => AssetCategory.Material,
            _ when HasPrefix(name, "BP_") => AssetCategory.Blueprint,
            _ when HasPrefix(name, "A_") || HasPrefix(name, "AM_") || HasPrefix(name, "ABP_") => AssetCategory.Animation,
            _ when HasPrefix(name, "SW_") || HasPrefix(name, "SC_") => AssetCategory.Audio,
            _ when HasPrefix(name, "DT_") || HasPrefix(name, "CT_") => AssetCategory.Data,
            _ => AssetCategory.Other
        };
    }

    /// <summary>去前缀、去变体后缀的短名。</summary>
    public static string ShortName(string assetKey)
    {
        if (string.IsNullOrWhiteSpace(assetKey)) return string.Empty;
        return StripVariantSuffix(StripPrefix(FileNameOf(assetKey)));
    }

    /// <summary>
    /// 拼出"1 张贴图、1 个模型"。数量为 0 的类别不出现；
    /// 顺序固定，避免同一组内容因字典顺序不同而让快照漂移。
    /// </summary>
    public static string Summarize(IEnumerable<AssetCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var counts = categories
            .GroupBy(c => c)
            .ToDictionary(g => g.Key, g => g.Count());

        var parts = Order
            .Where(counts.ContainsKey)
            .Select(c => $"{counts[c]} {Measure(c)}")
            .ToList();

        return parts.Count == 0 ? string.Empty : string.Join("、", parts);
    }

    /// <summary>类别的中文名。</summary>
    public static string DisplayName(AssetCategory category) => category switch
    {
        AssetCategory.Texture => "贴图",
        AssetCategory.Mesh => "模型",
        AssetCategory.Material => "材质",
        AssetCategory.Blueprint => "蓝图",
        AssetCategory.Animation => "动画",
        AssetCategory.Audio => "音频",
        AssetCategory.Data => "数据表",
        _ => "其他"
    };

    /// <summary>类别的量词：贴图"张"、动画"段"、数据表"张"，其余"个"。</summary>
    public static string Measure(AssetCategory category) => category switch
    {
        AssetCategory.Texture => "张贴图",
        AssetCategory.Animation => "段动画",
        AssetCategory.Data => "张数据表",
        AssetCategory.Mesh => "个模型",
        AssetCategory.Material => "个材质",
        AssetCategory.Blueprint => "个蓝图",
        AssetCategory.Audio => "个音频",
        _ => "个其他资源"
    };

    /// <summary>统计中出现类别的固定顺序。</summary>
    private static readonly AssetCategory[] Order =
    [
        AssetCategory.Texture, AssetCategory.Mesh, AssetCategory.Material,
        AssetCategory.Blueprint, AssetCategory.Animation, AssetCategory.Audio,
        AssetCategory.Data, AssetCategory.Other
    ];

    private static string FileNameOf(string assetKey)
    {
        var normalized = assetKey.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static bool HasPrefix(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉第一段下划线前缀（T_ / SK_ / MI_ …）。没有前缀时原样返回。</summary>
    private static string StripPrefix(string name)
    {
        var underscore = name.IndexOf('_');
        if (underscore <= 0 || underscore >= name.Length - 1) return name;

        // 只认已知的两类前缀：单字母（T_/M_/A_）和两字母（SK_/SM_/MI_/BP_/DT_…）。
        var prefix = name[..underscore];
        return prefix.Length is 1 or 2 && prefix.All(char.IsLetter) ? name[(underscore + 1)..] : name;
    }

    /// <summary>去掉贴图变体后缀 _D / _N / _M / _R / _ORM / _P。</summary>
    private static string StripVariantSuffix(string name)
    {
        foreach (var suffix in VariantSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        }
        return name;
    }

    private static readonly string[] VariantSuffixes = ["_ORM", "_D", "_N", "_M", "_R", "_P"];
}
