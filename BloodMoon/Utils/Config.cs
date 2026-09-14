using System;
using System.IO;
using UnityEngine;

namespace BloodMoon.Utils
{
    [Serializable]
    public class ModConfig
    {
        // ==========================================
        // 时间周期设置
        // ==========================================
        
        /// <summary>
        /// 血月休眠时间（小时）- 两次血月之间的间隔（72h ≈ 72 真实分钟 @60x 流速）
        /// </summary>
        public float SleepHours = 72f;
        
        /// <summary>
        /// 血月持续时间（小时）- 血月每次激活的时长（24h ≈ 24 真实分钟）
        /// </summary>
        public float ActiveHours = 24f;
        
        // ==========================================
        // Boss设置
        // ==========================================
        
        /// <summary>
        /// Boss数量 - 每次血月生成的Boss数量（1-5，默认 2 保持可控）
        /// </summary>
        public int BossCount = 2;
        
        /// <summary>
        /// 每个Boss的随从数量 - 每个Boss带多少个小弟（1-10）
        /// </summary>
        public int BossMinionCount = 3;
        
        /// <summary>
        /// Boss生命值倍数 - 相对于基础Boss的生命值倍率
        /// </summary>
        public float BossHealthMultiplier = 3.0f;
        
        /// <summary>
        /// 随从生命值倍数 - 相对于普通敌人的生命值倍率
        /// </summary>
        public float MinionHealthMultiplier = 1.8f;
        
        /// <summary>
        /// Boss头部护甲 - Boss的头部护甲值
        /// </summary>
        public float BossHeadArmor = 6f;
        
        /// <summary>
        /// Boss身体护甲 - Boss的身体护甲值
        /// </summary>
        public float BossBodyArmor = 8f;
        
        /// <summary>
        /// 随从头部护甲 - 随从的头部护甲值
        /// </summary>
        public float MinionHeadArmor = 5f;
        
        /// <summary>
        /// 随从身体护甲 - 随从的身体护甲值
        /// </summary>
        public float MinionBodyArmor = 6f;
        
        /// <summary>
        /// Boss发光效果 - 是否启用Boss红色发光效果
        /// </summary>
        public bool EnableBossGlow = true;
        
        // ==========================================
        // 战利品设置
        // ==========================================
        
        /// <summary>
        /// Boss战利品最低品质 - Boss掉落物品的最低品质（1-10）
        /// </summary>
        public int BossLootMinQuality = 4;
        
        /// <summary>
        /// Boss战利品最高品质 - Boss掉落物品的最高品质（1-10）
        /// </summary>
        public int BossLootMaxQuality = 10;
        
        /// <summary>
        /// Boss战利品最小数量 - Boss最少掉落几件物品
        /// </summary>
        public int BossLootMinCount = 1;
        
        /// <summary>
        /// Boss战利品最大数量 - Boss最多掉落几件物品
        /// </summary>
        public int BossLootMaxCount = 3;

        // ==========================================
        // AI行为设置
        // ==========================================

        /// <summary>
        /// Boss移动速度倍数 - 数值越高Boss跑得越快
        /// </summary>
        public float BossSpeedMultiplier = 1.35f;
        
        /// <summary>
        /// 随从移动速度倍数 - 数值越高随从跑得越快
        /// </summary>
        public float MinionSpeedMultiplier = 1.2f;
        
        // ==========================================
        // 视觉效果设置
        // ==========================================

        /// <summary>
        /// 红色覆盖层开关 - 是否启用血月红色屏幕效果（雾 + 环境光变红）
        /// </summary>
        public bool EnableRedOverlay = true;
        
        /// <summary>
        /// Boss发光强度 - Boss发光效果的亮度（0.0-5.0）
        /// </summary>
        public float BossGlowIntensity = 4.0f;
        
        /// <summary>
        /// Boss发光范围 - Boss发光效果的范围（米）
        /// </summary>
        public float BossGlowRange = 6.0f;
        
        // ==========================================
        // 通用设置
        // ==========================================
        
        /// <summary>
        /// 语言设置 - 模组显示语言（zh-CN, en-US）
        /// </summary>
        public string Language = "zh-CN";

        // 说明：这里原本还有 4 个 AI 倍率配置项（AIAggressionMultiplier / AIAccuracyMultiplier /
        // AIReactionTimeMultiplier / AIDamageMultiplier），因为从来没有被任何代码读取，
        // 已于 2026-09-10 的清理中删除 —— AI 难度倍率实际由 AdaptiveDifficulty 在运行时
        // 按玩家表现自动计算，玩家侧无需也无法调节。
        
