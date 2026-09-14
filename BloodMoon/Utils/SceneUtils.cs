using UnityEngine;
using UnityEngine.SceneManagement;

namespace BloodMoon.Utils
{
    /// <summary>
    /// 场景判定工具。
    ///
    /// 背景（2026-09-10 实机日志暴露）：
    ///   `LevelConfig.isRaidMap` 的字段默认值就是 **true**、`isBaseLevel` 默认 **false**
    ///   （LevelConfig.cs:13,16），而 `LevelConfig.Instance` 是静态实例 —— 在基地/加载场景
    ///   初始化时它可能仍指向上一张 raid 图的 LevelConfig，或该场景根本没有 LevelConfig。
    ///   于是 `LevelManager.IsRaidMap && !LevelManager.IsBaseLevel` 这种判断会在
    ///   **基地（Base）** 与 **加载场景（LoadingScreen_*）** 上返回 true：
    ///     · 模组把基地当成 raid，给基地也建了一份 AI 记忆文件（map_Base.json）；
    ///     · 血月期间红色覆盖层/刷怪器禁用等逻辑也可能在基地生效。
    ///
    /// 这里的判定 = 关卡报告自己是 raid + 关卡名不是已知的非战斗场景。
    /// </summary>
    public static class SceneUtils
    {
        /// <summary>已知的非 raid 场景名关键字（小写匹配）</summary>
        private static readonly string[] NonRaidSceneMarkers =
        {
            "base", "loading", "startup", "menu", "lobby", "title"
        };

        /// <summary>
        /// 当前是否处于 raid（战斗）地图。
        /// </summary>
        public static bool IsRaidScene()
        {
            return IsRaidScene(out _);
        }

        /// <summary>
        /// 当前是否处于 raid（战斗）地图，并返回当前关卡场景名。
        /// </summary>
        public static bool IsRaidScene(out string sceneName)
        {
            sceneName = string.Empty;

            var lm = LevelManager.Instance;
            if (lm == null) return false;
            if (lm.IsBaseLevel) return false;

            sceneName = GetLevelSceneName();
            if (string.IsNullOrEmpty(sceneName)) return false;

            if (IsNonRaidSceneName(sceneName)) return false;

            return lm.IsRaidMap;
        }

        /// <summary>当前关卡场景名（优先取 LevelManager 所在的场景名）</summary>
        public static string GetLevelSceneName()
        {
            try
            {
                if (LevelManager.Instance != null)
                {
                    var info = LevelManager.GetCurrentLevelInfo();
                    if (!string.IsNullOrEmpty(info.sceneName)) return info.sceneName;
                }
            }
            catch (System.Exception)
            {
                // 忽略，退回活动场景名
            }

            var scene = SceneManager.GetActiveScene();
            return string.IsNullOrEmpty(scene.name) ? string.Empty : scene.name;
        }

        /// <summary>场景名是否属于基地/加载/菜单等非战斗场景</summary>
        public static bool IsNonRaidSceneName(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            string lower = sceneName.ToLowerInvariant();
            for (int i = 0; i < NonRaidSceneMarkers.Length; i++)
            {
                if (lower.Contains(NonRaidSceneMarkers[i])) return true;
            }
            return false;
        }
    }
}
