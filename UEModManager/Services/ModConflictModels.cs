using System;
using System.Collections.Generic;

namespace UEModManager.Services
{
    public class ConflictEntry
    {
        public string AssetPath { get; set; } = string.Empty;
        public List<string> Mods { get; set; } = new();
    }

    public class ModConflictSummary
    {
        public string ModName { get; set; } = string.Empty;
        public string OriginalName { get; set; } = string.Empty;
        public int ConflictCount { get; set; }
        public List<string> ConflictAssetsTop5 { get; set; } = new();
    }

    public class ModConflictResult
    {
        public int ScannedMods { get; set; }
        public int TotalAssets { get; set; }
        public int ConflictAssets { get; set; }
        public List<ConflictEntry> Conflicts { get; set; } = new();
        public List<ModConflictSummary> Summaries { get; set; } = new();
        public string ModeDescription { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;
    }
}