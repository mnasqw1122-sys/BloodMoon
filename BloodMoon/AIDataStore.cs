using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov;
using Duckov.Utilities;
using System.IO;
using BloodMoon.Utils;
using Logger = BloodMoon.Utils.Logger;   // 消歧：UnityEngine 也有一个 Logger 类型

namespace BloodMoon
{
    // ============================================================================
    // 结构体必须放在命名空间顶层：Unity 的 JsonUtility **不会序列化 List<自定义结构体>**
    // （把结构体嵌套在类里也一样），实机自检日志 mapDangerSerialized=False 是证据。
    // 因此 DTO 里用平行列表打包（见 MapSaveData）。
    // ============================================================================

    /// <summary>危险事件（危险热力图的一个采样点）</summary>
    [Serializable]
    public struct DangerEvent
    {
        public Vector3 pos;
        public float time;
        public float weight;
    }


    /// <summary>
    /// AI数据存储类，负责存储和加载AI的战术数据、危险记忆等
    /// </summary>
    [Serializable]
    public class AIDataStore
    {

        /// <summary>
        /// 地图特定数据（记忆/危险）- 每个地图单独。
        /// 注意：Unity 的 JsonUtility **不会序列化 `List&lt;自定义结构体&gt;`**，所以危险事件用
        /// 三个平行列表（`dangerPos`/`dangerTime`/`dangerWeight`）打包，而不是 `List&lt;DangerEvent&gt;`。
        /// </summary>
        [Serializable]
        private class MapSaveData
        {
            public List<Vector3> stuckSpots = new List<Vector3>();
            public Vector3 lastKnownPlayerPos;
            public int deathCount;
            // 修复 P0-2：危险热力图与"最后见到玩家"必须落盘，否则每次存读档都被清零，
            // 而它们正是侧翼选点(Action_Flank)/掩体评分(TryGetKnownCover)的关键输入。
            public List<Vector3> dangerPos = new List<Vector3>();
            public List<float> dangerTime = new List<float>();
            public List<float> dangerWeight = new List<float>();
            public float lastSeenTime;
            public float lastDeathTime;

            /// <summary>
            /// 从存储对象中读取地图数据
            /// </summary>
            /// <param name="s">数据存储对象</param>
            public void FromStore(AIDataStore s)
            {
                stuckSpots = s.StuckSpots;
                lastKnownPlayerPos = s.LastKnownPlayerPos;
                deathCount = s.DeathCount;
                lastSeenTime = s.LastSeenTime;
                lastDeathTime = s.LastDeathTime;

                dangerPos.Clear(); dangerTime.Clear(); dangerWeight.Clear();
                var events = s.DangerEvents;
                if (events != null)
                {
                    for (int i = 0; i < events.Count; i++)
                    {
                        dangerPos.Add(events[i].pos);
                        dangerTime.Add(events[i].time);
                        dangerWeight.Add(events[i].weight);
                    }
                }
            }

            /// <summary>
            /// 将地图数据写入存储对象
            /// </summary>
            /// <param name="s">数据存储对象</param>
            public void ToStore(AIDataStore s)
            {
                if (stuckSpots != null) s.StuckSpots = stuckSpots;
                s.LastKnownPlayerPos = lastKnownPlayerPos;
                s.DeathCount = deathCount;
                s.LastSeenTime = lastSeenTime;
                s.LastDeathTime = lastDeathTime;

                var events = new List<DangerEvent>();
                if (dangerPos != null)
                {
                    for (int i = 0; i < dangerPos.Count; i++)
                    {
                        events.Add(new DangerEvent
                        {
                            pos = dangerPos[i],
                            time = (dangerTime != null && i < dangerTime.Count) ? dangerTime[i] : 0f,
                            weight = (dangerWeight != null && i < dangerWeight.Count) ? dangerWeight[i] : 1f
                        });
                    }
                }
                s.DangerEvents = events;
            }
        }

        private const string FolderName = "BloodMoonAI";

        // 落盘去抖状态（P1-13）
        private bool _dirty;
        private float _lastWriteRealtime = -999f;
        /// <summary>当前内存里的地图记忆属于哪个文件（切图时必须写回旧文件，P0-7）</summary>
        private string _loadedMapFileName = string.Empty;
        /// <summary>当前内存里的地图记忆是否属于 raid 地图（基地/加载场景不记录）</summary>
        private bool _mapIsRaid;

