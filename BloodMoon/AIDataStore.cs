using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov;
using Duckov.Utilities;
using System.IO;

namespace BloodMoon
{
    [Serializable]
    public class AIDataStore
    {
        // Global Data (IQ / Tactics) - Shared across all maps
        [Serializable]
        private class GlobalSaveData
        {
            public float avgPlayerSpeed = 3.0f;
            public List<ApproachStat> approachStats = new List<ApproachStat>();
            public List<KeyWeight> strategyWeights = new List<KeyWeight>();
            public List<LeaderPref> leaderPrefs = new List<LeaderPref>();

            public void FromStore(AIDataStore s)
            {
                avgPlayerSpeed = s._avgPlayerSpeed;
                approachStats = s.ApproachStats;
                strategyWeights = s.StrategyWeights;
                leaderPrefs = s.LeaderPrefs;
            }

            public void ToStore(AIDataStore s)
            {
                s._avgPlayerSpeed = avgPlayerSpeed;
                if (approachStats != null) s.ApproachStats = approachStats;
                if (strategyWeights != null) s.StrategyWeights = strategyWeights;
                if (leaderPrefs != null) s.LeaderPrefs = leaderPrefs;
            }
        }

        // Map Specific Data (Memory / Danger) - Separate per map
        [Serializable]
        private class MapSaveData
        {
            public List<Vector3> playerAmbushSpots = new List<Vector3>();
            public List<Vector3> stuckSpots = new List<Vector3>();
            public Vector3 lastKnownPlayerPos;
            public int reloadCount;
            public int deathCount;

            public void FromStore(AIDataStore s)
            {
                playerAmbushSpots = s.PlayerAmbushSpots;
                stuckSpots = s.StuckSpots;
                lastKnownPlayerPos = s.LastKnownPlayerPos;
                reloadCount = s._reloadCount;
                deathCount = s.DeathCount;
            }

            public void ToStore(AIDataStore s)
            {
                if (playerAmbushSpots != null) s.PlayerAmbushSpots = playerAmbushSpots;
                if (stuckSpots != null) s.StuckSpots = stuckSpots;
                s.LastKnownPlayerPos = lastKnownPlayerPos;
                s._reloadCount = reloadCount;
                s.DeathCount = deathCount;
            }
        }

        private const string FolderName = "BloodMoonAI";
        private const string GlobalFileName = "global_tactics.json";

        private float _avgPlayerSpeed = 3.0f;
        public Vector3 LastKnownPlayerPos;
        public float LastSeenTime;
        public List<Vector3> PlayerAmbushSpots = new List<Vector3>();
        public List<Vector3> StuckSpots = new List<Vector3>();

        public void MarkStuckSpot(Vector3 pos)
        {
            // Cluster check
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if ((StuckSpots[i] - pos).sqrMagnitude < 4.0f) return;
            }
            StuckSpots.Add(pos);
            if (StuckSpots.Count > 128) StuckSpots.RemoveAt(0);
        }

