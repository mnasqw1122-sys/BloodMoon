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

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        /// <param name="modDirectory">模组目录路径</param>
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

        /// <summary>
        /// 记录信息级别日志
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Log(string message)
        {
            UnityEngine.Debug.Log($"[BloodMoon] {message}");
            WriteToFile($"[INFO] {message}");
        }

        /// <summary>
        /// 记录警告级别日志
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Warning(string message)
        {
            UnityEngine.Debug.LogWarning($"[BloodMoon] {message}");
            WriteToFile($"[WARN] {message}");
        }

        /// <summary>
        /// 记录错误级别日志
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Error(string message)
        {
            UnityEngine.Debug.LogError($"[BloodMoon] {message}");
            WriteToFile($"[ERROR] {message}");
        }

        /// <summary>
        /// 记录调试级别日志（仅在DEBUG模式下）
        /// </summary>
        /// <param name="message">日志消息</param>
        public static void Debug(string message)
        {
#if DEBUG
            UnityEngine.Debug.Log($"[BloodMoon DEBUG] {message}");
            WriteToFile($"[DEBUG] {message}");
#endif
        }

        /// <summary>
        /// 将日志消息写入文件
        /// </summary>
        /// <param name="message">要写入的消息</param>
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
