using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BloodMoon.Utils
{
    [Serializable]
    public class ModConfig
    {
        public float SleepHours = 160f;
        public float ActiveHours = 24f;
        public float BossHealthMultiplier = 4.0f;
        public float MinionHealthMultiplier = 2.0f;
        public int BossMinionCount = 3;
        // public int MaxMinionsPerMap = 15;
        public float BossDefense = 10f;
        public float MinionDefense = 8f;
        public bool EnableBossGlow = true;
        public string Language = "zh-CN"; // "en-US"

        private static ModConfig? _instance;
        public static ModConfig Instance
        {
            get
            {
                if (_instance == null) Load();
                return _instance!;
            }
        }

        public static void Load()
        {
            // Strategy 1: Relative to Data Path (Most reliable for Unity Games)
            string root = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(root, "UserData", "BloodMoon", "config.json");
            
            // Strategy 2: Current Directory (Fallback for some launchers)
            if (!File.Exists(path))
            {
                string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "UserData", "BloodMoon", "config.json");
                if (File.Exists(cwdPath))
                {
                    path = cwdPath;
                    Debug.Log($"[BloodMoon] Found config in CWD: {path}");
                }
            }

            Debug.Log($"[BloodMoon] Attempting to load config from: {path}");

            if (File.Exists(path))
            {
                try
                {
                    // Let .NET auto-detect encoding for reading to be safe with ANSI/UTF-8
                    string json = File.ReadAllText(path);
                    Debug.Log($"[BloodMoon] Read JSON content (len={json.Length}): {json}"); 
                    
                    _instance = JsonUtility.FromJson<ModConfig>(json);
                    
                    if (_instance == null) 
                    {
                        Debug.LogError("[BloodMoon] JsonUtility returned null! JSON might be invalid.");
                        _instance = new ModConfig();
                    }
                    else
                    {
                        Debug.Log($"[BloodMoon] Config loaded. ActiveHours: {_instance.ActiveHours}, BossHP: {_instance.BossHealthMultiplier}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BloodMoon] Failed to load config: {e}");
                    // Do not overwrite the file if it's broken, just use defaults in memory
                    _instance = new ModConfig();
                }
            }
            else
            {
                Debug.Log($"[BloodMoon] Config not found at {path}, creating default.");
                _instance = new ModConfig();
                // Ensure directory exists before saving
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Save();
            }
        }

        public static void Save()
        {
            if (_instance == null) return;
            
            // Always save to the DataPath location to standardise
            string root = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(root, "UserData", "BloodMoon");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            
            // Use UTF8 for writing new files
            File.WriteAllText(path, JsonUtility.ToJson(_instance, true), Encoding.UTF8);
            Debug.Log($"[BloodMoon] Config saved to {path}");
        }
    }
}
