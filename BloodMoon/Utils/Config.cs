using System;
using UnityEngine;

namespace BloodMoon.Utils
{
    // Hardcoded configuration as per request. No file I/O.
    public class ModConfig
    {
        // Fixed Values
        public float SleepHours { get; } = 160f;
        public float ActiveHours { get; } = 48f;
        public float BossHealthMultiplier { get; } = 5.0f;
        public float MinionHealthMultiplier { get; } = 2.0f;
        public int BossMinionCount { get; } = 5;
        public float BossHeadArmor { get; } = 6f;
        public float BossBodyArmor { get; } = 8f;
        public float MinionHeadArmor { get; } = 5f;
        public float MinionBodyArmor { get; } = 6f;
        public bool EnableBossGlow { get; } = true;
        public string Language { get; } = "zh-CN";
        public int BossCount { get; } = 5; // New config for BOSS count

        // Singleton instance (Lazy load unnecessary now but kept for compatibility)
        private static readonly ModConfig _instance = new ModConfig();
        public static ModConfig Instance => _instance;

        // Methods are no-ops or removed
        public static void Load() { }
        public static void Save() { }
    }
}
