using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace BloodMoon.Utils
{
    public static class ErrorHandler
    {
        /// <summary>
        /// 安全执行同步操作，捕获并记录异常
        /// </summary>
        /// <param name="action">要执行的操作</param>
        /// <param name="context">上下文描述，用于日志</param>
        public static void SafeExecute(Action action, string context = "")
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Error($"[ErrorHandler] {context} failed: {ex}");
                
                // 对于特定类型的异常，可以添加额外的处理逻辑
                if (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Index error in {context}. This may indicate a bug in array/list access.");
                }
                else if (ex is NullReferenceException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Null reference in {context}. Check object initialization.");
                }
            }
        }

        /// <summary>
        /// 安全执行异步操作，捕获并记录异常
        /// </summary>
        /// <param name="asyncAction">要执行的异步操作</param>
        /// <param name="context">上下文描述，用于日志</param>
        public static async UniTask SafeExecuteAsync(Func<UniTask> asyncAction, string context = "")
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                Logger.Error($"[ErrorHandler] {context} failed: {ex}");
                
                // 对于特定类型的异常，可以添加额外的处理逻辑
                if (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Index error in {context}. This may indicate a bug in array/list access.");
                }
                else if (ex is NullReferenceException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Null reference in {context}. Check object initialization.");
                }
                else if (ex is TimeoutException)
                {
                    Logger.Error($"[ErrorHandler] Timeout in {context}. Operation took too long.");
                }
            }
        }

        /// <summary>
        /// 安全执行异步操作并返回结果，捕获并记录异常
        /// </summary>
        /// <param name="asyncAction">要执行的异步操作</param>
        /// <param name="context">上下文描述，用于日志</param>
        /// <param name="defaultValue">默认值，当操作失败时返回</param>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <returns>操作结果或默认值</returns>
        public static async UniTask<T> SafeExecuteAsync<T>(Func<UniTask<T>> asyncAction, string context = "", T defaultValue = default!)
        {
            try
            {
                return await asyncAction();
            }
            catch (Exception ex)
            {
                Logger.Error($"[ErrorHandler] {context} failed: {ex}");
                
                // 对于特定类型的异常，可以添加额外的处理逻辑
                if (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Index error in {context}. This may indicate a bug in array/list access.");
                }
                else if (ex is NullReferenceException)
                {
                    Logger.Error($"[ErrorHandler] CRITICAL: Null reference in {context}. Check object initialization.");
                }
                
                return defaultValue;
            }
        }

        /// <summary>
        /// 验证参数不为空，如果为空则记录错误
        /// </summary>
        /// <param name="obj">要验证的对象</param>
        /// <param name="paramName">参数名</param>
        /// <param name="context">上下文描述</param>
        /// <returns>如果对象不为空返回true，否则返回false</returns>
        public static bool ValidateNotNull(object? obj, string paramName, string context = "")
        {
            if (obj == null)
            {
                Logger.Error($"[ErrorHandler] {context}: Parameter '{paramName}' is null");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 验证索引在范围内，如果不在范围内则记录错误
        /// </summary>
        /// <param name="index">要验证的索引</param>
        /// <param name="count">集合大小</param>
        /// <param name="arrayName">集合名称</param>
        /// <param name="context">上下文描述</param>
        /// <returns>如果索引有效返回true，否则返回false</returns>
        public static bool ValidateIndex(int index, int count, string arrayName, string context = "")
        {
            if (index < 0 || index >= count)
            {
                Logger.Error($"[ErrorHandler] {context}: Index {index} out of range for {arrayName} (count: {count})");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 验证集合不为空，如果为空则记录错误
        /// </summary>
        /// <param name="collection">要验证的集合</param>
        /// <param name="collectionName">集合名称</param>
        /// <param name="context">上下文描述</param>
        /// <typeparam name="T">集合元素类型</typeparam>
        /// <returns>如果集合有效返回true，否则返回false</returns>
        public static bool ValidateCollectionNotEmpty<T>(System.Collections.Generic.ICollection<T>? collection, string collectionName, string context = "")
        {
            if (collection == null)
            {
                Logger.Error($"[ErrorHandler] {context}: Collection '{collectionName}' is null");
                return false;
            }
            
            if (collection.Count == 0)
            {
                Logger.Error($"[ErrorHandler] {context}: Collection '{collectionName}' is empty");
                return false;
            }
            
            return true;
        }
    }
}