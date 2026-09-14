using System.Collections.Generic;
using BloodMoon.Utils;
using ItemStatsSystem;

namespace BloodMoon.AI
{
    /// <summary>
    /// AI 武器池过滤（2026-09-10 实机日志观察项）。
    ///
    /// 问题：修复了资产扫描之后，武器池从 43 把涨到 124 把，AI 开始拿到
    ///   <c>FishingRod</c>（鱼竿）、<c>Wand</c>（魔杖）、<c>BarbedWire_NPC_Mud</c>（铁丝网）、
    ///   <c>Plant_SMG_Normal</c>（植物枪）、<c>AnimalWeapon_WolfKing_Ice</c>（动物武器）、
    ///   <c>EGun_Magic_Ring_WhiteHorse</c>、<c>RKT_FireWork_Fire</c>（烟花枪）、
    ///   <c>Energy_CubeGun</c>（方块枪，连子弹都生成不出来）这类"出戏/超模"物品，
    ///   以及带 <c>_Player</c> / <c>Quest</c> 后缀的专属变体。
    ///
    /// 这里按名字做分类黑名单（可用 ModConfig 逐类放开），过滤在**两个时机**生效：
    ///   · 构建武器池时（资产扫描能拿到 prefab 名字，提前排除，省得反复实例化）；
    ///   · 实例化之后（tag 搜索/硬编码 ID 只能拿到 ID，必须看实例名字兜底）。
    /// </summary>
    public static class WeaponPoolFilter
    {
        /// <summary>动物/植物/昆虫等"非人类"武器（含实机观察到的 GiantSwordFish）。
        /// 注意：`roadblock` 会顺带命中 `Helmat_Roadblock`（路障头盔）这类非武器物品 ——
        /// 武器池本来就只收枪/近战，误伤无害，但别把这个关键字用于其它用途。</summary>
        private static readonly string[] MonsterMarkers =
        {
            "animalweapon", "animal_", "monster", "plant_", "insect", "beast", "creature",
            "swordfish", "spider", "roadblock"
        };

        /// <summary>魔法/能量/彩蛋/恶搞类武器（多数连弹药都生成不出来）</summary>
        private static readonly string[] MagicMarkers =
        {
            "egun_", "magic", "wand", "wish", "energy_", "cubegun", "stormboss", "firework", "firecracker",
            "poop", "coinblade"
        };

        /// <summary>场景固定武器/敌人专用枪（实机观察到 EnemyGun_RoadBlock_Rifle、EnemyGun_Spider_Ring）。
        /// 单独成类，方便玩家只放开这一类而不放开全部"怪物武器"。</summary>
        private static readonly string[] EnemyGunMarkers = { "enemygun" };

        /// <summary>命名 Boss 的专属武器（实机观察到 SHT_SnowBoss_Ice 被发给普通随从）</summary>
        private static readonly string[] NamedBossMarkers = { "snowboss", "stormboss", "bossgun" };

        /// <summary>模板/占位/开发用物品（实机观察到 1_Rifle-A_template）</summary>
        private static readonly string[] TemplateMarkers = { "_template", "template_", "_test", "_dev" };

        /// <summary>工具/杂物被当作武器（鱼竿、铁丝网、陷阱等）。
        /// 注意：不要用 "net_" 之类的短标记，会误伤 "Bayonet_xxx"（刺刀是正经近战武器）。</summary>
        private static readonly string[] ToolMarkers =
        {
            "fishingrod", "fishing", "barbedwire", "barbed", "trap_"
        };

        /// <summary>任务专属变体</summary>
        private static readonly string[] QuestMarkers = { "quest" };

        /// <summary>玩家/剧情专属变体</summary>
        private static readonly string[] PlayerVariantMarkers = { "_player", "player_" };

        /// <summary>被过滤掉的分类统计（用于一次性诊断日志）</summary>
        private static readonly Dictionary<string, int> _blockedByCategory = new Dictionary<string, int>();
        private static bool _blockedLogDumped;

