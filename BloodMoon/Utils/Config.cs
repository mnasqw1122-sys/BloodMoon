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
        /// 血月休眠时间（小时）- 两次血月之间的间隔
        /// </summary>
        public float SleepHours = 160f;
        
        /// <summary>
        /// 血月持续时间（小时）- 血月每次激活的时长
        /// </summary>
        public float ActiveHours = 48f;
        
        // ==========================================
        // Boss设置
        // ==========================================
        
        /// <summary>
        /// Boss数量 - 每次血月生成的Boss数量（1-5）
        /// </summary>
        public int BossCount = 5;
        
        /// <summary>
        /// 每个Boss的随从数量 - 每个Boss带多少个小弟（3-10）
        /// </summary>
        public int BossMinionCount = 5;
        
        /// <summary>
        /// Boss生命值倍数 - 相对于基础Boss的生命值倍率
        /// </summary>
        public float BossHealthMultiplier = 5.0f;
        
        /// <summary>
        /// 随从生命值倍数 - 相对于普通敌人的生命值倍率
        /// </summary>
        public float MinionHealthMultiplier = 2.0f;
        
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
        /// AI攻击欲望倍数 - 数值越高AI越主动攻击
        /// </summary>
        public float AIAggressionMultiplier = 1.0f;
        
        /// <summary>
        /// AI准度倍数 - 数值越高AI射击越准
        /// </summary>
        public float AIAccuracyMultiplier = 1.0f;
        
        /// <summary>
        /// AI反应时间倍数 - 数值越小AI反应越快
        /// </summary>
        public float AIReactionTimeMultiplier = 1.0f;
        
        /// <summary>
        /// AI伤害倍数 - 数值越高AI造成的伤害越大
        /// </summary>
        public float AIDamageMultiplier = 1.0f;
        
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
        /// 红色覆盖层强度 - 血月时屏幕红色效果的强度（0.0-2.0）
        /// </summary>
        public float RedOverlayIntensity = 1.0f;
        
        /// <summary>
        /// 红色覆盖层开关 - 是否启用血月红色屏幕效果
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
        
        /// <summary>
        /// 禁用默认生成器 - 是否禁用游戏默认的敌人生成器
        /// </summary>
        public bool DisableDefaultSpawners = true;
        
        // ==========================================
        // 调试设置
        // ==========================================
        
        /// <summary>
        /// 启用调试日志 - 是否在控制台输出详细日志
        /// </summary>
        public bool EnableDebugLogging = false;
        
        /// <summary>
        /// 启用AI调试视觉 - 是否显示AI调试信息
        /// </summary>
        public bool EnableAIDebugVisuals = false;

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
