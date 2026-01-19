using System;
using System.IO;
using UnityEngine;

namespace BloodMoon.Utils
{
    public static class Logger
    {
        public static string ModDirectory { get; private set; } = string.Empty;
        private static string _logPath = string.Empty;
        private static bool _initialized;

        public static void Initialize(string modDirectory)
        {
            ModDirectory = modDirectory;
            _logPath = Path.Combine(modDirectory, "BloodMoon.log");
            _initialized = true;
            
            // 启动时重置日志文件
            try
            {
                File.WriteAllText(_logPath, $"[BloodMoon] Log Started at {DateTime.Now}\n");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[BloodMoon] Failed to create log file: {ex.Message}");
            }
        }

        public static void Log(string message)
        {
            UnityEngine.Debug.Log($"[BloodMoon] {message}");
            WriteToFile($"[INFO] {message}");
        }

        public static void Warning(string message)
        {
            UnityEngine.Debug.LogWarning($"[BloodMoon] {message}");
            WriteToFile($"[WARN] {message}");
        }

        public static void Error(string message)
        {
            UnityEngine.Debug.LogError($"[BloodMoon] {message}");
            WriteToFile($"[ERROR] {message}");
        }

        public static void Debug(string message)
        {
#if DEBUG
            UnityEngine.Debug.Log($"[BloodMoon DEBUG] {message}");
            WriteToFile($"[DEBUG] {message}");
#endif
        }

        private static void WriteToFile(string message)
        {
            if (!_initialized) return;
            try
            {
                using (StreamWriter writer = File.AppendText(_logPath))
                {
                    writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                }
            }
            catch
            {
                // 忽略文件写入错误以避免无限循环
            }
        }
    }
}