        /// <summary>名字是否可以进 AI 武器池</summary>
        public static bool IsAllowedName(string? name)
        {
            return string.IsNullOrEmpty(Classify(name));
        }

        /// <summary>
        /// 返回被拦截的分类名（"monster"/"magic"/"tool"/"quest"/"playerVariant"/"enemyGun"/"namedBoss"/"template"/"quality"），
        /// 允许则返回 null。中文显示名同样会参与匹配（配置里可以直接写中文关键字）。
        /// </summary>
        public static string? Classify(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var cfg = ModConfig.Instance;
            if (cfg == null || !cfg.EnableWeaponPoolFilter) return null;

            string lower = name!.ToLowerInvariant();

            if (!cfg.AllowMonsterWeapons && Matches(lower, EnemyGunMarkers)) return Record("enemyGun");
            if (!cfg.AllowMonsterWeapons && Matches(lower, NamedBossMarkers)) return Record("namedBoss");
            if (!cfg.AllowMonsterWeapons && Matches(lower, TemplateMarkers)) return Record("template");
            if (!cfg.AllowMonsterWeapons && Matches(lower, MonsterMarkers)) return Record("monster");
            if (!cfg.AllowMonsterWeapons && Matches(lower, MagicMarkers)) return Record("magic");
            if (!cfg.AllowToolWeapons && Matches(lower, ToolMarkers)) return Record("tool");
            if (!cfg.AllowQuestWeapons && Matches(lower, QuestMarkers)) return Record("quest");
            if (!cfg.AllowPlayerVariantWeapons && Matches(lower, PlayerVariantMarkers)) return Record("playerVariant");

            return null;
        }

        /// <summary>资产 prefab 是否可以进武器池</summary>
        public static bool IsAllowedPrefab(Item? prefab)
        {
            if (prefab == null) return false;
            if (!IsAllowedName(prefab.name)) return false;
            if (!IsAllowedName(SafeDisplayName(prefab))) return false;   // 中文显示名也参与过滤

            var cfg = ModConfig.Instance;
            if (cfg != null && cfg.MinWeaponQuality > 0 && prefab.Quality < cfg.MinWeaponQuality)
            {
                Record("quality");
                return false;
            }
            return true;
        }

        /// <summary>实例化出来的物品是否可以交给 AI</summary>
        public static bool IsAllowedItem(Item? item)
        {
            if (item == null) return false;
            if (!IsAllowedName(item.name)) return false;
            if (!IsAllowedName(SafeDisplayName(item))) return false;     // 中文显示名也参与过滤

            var cfg = ModConfig.Instance;
            if (cfg != null && cfg.MinWeaponQuality > 0 && item.Quality < cfg.MinWeaponQuality)
            {
                Record("quality");
                return false;
            }
            return true;
        }

        /// <summary>取中文显示名（本地化未就绪时返回空串，不影响判定）</summary>
        private static string? SafeDisplayName(Item item)
        {
            try { return item.DisplayName; }
            catch { return null; }
        }

        private static bool Matches(string lowerName, string[] markers)
        {
            for (int i = 0; i < markers.Length; i++)
            {
                if (lowerName.Contains(markers[i])) return true;
            }
            return false;
        }

        /// <summary>记录一次拦截并返回分类名（供 Classify 使用）</summary>
        private static string Record(string category)
        {
            if (_blockedByCategory.TryGetValue(category, out int n)) _blockedByCategory[category] = n + 1;
            else _blockedByCategory[category] = 1;
            return category;
        }

        /// <summary>把过滤统计打一次日志（每个会话一次），便于确认过滤真的在生效</summary>
        public static void DumpBlockedSummaryOnce()
        {
            if (_blockedLogDumped) return;
            if (_blockedByCategory.Count == 0) return;
            _blockedLogDumped = true;

            var sb = new System.Text.StringBuilder("[WeaponPoolFilter] Blocked from AI weapon pool: ");
            bool first = true;
            foreach (var kv in _blockedByCategory)
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
                first = false;
            }
            Logger.Log(sb.ToString());
        }
    }
}
