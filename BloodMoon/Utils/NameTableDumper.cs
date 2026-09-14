using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Duckov.Utilities;
using UnityEngine;

namespace BloodMoon.Utils
{
    /// <summary>
    /// 名称对照表导出（英文资产名 ↔ 中文显示名）。
    ///
    /// 背景：游戏里的角色/物品显示名来自本地化表
    ///   <c>StreamingAssets/Localization/ChineseSimplified.csv</c>（列：key,value,version,sheet），
    ///   而 <c>CharacterRandomPreset</c> 通过 <c>[LocalizationKey("Characters")] nameKey</c> +
    ///   <c>nameKey.ToPlainText()</c> 取中文名（CharacterRandomPreset.cs:24-25,229-231）。
    ///   **preset 的资产名与 nameKey 没有可推导的命名规律**（实测 38 个 preset 里只有 3 个能按
    ///   `EnemyPreset_X → Cname_X` 猜中），所以必须在运行时读一次真实映射。
    ///
    /// 本类在游戏运行时把两张表导出到 <c>&lt;游戏目录&gt;/UserData/BloodMoonAI/</c>：
    ///   · <c>name_table_presets.csv</c> —— 所有角色 preset：资产名 / nameKey / 中文名 / 是否 Boss /
    ///     阵营 / 经验 / 是否被 BloodMoon 过滤
    ///   · <c>name_table_items.csv</c> —— 所有物品：TypeID / 资产名 / 中文名 / 品质 / 武器类别 /
    ///     是否允许进 AI 武器池 / 被过滤的原因
    ///
    /// 有了它，就能用"游戏里看到的中文名"来配置过滤（见 <c>Config.BossPresetBlocklist</c>）。
    /// </summary>
    public static class NameTableDumper
    {
        private static bool _dumped;

        public static void DumpOnce()
        {
            if (_dumped) return;
            var cfg = ModConfig.Instance;
            if (cfg != null && !cfg.DumpNameTables) return;

            try
            {
                string dir = GetOutputDir();
                DumpPresets(dir);
                DumpItems(dir);
                _dumped = true;
            }
            catch (Exception e)
            {
                Logger.Warning($"[NameTableDumper] dump failed: {e.Message}");
            }
        }

        private static string GetOutputDir()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(root, "UserData", "BloodMoonAI");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DumpPresets(string dir)
        {
            var presets = GameplayDataSettings.CharacterRandomPresetData?.presets;
            if (presets == null)
            {
                Logger.Warning("[NameTableDumper] preset table not ready");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("assetName,nameKey,chineseName,isBoss,team,exp,health,showHealthBar,iconKind,bloodMoonBlocked");

            int rows = 0, blocked = 0;
            for (int i = 0; i < presets.Count; i++)
            {
                var p = presets[i];
                if (p == null) continue;

                string assetName = p.name ?? string.Empty;
                string nameKey = p.nameKey ?? string.Empty;
                string chinese = SafeDisplayName(p);
                // 修复（2026-09-10 第 9 轮后核对导出表时发现）：原实现直接用 == 比较 Sprite 引用，
                // 当 icon 是 null（CharacterIconTypes.none）而 UIStyle 图标也为 null 时，
                // null == null 成立 → 三列全被标成 True，导出的表完全不可信。
                // 现在显式判空，并把"没有图标"标成 none。
                string iconKind = "?";
                try
                {
                    var icon = p.GetCharacterIcon();
                    if (icon == null) iconKind = "none";
                    else if (icon == GameplayDataSettings.UIStyle.BossCharacterIcon) iconKind = "boss";
                    else if (icon == GameplayDataSettings.UIStyle.MerchantCharacterIcon) iconKind = "merchant";
                    else if (icon == GameplayDataSettings.UIStyle.PetCharacterIcon) iconKind = "pet";
                    else if (icon == GameplayDataSettings.UIStyle.PmcCharacterIcon) iconKind = "pmc";
                    else if (icon == GameplayDataSettings.UIStyle.EleteCharacterIcon) iconKind = "elete";
                    else iconKind = "other";
                }
                catch { iconKind = "err"; }

                bool isBlocked = BossPresetFilterBridge.IsBlocked(p);
                if (isBlocked) blocked++;

                sb.Append(Escape(assetName)).Append(',')
                  .Append(Escape(nameKey)).Append(',')
                  .Append(Escape(chinese)).Append(',')
                  .Append(p.isBoss).Append(',')
                  .Append(p.team).Append(',')
                  .Append(p.exp).Append(',')
                  .Append(p.health).Append(',')
                  .Append(p.showHealthBar).Append(',')
                  .Append(iconKind).Append(',')
                  .Append(isBlocked)
                  .AppendLine();
                rows++;
            }

            string path = Path.Combine(dir, "name_table_presets.csv");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Logger.Log($"[NameTableDumper] presets -> {path} ({rows} rows, {blocked} blocked)");
        }

        private static void DumpItems(string dir)
        {
            var collection = ItemStatsSystem.ItemAssetsCollection.Instance;
            if (collection?.entries == null)
            {
                Logger.Warning("[NameTableDumper] item table not ready");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("typeID,assetName,chineseName,quality,maxStack,kind,poolAllowed,blockReason");

            int rows = 0;
            for (int i = 0; i < collection.entries.Count; i++)
            {
                var entry = collection.entries[i];
                if (entry?.prefab == null) continue;

                var item = entry.prefab;
                string assetName = item.name ?? string.Empty;
                string chinese = SafeDisplayName(item);

                string kind = "other";
                try
                {
                    if (AI.EnhancedWeaponManager.LooksLikeGun(item)) kind = "gun";
                    else if (AI.EnhancedWeaponManager.LooksLikeMelee(item)) kind = "melee";
                    else if (item.GetComponentInChildren<ItemSetting_Skill>(true) != null) kind = "skill";
                }
                catch { }

                string blockReason = AI.WeaponPoolFilter.Classify(assetName) ?? string.Empty;
                if (string.IsNullOrEmpty(blockReason))
                {
                    blockReason = AI.WeaponPoolFilter.Classify(chinese) ?? string.Empty;
                }
                bool allowed = string.IsNullOrEmpty(blockReason);

                sb.Append(entry.typeID).Append(',')
                  .Append(Escape(assetName)).Append(',')
                  .Append(Escape(chinese)).Append(',')
                  .Append(item.Quality).Append(',')
                  .Append(item.MaxStackCount).Append(',')
                  .Append(kind).Append(',')
                  .Append(allowed).Append(',')
                  .Append(Escape(blockReason ?? string.Empty))
                  .AppendLine();
                rows++;
            }

            string path = Path.Combine(dir, "name_table_items.csv");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Logger.Log($"[NameTableDumper] items -> {path} ({rows} rows)");
        }

        private static string SafeDisplayName(CharacterRandomPreset p)
        {
            try { return p.DisplayName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeDisplayName(ItemStatsSystem.Item item)
        {
            try { return item.DisplayName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    /// <summary>
    /// 让 NameTableDumper 能问到"这个 preset 是否被 BloodMoon 过滤"，
    /// 又不必把 BossManager 的内部判断暴露成公共 API（避免循环依赖）。
    /// </summary>
    public static class BossPresetFilterBridge
    {
        /// <summary>由 BossManager 在初始化时注入</summary>
        public static Func<CharacterRandomPreset, bool>? IsBlockedImpl;

        public static bool IsBlocked(CharacterRandomPreset preset)
        {
            try
            {
                var impl = IsBlockedImpl;
                return impl != null && impl(preset);
            }
            catch
            {
                return false;
            }
        }
    }
}
