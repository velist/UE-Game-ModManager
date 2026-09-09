using System.Collections.Generic;
using UEModManager.Services;

namespace UEModManager.Localization;

/// <summary>Display names only. Game configuration keys and custom names remain unchanged.</summary>
public static class GameDisplayNames
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["黑神话·悟空"] = "Black Myth: Wukong",
        ["剑星"] = "Stellar Blade",
        ["剑星 (CNS)"] = "Stellar Blade (CNS)",
        ["光与影：33号远征队"] = "Clair Obscur: Expedition 33",
        ["明末·渊虚之羽"] = "WUCHANG: Fallen Feathers",
        ["暗黑破坏神4"] = "Diablo IV",
        ["生化危机9"] = "Resident Evil Requiem",
        ["识质存在"] = "PRAGMATA",
        ["无主之地4"] = "Borderlands 4",
        ["死亡搁浅2"] = "Death Stranding 2",
        ["杀戮尖塔2"] = "Slay the Spire 2"
    };

    public static string For(string name)
        => LanguageManager.IsEnglish && English.TryGetValue(name, out var displayName) ? displayName : name;
}
