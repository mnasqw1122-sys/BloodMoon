using System.Collections.Generic;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;

namespace BloodMoon.Utils
{
    /// <summary>
    /// 物品自定义数据（<see cref="CustomData"/>）防御性修复。
    ///
    /// 背景（2026-09-10 实机日志）：Player.log 里出现 141 条
    ///   <c>Error: Index was out of range. Must be non-negative and less than the size of the collection. Parameter name: startIndex</c>
    /// 来源是游戏自己的 <c>CustomData.GetInt()</c>：
    ///   <c>BitConverter.ToInt32(Data, 0)</c> 在 <c>Data.Length &lt; 4</c> 时抛异常，
    ///   被 <c>catch</c> 后打印 <c>"Error: " + ex.Message</c>（CustomData.cs:119-123）。
    /// 触发条件是：某个声明为 <c>Int</c> 的自定义数据，字节数组却是空的。
    ///
    /// 危害不止刷日志：<c>Item.StackCount =&gt; GetInt("Count", 1)</c>（Item.cs:455）在异常路径上返回的是
    /// catch 里的 **0**（而不是默认值 1），也就是"数量为 0 的物品"；击杀结算读 "Exp" 同样拿到 0。
    ///
    /// 这里的修复方式：把"类型声明与实际字节数不符"的条目，按该类型的默认值补齐（Count 补 1，其余补 0）。
    /// 因为游戏读取失败时本来就回落到 0，所以这是**保持语义**的修复，不会改变数值平衡。
    /// </summary>
    public static class ItemDataSanitizer
    {
        private static readonly Dictionary<string, int> _repairedKeys = new Dictionary<string, int>();
        private static int _repairedTotal;
        private static bool _summaryLogged;

        private static bool _prefabRepairDone;

        /// <summary>
        /// 预修复物品资产模板，只跑一次（幂等：再跑也只会发现没有可修的了）。
        /// </summary>
        public static int EnsurePrefabTemplatesRepaired()
        {
            if (_prefabRepairDone) return 0;
            int n = SanitizePrefabTemplates();
            _prefabRepairDone = true;
            return n;
        }

        /// <summary>
        /// 预修复物品资产模板（<c>ItemAssetsCollection.entries[*].prefab</c>）。
        ///
        /// 这一步针对实机日志确认的报错源：物品模板里的 <c>BulletCount</c> 声明为 Int 但字节为空，
        /// 于是**每次游戏读取枪的弹匣状态都会抛一次** <c>Index was out of range</c>（关卡初始化载入
        /// 角色物品时尤其密集）。游戏是在 mod 之前跑的，我们无法拦截它的读取，但可以**先把模板修干净**：
        /// 之后所有 <c>InstantiateAsync</c> 出来的物品（AI 装备、掉落、商店）天然就是合法的。
        ///
        /// 注意：只改内存中的资产对象，不会写回任何文件，重启游戏即还原。
        /// </summary>
        public static int SanitizePrefabTemplates()
        {
            var cfg = ModConfig.Instance;
            if (cfg != null && !cfg.RepairItemCustomData) return 0;

            int repairedItems = 0;
            int repairedEntries = 0;
            try
            {
                var collection = ItemStatsSystem.ItemAssetsCollection.Instance;
                if (collection?.entries == null)
                {
                    Logger.Warning("[ItemDataSanitizer] ItemAssetsCollection not ready, skip prefab repair");
                    return 0;
                }

                for (int i = 0; i < collection.entries.Count; i++)
                {
                    var entry = collection.entries[i];
                    if (entry?.prefab == null) continue;

                    int n = Sanitize(entry.prefab);
                    if (n > 0)
                    {
                        repairedItems++;
                        repairedEntries += n;
                    }
                }
            }
            catch (System.Exception e)
            {
                Logger.Warning($"[ItemDataSanitizer] SanitizePrefabTemplates failed: {e.Message}");
            }

            if (repairedEntries > 0)
            {
                _repairedTotal += repairedEntries;
                Logger.Log($"[ItemDataSanitizer] Pre-repaired {repairedEntries} malformed entries across {repairedItems} item templates");
            }
            // 这里不打印会话汇总：留给第一次物品树修复（关卡初始化）时统一输出，汇总数字更完整
            return repairedEntries;
        }