        /// <summary>
        /// 禁用默认生成器 - 是否禁用游戏默认的敌人生成器
        /// </summary>
        public bool DisableDefaultSpawners = true;

        // ==========================================
        // 武器池过滤（2026-09-10 实机日志观察项）
        // ==========================================

        /// <summary>
        /// 武器池过滤总开关 - 关掉后 AI 会重新拿到鱼竿/魔杖/植物枪等"出戏"物品
        /// </summary>
        public bool EnableWeaponPoolFilter = true;

        /// <summary>
        /// 允许动物/植物/魔法/能量/烟花类武器（这类多为非人类单位专用，且常常无法生成弹药）
        /// </summary>
        public bool AllowMonsterWeapons = false;

        /// <summary>
        /// 允许"工具当武器"（鱼竿、铁丝网、陷阱等）
        /// </summary>
        public bool AllowToolWeapons = false;

        /// <summary>
        /// 允许任务专属变体（名字含 Quest 的物品）
        /// </summary>
        public bool AllowQuestWeapons = false;

        /// <summary>
        /// 允许玩家/剧情专属变体（名字含 _Player 的物品）
        /// </summary>
        public bool AllowPlayerVariantWeapons = false;

        /// <summary>
        /// AI 武器最低品质（0 = 不限制）
        /// </summary>
        public int MinWeaponQuality = 0;

        /// <summary>
        /// 修复物品自定义数据的结构错误（Int/Float/Bool 声明但字节缺失）。
        /// 这类数据会让游戏自己打印 "Index was out of range" 并把物品数量读成 0；修复保持语义、不改数值。
        /// </summary>
        public bool RepairItemCustomData = true;

        /// <summary>
        /// 【工具开关，默认关闭】导出名称对照表到 `UserData/BloodMoonAI/`：
        /// `name_table_presets.csv`（角色 preset 的资产名 / nameKey / 中文名 / 是否被过滤）与
        /// `name_table_items.csv`（物品 TypeID / 资产名 / 中文名 / 武器类别 / 是否允许进 AI 武器池）。
        /// 游戏里的名字都来自本地化表，preset 资产名与中文名没有可推导规律，需要时临时打开一次即可。
        /// </summary>
        public bool DumpNameTables = false;

        // ==========================================
        // 受击体修复（2026-09-10 玩家反馈：风暴机器人打不动、子弹穿过）
        // ==========================================

        /// <summary>
        /// 修复被接管敌人的受击体：校正 DamageReceiver 的 Layer、激活被禁用的受击体、
        /// 把大型 Boss 的受击胶囊放大到与模型匹配（只补不削）。
        /// </summary>
        public bool FixBossHitbox = true;

        /// <summary>
        /// 对**所有**敌人（含游戏原生 Boss，不只是血月刷出来的）做一次受击体体检。
        /// </summary>
        public bool FixHitboxForAllEnemies = true;

        /// <summary>
        /// Boss/随从 preset 黑名单（按名字关键字，逗号分隔，忽略大小写）。
        /// 用途：把"脚本化特殊遭遇 / 非战斗"的角色排除在血月随机生成之外。
        /// **可以直接写游戏里显示的中文名**（会同时匹配资产名 `EnemyPreset_XXX`、
        /// 本地化 key `Cname_XXX`、中文显示名三者），中文对照见
        /// `UserData/BloodMoonAI/name_table_presets.csv`。例：口口口口,噗咙噗咙
        /// </summary>
        public string BossPresetBlocklist = "";
        
        // ==========================================
        // 调试设置
        // ==========================================
        
        /// <summary>
        /// 启用调试日志 - 打开后会输出详细日志（AI 决策、落盘自检、preset 过滤明细、武器池拦截等）
        /// </summary>
        public bool EnableDebugLogging = false;

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

        /// <summary>
        /// 初始化配置系统
        /// </summary>
        /// <param name="modDirectory">模组目录路径</param>
        public static void Initialize(string modDirectory)
        {
            _configPath = Path.Combine(modDirectory, "BloodMoonConfig.json");
            Load();
        }

        /// <summary>
        /// 从文件加载配置
        /// </summary>
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

                    // 旧配置文件里没有新增字段：JsonUtility 会保留字段初始值（也就是我们的默认值，
                    // 行为正确），但用户在文件里看不到这些开关。检测到旧 schema 就把完整配置写回一次。
                    if (!json.Contains("EnableWeaponPoolFilter") || !json.Contains("RepairItemCustomData"))
                    {
                        Logger.Log("Configuration schema upgraded: rewriting file with the new options.");
                        Save();
                    }
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

        /// <summary>
        /// 保存配置到文件
        /// </summary>
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