        public bool IsStuckSpot(Vector3 pos, float threshold)
        {
            float sqrThresh = threshold * threshold;
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if ((StuckSpots[i] - pos).sqrMagnitude < sqrThresh) return true;
            }
            return false;
        }

        public void MarkPlayerAmbush(Vector3 playerPos)
        {
            // Cluster check
            for (int i = 0; i < PlayerAmbushSpots.Count; i++)
            {
                if ((PlayerAmbushSpots[i] - playerPos).sqrMagnitude < 4.0f)
                {
                    // Already known spot
                    return;
                }
            }
            PlayerAmbushSpots.Add(playerPos);
            if (PlayerAmbushSpots.Count > 32) PlayerAmbushSpots.RemoveAt(0);
        }

        public bool IsPlayerAmbushSpot(Vector3 pos, float threshold)
        {
             float sqrThresh = threshold * threshold;
             for (int i = 0; i < PlayerAmbushSpots.Count; i++)
            {
                if ((PlayerAmbushSpots[i] - pos).sqrMagnitude < sqrThresh) return true;
            }
            return false;
        }
        [Serializable]
        public struct DangerEvent
        {
            public Vector3 pos;
            public float time;
            public float weight;
        }
        public List<DangerEvent> DangerEvents = new List<DangerEvent>();
        private int _reloadCount;
        private float _lastReloadMark;
        public int DeathCount;
        public float LastDeathTime;
        [Serializable]
        public struct ApproachStat
        {
            public Vector3 pos;
            public int success;
            public int fail;
            public float lastTime;
        }
        public List<ApproachStat> ApproachStats = new List<ApproachStat>();
        [Serializable]
        public struct KeyWeight
        {
            public string key;
            public float w;
        }
        public List<KeyWeight> StrategyWeights = new List<KeyWeight>();

        [Serializable]
        public struct LeaderPref
        {
            public string id;
            public float baseRadius;
            public float sideAngle;
            public float spacing;
            public float lastUpdate;
        }
        public List<LeaderPref> LeaderPrefs = new List<LeaderPref>();
        
        // Runtime Cache for O(1) lookup
        private Dictionary<string, int> _leaderPrefIndexCache = new Dictionary<string, int>();

        public void RecordPlayerSpeed(float v)
        {
            if (v > 0.05f && v < 15f)
            {
                // Exponential Moving Average (EMA) with alpha = 0.05
                _avgPlayerSpeed = Mathf.Lerp(_avgPlayerSpeed, v, 0.05f);
            }
        }

        public float GetAverageSpeed()
        {
            return _avgPlayerSpeed;
        }

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

        public float[] ComputeHeatBatch(Vector3[] positions, float now, float radius)
        {
            float[] results = new float[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                results[i] = GetHeatAt(positions[i], now, radius);
            }
            return results;
        }

        public void DecayAndPrune(float now, float maxAgeSec)
        {
            for (int i = DangerEvents.Count - 1; i >= 0; i--)
            {
                if (now - DangerEvents[i].time > maxAgeSec) DangerEvents.RemoveAt(i);
            }
            if (DangerEvents.Count > 64) DangerEvents.RemoveRange(0, DangerEvents.Count - 64);
        }

        public void RegisterPlayerReload()
        {
            float now = Time.time;
            if (now - _lastReloadMark > 1.0f)
            {
                _reloadCount++;
                _lastReloadMark = now;
            }
        }

        public float GetAggressionBoost(float now)
        {
            float recent = Mathf.Clamp01(1f - Mathf.Max(0f, now - _lastReloadMark) / 6f);
            return 1f + recent * Mathf.Clamp(_reloadCount, 0, 5) * 0.03f;
        }

        private string GetSaveDirectory()
        {
            // Use BepInEx config path or UserData path if available, otherwise relative to executable
            string root = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(root, "UserData", FolderName);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }

        private string GetMapFileName()
        {
            var scene = SceneManager.GetActiveScene();
            var name = string.IsNullOrEmpty(scene.name) ? "UnknownScene" : scene.name;
            return $"map_{name}.json";
        }

        public void Save()
        {
            try
            {
                string dir = GetSaveDirectory();

                // 1. Save Global Data
                var globalData = new GlobalSaveData();
                globalData.FromStore(this);
                string globalJson = JsonUtility.ToJson(globalData, true);
                File.WriteAllText(Path.Combine(dir, GlobalFileName), globalJson);

                // 2. Save Map Data
                var mapData = new MapSaveData();
                mapData.FromStore(this);
                string mapJson = JsonUtility.ToJson(mapData, true);
                File.WriteAllText(Path.Combine(dir, GetMapFileName()), mapJson);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BloodMoon] Failed to save AI data: {e.Message}");
            }
        }

        public void Load()
        {
            try
            {
                string dir = GetSaveDirectory();

                // 1. Load Global Data
                string globalPath = Path.Combine(dir, GlobalFileName);
                if (File.Exists(globalPath))
                {
                    string json = File.ReadAllText(globalPath);
                    var globalData = JsonUtility.FromJson<GlobalSaveData>(json);
                    if (globalData != null) globalData.ToStore(this);
                }
                else
                {
                    // Defaults for global
                    _avgPlayerSpeed = 3.0f;
                    ApproachStats = new List<ApproachStat>();
                    StrategyWeights = new List<KeyWeight>();
                    LeaderPrefs = new List<LeaderPref>();
                }

                // 2. Load Map Data
                string mapPath = Path.Combine(dir, GetMapFileName());
                if (File.Exists(mapPath))
                {
                    string json = File.ReadAllText(mapPath);
                    var mapData = JsonUtility.FromJson<MapSaveData>(json);
                    if (mapData != null) mapData.ToStore(this);
                }
                else
                {
                    // Defaults for map
                    PlayerAmbushSpots = new List<Vector3>();
                    StuckSpots = new List<Vector3>();
                    DangerEvents = new List<DangerEvent>();
                    LastKnownPlayerPos = Vector3.zero;
                    LastSeenTime = 0f;
                    _reloadCount = 0;
                    _lastReloadMark = 0f;
                    DeathCount = 0;
                    LastDeathTime = 0f;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BloodMoon] Failed to load AI data: {e.Message}");
            }
        }

        public bool TryGetKnownCover(CharacterMainControl player, Vector3 center, float searchRadius, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            // Real-time local cover search instead of offline list
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            int samples = 8; // Reduced from 16 for performance
            float best = float.NegativeInfinity;
            
            // Optimization: Cache transform access
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
                    // Check if cover blocks view to player
                    if (Physics.Raycast(origin, dir.normalized, dir.magnitude, mask))
                    {
                        // Found a spot blocked from player view
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
            ApplyReward("dash_prob", -0.1f);
            ApplyReward("spray_prob", -0.1f);
            ApplyReward("approach", -0.1f);
        }

        public float GetWeight(string key, float def)
        {
            for (int i = 0; i < StrategyWeights.Count; i++)
            {
                if (StrategyWeights[i].key == key) return StrategyWeights[i].w;
            }
            StrategyWeights.Add(new KeyWeight { key = key, w = def });
            return def;
        }

        public void ApplyReward(string key, float delta)
        {
            for (int i = 0; i < StrategyWeights.Count; i++)
            {
                if (StrategyWeights[i].key == key)
                {
                    var v = StrategyWeights[i];
                    v.w = Mathf.Clamp(v.w + delta, 0.2f, 1.8f);
                    StrategyWeights[i] = v;
                    return;
                }
            }
            StrategyWeights.Add(new KeyWeight { key = key, w = Mathf.Clamp(delta, 0.2f, 1.8f) });
        }

        public void DecayWeights(float factor)
        {
            for (int i = 0; i < StrategyWeights.Count; i++)
            {
                var kv = StrategyWeights[i];
                float def = 1.0f;
                kv.w = Mathf.Lerp(kv.w, def, 1f - factor);
                StrategyWeights[i] = kv;
            }
        }

        public void RecenterWeights(float rate)
        {
            float sum = 0f;
            for (int i = 0; i < StrategyWeights.Count; i++) sum += StrategyWeights[i].w;
            float avg = StrategyWeights.Count > 0 ? sum / StrategyWeights.Count : 1f;
            for (int i = 0; i < StrategyWeights.Count; i++)
            {
                var kv = StrategyWeights[i];
                kv.w = Mathf.Lerp(kv.w, avg, rate);
                StrategyWeights[i] = kv;
            }
        }

        public LeaderPref GetLeaderPref(string id)
        {
            if (_leaderPrefIndexCache.TryGetValue(id, out int idx))
            {
                if (idx < LeaderPrefs.Count && LeaderPrefs[idx].id == id) return LeaderPrefs[idx];
                _leaderPrefIndexCache.Remove(id); // Invalid cache
            }
            
            for (int i = 0; i < LeaderPrefs.Count; i++)
            {
                if (LeaderPrefs[i].id == id) 
                {
                    _leaderPrefIndexCache[id] = i;
                    return LeaderPrefs[i];
                }
            }
            var lp = new LeaderPref { id = id, baseRadius = 3.0f, sideAngle = 30f, spacing = 1.2f, lastUpdate = Time.time };
            LeaderPrefs.Add(lp);
            _leaderPrefIndexCache[id] = LeaderPrefs.Count - 1;
            
            // Prune if too large
            if (LeaderPrefs.Count > 256)
            {
                // Remove oldest
                int oldestIdx = 0; float oldestTime = float.MaxValue;
                for(int i=0; i<LeaderPrefs.Count; i++)
                {
                    if (LeaderPrefs[i].lastUpdate < oldestTime) { oldestTime = LeaderPrefs[i].lastUpdate; oldestIdx = i; }
                }
                LeaderPrefs.RemoveAt(oldestIdx);
                _leaderPrefIndexCache.Clear(); // Full rebuild needed
            }
            return lp;
        }

        public void UpdateLeaderPref(string id, float pressureScore, Vector3 center)
        {
            int idx = -1;
            if (_leaderPrefIndexCache.TryGetValue(id, out int cIdx))
            {
                if (cIdx < LeaderPrefs.Count && LeaderPrefs[cIdx].id == id) idx = cIdx;
            }
            
            if (idx == -1)
            {
                for (int i = 0; i < LeaderPrefs.Count; i++)
                {
                    if (LeaderPrefs[i].id == id) { idx = i; _leaderPrefIndexCache[id] = i; break; }
                }
            }
            
            LeaderPref lp = idx >= 0 ? LeaderPrefs[idx] : GetLeaderPref(id);
            float density = 0f;
            float densNorm = Mathf.Clamp(density / 8f, 0f, 1f);
            float pressNorm = Mathf.Clamp(pressureScore / 3f, 0f, 1f);
            float widen = Mathf.Clamp01(0.5f * densNorm + 0.5f * pressNorm);
            float tighten = 1f - widen;
            lp.baseRadius = Mathf.Clamp(Mathf.Lerp(lp.baseRadius, 2.8f + 3.2f * widen, 0.1f), 2.5f, 6.8f);
            lp.spacing = Mathf.Clamp(Mathf.Lerp(lp.spacing, 1.0f + 0.8f * widen, 0.1f), 1.0f, 1.8f);
            float targetAngle = Mathf.Lerp(28f, 38f, widen);
            lp.sideAngle = Mathf.Clamp(Mathf.Lerp(lp.sideAngle, targetAngle, 0.1f), 25f, 40f);
            lp.lastUpdate = Time.time;
            if (idx >= 0) LeaderPrefs[idx] = lp; 
            else 
            {
                LeaderPrefs.Add(lp);
                _leaderPrefIndexCache[id] = LeaderPrefs.Count - 1;
            }
        }

        public void SetLeaderPrefBaseline(string id, float baseRadius, float sideAngle, float spacing)
        {
            int idx = -1;
            if (_leaderPrefIndexCache.TryGetValue(id, out int cIdx))
            {
                if (cIdx < LeaderPrefs.Count && LeaderPrefs[cIdx].id == id) idx = cIdx;
            }
            
            if (idx == -1)
            {
                for (int i = 0; i < LeaderPrefs.Count; i++)
                {
                    if (LeaderPrefs[i].id == id) { idx = i; _leaderPrefIndexCache[id] = i; break; }
                }
            }
            
            var lp = new LeaderPref { id = id, baseRadius = baseRadius, sideAngle = sideAngle, spacing = spacing, lastUpdate = Time.time };
            if (idx >= 0) LeaderPrefs[idx] = lp; 
            else 
            {
                LeaderPrefs.Add(lp);
                _leaderPrefIndexCache[id] = LeaderPrefs.Count - 1;
            }
        }

        public void RecordApproachOutcome(Vector3 point, bool success)
        {
            // Grid Snap (Quantize) to 2m grid to generalize learning
            point = new Vector3(
                Mathf.Round(point.x / 2f) * 2f,
                Mathf.Round(point.y / 1f) * 1f, // Y matters less but snap to 1m
                Mathf.Round(point.z / 2f) * 2f
            );

            int idx = -1; float minDist = 0.5f; // Since we snap, exact match is likely
            for (int i = 0; i < ApproachStats.Count; i++)
            {
                if (Vector3.Distance(ApproachStats[i].pos, point) < minDist) { idx = i; break; }
            }
            if (idx == -1)
            {
                ApproachStats.Add(new ApproachStat { pos = point, success = success ? 1 : 0, fail = success ? 0 : 1, lastTime = Time.time });
            }
            else
            {
                var st = ApproachStats[idx];
                if (success) st.success++; else st.fail++;
                st.lastTime = Time.time;
                ApproachStats[idx] = st;
            }
            if (ApproachStats.Count > 120) ApproachStats.RemoveAt(0); // Increased buffer
        }

        public float GetApproachWeight(Vector3 point)
        {
            // Quantize to match recording (Model lookup)
            point = new Vector3(
                Mathf.Round(point.x / 2f) * 2f,
                Mathf.Round(point.y / 1f) * 1f,
                Mathf.Round(point.z / 2f) * 2f
            );

            float w = 1f; float minDist = 0.5f;
            for (int i = 0; i < ApproachStats.Count; i++)
            {
                var st = ApproachStats[i];
                if (Vector3.Distance(st.pos, point) < minDist)
                {
                    float s = Mathf.Max(0, st.success);
                    float f = Mathf.Max(0, st.fail);
                    float recency = Mathf.Exp(-Mathf.Max(0f, Time.time - st.lastTime) / 120f);
                    // Thompson Sampling-ish heuristic: (success + 1) / (total + 2)
                    // But here we want a weight multiplier.
                    // If high success -> weight > 1. If high fail -> weight < 1.
                    float total = s + f;
                    if (total > 0)
                    {
                        float rate = s / total;
                        // Bias towards 1.0 if low samples (recency decay also lowers confidence)
                        w = Mathf.Lerp(1.0f, rate * 2.0f, Mathf.Clamp01(total / 5f) * recency); 
                        // Map 0..1 rate to 0.5..1.5 multiplier range? Or 0..2?
                        // Let's say: rate 0.5 -> 1.0. rate 1.0 -> 2.0. rate 0.0 -> 0.0?
                        // If we fail a lot, we want to discourage (w < 1).
                    }
                    break;
                }
            }
            return w;
        }

        // Runtime Engagement Tracking (Not serialized)
        private Dictionary<int, List<CharacterMainControl>> _engagements = new Dictionary<int, List<CharacterMainControl>>();

        public void RegisterEngagement(CharacterMainControl target, CharacterMainControl attacker)
        {
            if (target == null || attacker == null) return;
            int id = target.GetInstanceID();
            if (!_engagements.ContainsKey(id)) _engagements[id] = new List<CharacterMainControl>();
            
            var list = _engagements[id];
            
            // Cleanup nulls while we are here to keep list healthy
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
            // Lazy cleanup
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || !list[i].gameObject.activeInHierarchy || list[i].Health.CurrentHealth <= 0)
                    list.RemoveAt(i);
            }
            return list.Count;
        }

        // --- Global Character Cache & Spatial Grid ---
        
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
            
            // Rebuild Spatial Grid
            _spatialGrid.Clear();
            foreach(var c in _globalCharacterCache)
            {
                _spatialGrid.Add(c);
            }
        }
    }

    public class SpatialGrid
    {
        private Dictionary<Vector2Int, List<CharacterMainControl>> _grid = new Dictionary<Vector2Int, List<CharacterMainControl>>();
        private float _cellSize = 15f; // Cell size
        
        public void Clear() => _grid.Clear();
        
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
        
        private Vector2Int GetCell(Vector3 pos)
        {
            return new Vector2Int(Mathf.FloorToInt(pos.x / _cellSize), Mathf.FloorToInt(pos.z / _cellSize));
        }
    }
}