        public Vector3 LastKnownPlayerPos;
        public float LastSeenTime;
        public List<Vector3> StuckSpots = new List<Vector3>();

        /// <summary>
        /// 标记卡住的位置，避免AI再次卡住
        /// </summary>
        /// <param name="pos">卡住的位置</param>
        public void MarkStuckSpot(Vector3 pos)
        {
            // 集群检查
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if ((StuckSpots[i] - pos).sqrMagnitude < 4.0f) return;
            }
            StuckSpots.Add(pos);
            if (StuckSpots.Count > 128) StuckSpots.RemoveAt(0);
        }

        /// <summary>
        /// 检查位置是否是曾经卡住的位置（供 AI 绕开已知卡点）
        /// </summary>
        /// <param name="pos">要检查的位置</param>
        /// <param name="threshold">距离阈值</param>
        /// <returns>是否是卡住的位置</returns>
        public bool IsStuckSpot(Vector3 pos, float threshold)
        {
            float sqrThresh = threshold * threshold;
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if ((StuckSpots[i] - pos).sqrMagnitude < sqrThresh) return true;
            }
            return false;
        }

        // P2 清理（2026-09-10）：删除了以下**写入端与读取端都没有调用者**的整条链路 ——
        //   · 玩家伏击点：MarkPlayerAmbush / IsPlayerAmbushSpot / PlayerAmbushSpots / 存档字段
        //   · 玩家速度 EMA：RecordPlayerSpeed / GetAverageSpeed / _avgPlayerSpeed / 存档字段
        //   · 玩家装填计数：RegisterPlayerReload / GetAggressionBoost / reloadCount
        //   · 策略权重：GetWeight / ApplyReward / DecayWeights / RecenterWeights / StrategyWeights
        //   · 队长编队偏好：GetLeaderPref / UpdateLeaderPref / SetLeaderPrefBaseline / LeaderPrefs
        //   · 进攻路线统计：RecordApproachOutcome / GetApproachWeight / ApproachStats
        //   · 危险批量查询：ComputeHeatBatch
        // 连带移除了整个"全局数据"层（GlobalSaveData / global_tactics.json）——
        // 它承载的正是上面这些没人用的数据。地图记忆层保持不变。

        public List<DangerEvent> DangerEvents = new List<DangerEvent>();
        public int DeathCount;
        public float LastDeathTime;

        /// <summary>
        /// 标记危险位置
        /// </summary>
        /// <param name="pos">危险位置</param>
        public void MarkDanger(Vector3 pos)
        {
            DangerEvents.Add(new DangerEvent { pos = pos, time = Time.time, weight = 1f });
            if (DangerEvents.Count > 64) DangerEvents.RemoveAt(0);
        }

        public float GetHeatAt(Vector3 pos, float now, float radius)
        {
            float heat = 0f;
            float tau = 30f;
            float sigma = Mathf.Max(1f, radius * 0.5f);
            for (int i = 0; i < DangerEvents.Count; i++)
            {
                var e = DangerEvents[i];
                float dt = Mathf.Max(0f, now - e.time);
                float decay = Mathf.Exp(-dt / tau);
                float d = Vector3.Distance(pos, e.pos);
                float spatial = Mathf.Exp(-(d * d) / (2f * sigma * sigma));
                heat += e.weight * decay * spatial;
            }
            return heat;
        }

        public void DecayAndPrune(float now, float maxAgeSec)
        {
            for (int i = DangerEvents.Count - 1; i >= 0; i--)
            {
                if (now - DangerEvents[i].time > maxAgeSec) DangerEvents.RemoveAt(i);
            }
            if (DangerEvents.Count > 64) DangerEvents.RemoveRange(0, DangerEvents.Count - 64);
        }

        private string GetSaveDirectory()
        {
            // 修复（2026-09-10 发布前检查）：原来无条件用 <游戏根>\UserData\<FolderName>，
            // 但游戏自己的 UserData 目录未必存在（实测只有玩家手动开过仓库之类才会建），
            // 于是模组会在游戏根目录凭空造一个 UserData —— 玩家可能装的其它模组各有各的写法，
            // 目录会越堆越乱。
            // 现在的顺序：
            //   ① 游戏已有 <根>\UserData        → 用它（与游戏习惯一致，便于玩家找到）
            //   ② 否则用 Application.persistentDataPath（= %USERPROFILE%\AppData\LocalLow\TeamSoda\Duckov）
            //      —— 这是 Unity 保证可写的模组数据位置，且一定在模组文件夹之外
            string root = Directory.GetParent(Application.dataPath).FullName;
            string userData = Path.Combine(root, "UserData");
            string path = Directory.Exists(userData)
                ? Path.Combine(userData, FolderName)
                : Path.Combine(Application.persistentDataPath, FolderName);

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }

        private string GetMapFileName()
        {
            // 修复 P0-7：多场景架构下 SceneManager.GetActiveScene() 返回的是**当前激活的子场景**
            // （MultiSceneCore.cs:302/329 会切换 active scene），拿它当键会导致
            //   · 子场景卸载前保存 → 数据写进 map_<主场景>.json
            //   · 换图后 Load 找不到当图数据
            // 正确来源是 LevelManager 所在主场景名（LevelManager.cs:771-790），
            // 它在一张 raid 图内切换子场景时保持不变。
            string name = BloodMoon.Utils.SceneUtils.GetLevelSceneName();
            if (string.IsNullOrEmpty(name)) name = "UnknownScene";
            return $"map_{SanitizeFileName(name)}.json";
        }

        /// <summary>
        /// 场景名可能含路径分隔符等非法字符，落盘前净化
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 原子写：先写临时文件再替换，避免写盘中途被打断导致 JSON 截断损坏（修复 P1-13）
        /// </summary>
        private static void WriteAllTextAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// 保存AI数据到文件
        /// </summary>
        public void Save()
        {
            _dirty = false;
            _lastWriteRealtime = Time.realtimeSinceStartup;
            try
            {
                string dir = GetSaveDirectory();

                // 保存地图数据 —— 只写回这份记忆**所属**的文件。
                // 绝不能用"当前场景名"重新算文件名：实机曾出现 raid 结束后在加载场景里
                // 把 raid 记忆写成 map_LoadingScreen 1.json 的情况（20:17 实测）。
                // 非 raid 场景（基地/加载界面）根本不记录地图记忆，避免生成垃圾文件。
                if (_mapIsRaid && !string.IsNullOrEmpty(_loadedMapFileName))
                {
                    string mapJson = SaveMapTo(dir, _loadedMapFileName);

                    // 首次落盘打一行自检（确认 List<结构体> 真的被序列化了 —— 它曾被 JsonUtility 静默丢弃）
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug(
                            $"[AIDataStore] Save ok -> {_loadedMapFileName} ({mapJson.Length}B). " +
                            $"stuck={StuckSpots.Count} danger={DangerEvents.Count} " +
                            $"mapDangerSerialized={mapJson.Contains("dangerPos")}");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to save AI data: {e.Message}");
            }
        }

        /// <summary>
        /// 把当前地图记忆写入指定文件（用于切图时把旧图数据写回**旧文件**），返回写出的 JSON
        /// </summary>
        private string SaveMapTo(string dir, string fileName)
        {
            var mapData = new MapSaveData();
            mapData.FromStore(this);
            string mapJson = JsonUtility.ToJson(mapData, true);
            WriteAllTextAtomic(Path.Combine(dir, fileName), mapJson);
            return mapJson;
        }

        /// <summary>
        /// 请求保存（去抖）。修复 P1-13：BossManager 在一次 raid 结束/子场景卸载里会连续调用多次
        /// Save()（同一帧最多 4~6 次全量 JSON 落盘），改为打脏标记，由 ModBehaviour 的 0.5s 心跳统一 Flush。
        /// </summary>
        public void RequestSave()
        {
            _dirty = true;
        }

        /// <summary>
        /// 若存在未落盘的修改则写盘（带最小间隔），由 ModBehaviour 的定时器调用
        /// </summary>
        public void FlushIfDirty(float minIntervalSeconds = 0.5f)
        {
            if (!_dirty) return;
            if (Time.realtimeSinceStartup - _lastWriteRealtime < minIntervalSeconds) return;
            Save();
        }

        /// <summary>
        /// 从文件加载AI数据
        /// </summary>
        public void Load()
        {
            string dir = GetSaveDirectory();

            // 只有 raid 地图才加载/记录地图记忆（基地、加载界面不记录）
            _mapIsRaid = IsCurrentRaidMap();
            if (_mapIsRaid)
            {
                LoadMap(dir);
                Logger.Log($"Loaded map AI data: {_loadedMapFileName}");
            }
            else
            {
                _loadedMapFileName = string.Empty;
                Logger.Log("Not a raid map, AI map memory idle until a raid starts");
            }
        }

        /// <summary>
        /// 关卡初始化时调用：按当前关卡切换地图记忆。
        /// 修复 P0-7 的完整语义：
        ///   · raid → 若换了图，先把旧图的记忆写回**旧文件**，再载入新图数据；
        ///   · 离开 raid（基地/加载场景）→ 把 raid 记忆落盘后停止记录，
        ///     这样不会再把 raid 数据写成 map_LoadingScreen / map_Startup 之类的垃圾文件。
        /// </summary>
        public void ReloadForCurrentScene()
        {
            try
            {
                string dir = GetSaveDirectory();
                bool isRaid = IsCurrentRaidMap();

                if (!isRaid)
                {
                    if (_mapIsRaid && !string.IsNullOrEmpty(_loadedMapFileName))
                    {
                        SaveMapTo(dir, _loadedMapFileName);   // 离开 raid：落盘
                    }
                    _mapIsRaid = false;
                    _loadedMapFileName = string.Empty;
                    return;
                }

                string newFile = GetMapFileName();
                if (_mapIsRaid && newFile == _loadedMapFileName) return;   // 同一张 raid 图（含子场景切换）

                if (_mapIsRaid && !string.IsNullOrEmpty(_loadedMapFileName))
                {
                    SaveMapTo(dir, _loadedMapFileName);       // 旧图数据写回旧文件
                }
                _mapIsRaid = true;
                LoadMap(dir);                                  // 载入新图数据（内部设置 _loadedMapFileName）
                Logger.Log($"Switched map AI data to {_loadedMapFileName}");
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to reload map AI data: {e.Message}");
            }
        }

        /// <summary>当前关卡是否是 raid 地图（基地与加载场景不算）</summary>
        private static bool IsCurrentRaidMap()
        {
            // 统一走 SceneUtils：LevelManager.IsRaidMap 在基地/加载场景上不可靠
            // （LevelConfig 的字段默认值就是 isRaidMap=true / isBaseLevel=false）
            return BloodMoon.Utils.SceneUtils.IsRaidScene();
        }


        private void LoadMap(string dir)
        {
            try
            {
                string fileName = GetMapFileName();
                string mapPath = Path.Combine(dir, fileName);
                if (File.Exists(mapPath))
                {
                    string json = File.ReadAllText(mapPath);
                    var mapData = JsonUtility.FromJson<MapSaveData>(json);
                    if (mapData != null) mapData.ToStore(this);
                }
                else
                {
                    // 地图的默认设置
                    StuckSpots = new List<Vector3>();
                    DangerEvents = new List<DangerEvent>();
                    LastKnownPlayerPos = Vector3.zero;
                    LastSeenTime = 0f;
                    DeathCount = 0;
                    LastDeathTime = 0f;
                }
                _loadedMapFileName = fileName;
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to load map AI data: {e.Message}");
            }
        }

        public bool TryGetKnownCover(CharacterMainControl player, Vector3 center, float searchRadius, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            // 实时本地封面搜索而非离线列表
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            int samples = 8; // 为提高性能，已从16减少
            float best = float.NegativeInfinity;
            
            // 优化：缓存变换访问以避免重复计算
            Vector3 playerPos = player.transform.position;
            
            for (int i = 0; i < samples; i++)
            {
                var rnd = UnityEngine.Random.insideUnitCircle * searchRadius;
                var p = center + new Vector3(rnd.x, 0f, rnd.y);
                if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    p = hit.point;
                    var origin = p + Vector3.up * 1.0f;
                    var target = playerPos + Vector3.up * 1.2f;
                    var dir = target - origin;
                    // 检查遮挡物是否阻挡了对队员的视线
                    if (Physics.Raycast(origin, dir.normalized, dir.magnitude, mask))
                    {
                        // 发现一个被玩家视线遮挡的位置
                        float score = -GetHeatAt(p, Time.time, 6f);
                        if (score > best) { best = score; coverPos = p; }
                    }
                }
            }
            return best > float.NegativeInfinity;
        }

        public void RegisterDeath(Vector3 pos)
        {
            DeathCount++;
            LastDeathTime = Time.time;
            DangerEvents.Add(new DangerEvent { pos = pos, time = LastDeathTime, weight = 2f });
            if (DangerEvents.Count > 64) DangerEvents.RemoveAt(0);
        }

        // 运行时参与跟踪（非序列化）
        private Dictionary<int, List<CharacterMainControl>> _engagements = new Dictionary<int, List<CharacterMainControl>>();

        public void RegisterEngagement(CharacterMainControl target, CharacterMainControl attacker)
        {
            if (target == null || attacker == null) return;
            int id = target.GetInstanceID();
            if (!_engagements.ContainsKey(id)) _engagements[id] = new List<CharacterMainControl>();
            
            var list = _engagements[id];
            
            // 清理空值，以保持列表的健康状态
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null) list.RemoveAt(i);
            }
            
            if (!list.Contains(attacker)) list.Add(attacker);
        }

        public void UnregisterEngagement(CharacterMainControl target, CharacterMainControl attacker)
        {
            if (target == null) return;
            int id = target.GetInstanceID();
            if (!_engagements.ContainsKey(id)) return;
            _engagements[id].Remove(attacker);
        }

        public int GetEngagementCount(CharacterMainControl target)
        {
            if (target == null) return 0;
            int id = target.GetInstanceID();
            if (!_engagements.ContainsKey(id)) return 0;
            var list = _engagements[id];
            // 清理
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || !list[i].gameObject.activeInHierarchy || list[i].Health.CurrentHealth <= 0)
                    list.RemoveAt(i);
            }
            return list.Count;
        }

        // --- 全局字符缓存与空间网格 ---
        
        private List<CharacterMainControl> _globalCharacterCache = new List<CharacterMainControl>();
        private float _lastCacheUpdateTime;
        private SpatialGrid _spatialGrid = new SpatialGrid();
        
        public List<CharacterMainControl> AllCharacters => _globalCharacterCache;
        public SpatialGrid Grid => _spatialGrid;
        
        public void UpdateCache()
        {
            if (Time.time - _lastCacheUpdateTime < 0.5f) return; // Update 2Hz
            _lastCacheUpdateTime = Time.time;
            
            _globalCharacterCache.Clear();
            var all = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>();
            for(int i=0; i<all.Length; i++)
            {
                if(all[i] != null && all[i].gameObject.activeInHierarchy && all[i].Health.CurrentHealth > 0)
                {
                    _globalCharacterCache.Add(all[i]);
                }
            }
            
            // 重建空间网格
            _spatialGrid.Clear();
            foreach(var c in _globalCharacterCache)
            {
                _spatialGrid.Add(c);
            }

        }
    }

    /// <summary>
    /// 空间网格类，用于高效的空间查询
    /// </summary>
    public class SpatialGrid
    {
        private Dictionary<Vector2Int, List<CharacterMainControl>> _grid = new Dictionary<Vector2Int, List<CharacterMainControl>>();
        private float _cellSize = 15f; // 大小
        
        /// <summary>
        /// 清空网格
        /// </summary>
        public void Clear() => _grid.Clear();
        
        /// <summary>
        /// 添加角色到网格
        /// </summary>
        /// <param name="c">角色对象</param>
        public void Add(CharacterMainControl c)
        {
            Vector2Int cell = GetCell(c.transform.position);
            if (!_grid.TryGetValue(cell, out var list))
            {
                list = new List<CharacterMainControl>();
                _grid[cell] = list;
            }
            list.Add(c);
        }
        
        /// <summary>
        /// 查询指定范围内的角色
        /// </summary>
        /// <param name="pos">中心位置</param>
        /// <param name="radius">查询半径</param>
        /// <param name="result">结果列表</param>
        public void Query(Vector3 pos, float radius, List<CharacterMainControl> result)
        {
            result.Clear();
            int cellRange = Mathf.CeilToInt(radius / _cellSize);
            Vector2Int center = GetCell(pos);
            float sqrRadius = radius * radius;
            
            for (int x = -cellRange; x <= cellRange; x++)
            {
                for (int y = -cellRange; y <= cellRange; y++)
                {
                    Vector2Int key = center + new Vector2Int(x, y);
                    if (_grid.TryGetValue(key, out var list))
                    {
                        for(int i=0; i<list.Count; i++)
                        {
                            var c = list[i];
                            if (c == null) continue;
                            if (Vector3.SqrMagnitude(c.transform.position - pos) <= sqrRadius)
                            {
                                result.Add(c);
                            }
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 获取位置所在的网格单元
        /// </summary>
        /// <param name="pos">位置</param>
        /// <returns>网格单元坐标</returns>
        private Vector2Int GetCell(Vector3 pos)
        {
            return new Vector2Int(Mathf.FloorToInt(pos.x / _cellSize), Mathf.FloorToInt(pos.z / _cellSize));
        }
    }
}