        /// <summary>单件物品的修复（返回修复条目数）</summary>
        public static int Sanitize(Item? item, bool countVisited = false)
        {
            if (item == null) return 0;
            var cfg = ModConfig.Instance;
            if (cfg != null && !cfg.RepairItemCustomData) return 0;

            int repaired = 0;
            try
            {
                var variables = item.Variables;
                if (variables == null) return 0;

                foreach (var v in variables)
                {
                    if (v == null) continue;
                    if (Repair(v)) repaired++;
                }
            }
            catch (System.Exception e)
            {
                Logger.Warning($"[ItemDataSanitizer] Sanitize failed on {item.name}: {e.Message}");
            }

            if (repaired > 0)
            {
                _repairedTotal += repaired;
                // 逐条修复明细只在调试模式输出（模板预修复一次会刷 120+ 行）；汇总仍打 INFO
                Logger.Debug($"[ItemDataSanitizer] Repaired {repaired} malformed custom-data entr{(repaired == 1 ? "y" : "ies")} on item {item.name}");
            }
            return repaired;
        }

        /// <summary>
        /// 递归修复一整棵物品树（玩家角色物品 / AI 角色物品 / 尸体容器）。
        /// 用于覆盖"从存档载入的旧物品"这一类无法在创建时拦截的来源。
        /// </summary>
        public static int SanitizeTree(Item? root, int maxDepth = 3)
        {
            if (root == null) return 0;
            var cfg = ModConfig.Instance;
            if (cfg != null && !cfg.RepairItemCustomData) return 0;

            int repaired = 0;
            var visited = new HashSet<int>();
            var queue = new Queue<(Item item, int depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (item, depth) = queue.Dequeue();
                if (item == null) continue;
                int id = item.GetInstanceID();
                if (!visited.Add(id)) continue;

                repaired += Sanitize(item);

                if (depth >= maxDepth) continue;

                try
                {
                    var slots = item.Slots;
                    if (slots != null)
                    {
                        foreach (var slot in slots)
                        {
                            if (slot?.Content != null) queue.Enqueue((slot.Content, depth + 1));
                        }
                    }

                    var inv = item.Inventory;
                    if (inv != null)
                    {
                        foreach (var child in inv)
                        {
                            if (child != null) queue.Enqueue((child, depth + 1));
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Logger.Warning($"[ItemDataSanitizer] Traverse failed on {item.name}: {e.Message}");
                }
            }

            DumpSummaryOnce();
            return repaired;
        }

        /// <summary>把一个条目补齐到该类型应有的字节数；返回是否发生了修复</summary>
        private static bool Repair(CustomData v)
        {
            byte[]? raw;
            try
            {
                raw = v.GetRawCopied();
            }
            catch
            {
                return false;
            }

            int required = RequiredBytes(v.DataType);
            if (required <= 0) return false;                       // Raw / String：长度自由
            if (raw != null && raw.Length >= required) return false; // 正常数据

            byte[] fill;
            switch (v.DataType)
            {
                case CustomDataType.Int:
                    // "Count" 的读取默认值是 1（Item.cs:455），补 1 才符合语义；其余补 0
                    int intValue = string.Equals(v.Key, "Count", System.StringComparison.Ordinal) ? 1 : 0;
                    fill = System.BitConverter.GetBytes(intValue);
                    break;
                case CustomDataType.Float:
                    fill = System.BitConverter.GetBytes(0f);
                    break;
                case CustomDataType.Bool:
                    fill = System.BitConverter.GetBytes(false);
                    break;
                default:
                    return false;
            }

            v.SetRaw(fill);

            string key = string.IsNullOrEmpty(v.Key) ? "(null)" : v.Key;
            if (_repairedKeys.TryGetValue(key, out int n)) _repairedKeys[key] = n + 1;
            else _repairedKeys[key] = 1;
            return true;
        }

        private static int RequiredBytes(CustomDataType type)
        {
            switch (type)
            {
                case CustomDataType.Int: return 4;
                case CustomDataType.Float: return 4;
                case CustomDataType.Bool: return 1;
                default: return 0;
            }
        }

        private static void DumpSummaryOnce()
        {
            if (_summaryLogged || _repairedTotal == 0) return;
            _summaryLogged = true;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[ItemDataSanitizer] Session summary: repaired {_repairedTotal} entries. Keys: ");
            bool first = true;
            foreach (var kv in _repairedKeys)
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key).Append('x').Append(kv.Value);
                first = false;
            }
            Logger.Log(sb.ToString());
        }
    }
}
