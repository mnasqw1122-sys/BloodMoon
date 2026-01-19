using System;
using System.IO;
using UnityEngine;

namespace BloodMoon.Utils
{
    [Serializable]
    public class ModConfig
    {
        // JsonUtility序列化的公共字段
        public float SleepHours = 160f;
        public float ActiveHours = 48f;
        
        // Boss设置
        public int BossCount = 5;
        public int BossMinionCount = 5;
        public float BossHealthMultiplier = 5.0f;
        public float MinionHealthMultiplier = 2.0f;
        public float BossHeadArmor = 6f;
        public float BossBodyArmor = 8f;
        public float MinionHeadArmor = 5f;
        public float MinionBodyArmor = 6f;
        public bool EnableBossGlow = true;
        
        // 通用设置
        public string Language = "zh-CN";

        // 静态单例
        private static ModConfig _instance = null!;
        public static ModConfig Instance
        {
            get
            {
                if (_instance == null) _instance = new ModConfig();
                return _instance;
            }
        }

        private static string _configPath = string.Empty;

        public static void Initialize(string modDirectory)
        {
            _configPath = Path.Combine(modDirectory, "BloodMoonConfig.json");
            Load();
        }

        public static void Load()
        {
            if (string.IsNullOrEmpty(_configPath)) return;

            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    _instance = JsonUtility.FromJson<ModConfig>(json);
                    if (_instance == null) _instance = new ModConfig();
                    Logger.Log("Configuration loaded successfully.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load config: {ex.Message}");
                    _instance = new ModConfig(); // 回退到默认值
                }
            }
            else
            {
                Logger.Log("Configuration file not found. Creating default.");
                _instance = new ModConfig();
                Save();
            }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_configPath) || _instance == null) return;

            try
            {
                string json = JsonUtility.ToJson(_instance, true);
                File.WriteAllText(_configPath, json);
                Logger.Log("Configuration saved.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save config: {ex.Message}");
            }
        }
    }
}
