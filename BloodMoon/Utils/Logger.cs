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
        private static System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private static StreamWriter? _writer;
        private static float _lastFlushTime = -1f;
        private const float FlushInterval = 1f;

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        /// <param name="modDirectory">模组目录路径</param>
        public static void Initialize(string modDirectory)
        {
            ModDirectory = modDirectory;
            _logPath = Path.Combine(modDirectory, "BloodMoon.log");
            _initialized = true;
            
            try
            {
                _writer = new StreamWriter(_logPath, false);
                _writer.WriteLine($"[BloodMoon] Log Started at {DateTime.Now}");
                _writer.Flush();
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
        /// 刷新日志缓冲区到磁盘
        /// </summary>
        public static void Flush()
        {
            try
            {
                while (_logQueue.TryDequeue(out var msg))
                {
                    _writer?.WriteLine(msg);
                }
                _writer?.Flush();
            }
            catch { }
        }

        /// <summary>
        /// 将日志消息写入文件
        /// </summary>
        /// <param name="message">要写入的消息</param>
        private static void WriteToFile(string message)
        {
            if (!_initialized) return;
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logQueue.Enqueue(line);
            // 批量落盘：避免每条日志同步磁盘 IO（错误风暴时拖慢游戏）
            if (Time.realtimeSinceStartup - _lastFlushTime >= FlushInterval)
            {
                Flush();
            }
        }

        /// <summary>
        /// 关闭日志系统
        /// </summary>
        public static void Shutdown()
        {
            Flush();
            _writer?.Close();
            _writer = null;
            _initialized = false;
        }
    }
}
