using System;
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
            public List<float> playerSpeedSamples = new List<float>();
            public List<ApproachStat> approachStats = new List<ApproachStat>();
            public List<KeyWeight> strategyWeights = new List<KeyWeight>();
            public List<LeaderPref> leaderPrefs = new List<LeaderPref>();

            public void FromStore(AIDataStore s)
            {
                playerSpeedSamples = s._playerSpeedSamples;
                approachStats = s.ApproachStats;
                strategyWeights = s.StrategyWeights;
                leaderPrefs = s.LeaderPrefs;
            }

            public void ToStore(AIDataStore s)
            {
                if (playerSpeedSamples != null) s._playerSpeedSamples = playerSpeedSamples;
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
            public List<DangerEvent> dangerEvents = new List<DangerEvent>();
            public Vector3 lastKnownPlayerPos;
            public float lastSeenTime;
            public int reloadCount;
            public float lastReloadMark;
            public int deathCount;
            public float lastDeathTime;

            public void FromStore(AIDataStore s)
            {
                playerAmbushSpots = s.PlayerAmbushSpots;
                stuckSpots = s.StuckSpots;
                dangerEvents = s.DangerEvents;
                lastKnownPlayerPos = s.LastKnownPlayerPos;
                lastSeenTime = s.LastSeenTime;
                reloadCount = s._reloadCount;
                lastReloadMark = s._lastReloadMark;
                deathCount = s.DeathCount;
                lastDeathTime = s.LastDeathTime;
            }

            public void ToStore(AIDataStore s)
            {
                if (playerAmbushSpots != null) s.PlayerAmbushSpots = playerAmbushSpots;
                if (stuckSpots != null) s.StuckSpots = stuckSpots;
                if (dangerEvents != null) s.DangerEvents = dangerEvents;
                s.LastKnownPlayerPos = lastKnownPlayerPos;
                s.LastSeenTime = lastSeenTime;
                s._reloadCount = reloadCount;
                s._lastReloadMark = lastReloadMark;
                s.DeathCount = deathCount;
                s.LastDeathTime = lastDeathTime;
            }
        }

        private const string FolderName = "BloodMoonAI";
        private const string GlobalFileName = "global_tactics.json";

        private List<float> _playerSpeedSamples = new List<float>();
        public Vector3 LastKnownPlayerPos;
        public float LastSeenTime;
        public List<Vector3> PlayerAmbushSpots = new List<Vector3>();
        public List<Vector3> StuckSpots = new List<Vector3>();

        public void MarkStuckSpot(Vector3 pos)
        {
            // Cluster check
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if (Vector3.Distance(StuckSpots[i], pos) < 2.0f) return;
            }
            StuckSpots.Add(pos);
            if (StuckSpots.Count > 128) StuckSpots.RemoveAt(0);
        }

        public bool IsStuckSpot(Vector3 pos, float threshold)
        {
            for (int i = 0; i < StuckSpots.Count; i++)
            {
                if (Vector3.Distance(StuckSpots[i], pos) < threshold) return true;
            }
            return false;
        }

        public void MarkPlayerAmbush(Vector3 playerPos)
        {
            // Cluster check
            for (int i = 0; i < PlayerAmbushSpots.Count; i++)
            {
                if (Vector3.Distance(PlayerAmbushSpots[i], playerPos) < 2.0f)
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
             for (int i = 0; i < PlayerAmbushSpots.Count; i++)
            {
                if (Vector3.Distance(PlayerAmbushSpots[i], pos) < threshold) return true;
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
            public int id;
            public float baseRadius;
            public float sideAngle;
            public float spacing;
            public float lastUpdate;
        }
        public List<LeaderPref> LeaderPrefs = new List<LeaderPref>();

        public void RecordPlayerSpeed(float v)
        {
            _playerSpeedSamples.Add(v);
            if (_playerSpeedSamples.Count > 1000) _playerSpeedSamples.RemoveAt(0);
        }

        public float GetAverageSpeed()
        {
            if (_playerSpeedSamples.Count == 0) return 3f;
            float s = 0f;
            for (int i = 0; i < _playerSpeedSamples.Count; i++) s += _playerSpeedSamples[i];
            return s / _playerSpeedSamples.Count;
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
            string path = Path.Combine(Directory.GetCurrentDirectory(), "UserData", FolderName);
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
                    _playerSpeedSamples = new List<float>();
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
            int samples = 16;
            float best = float.NegativeInfinity;
            for (int i = 0; i < samples; i++)
            {
                var rnd = UnityEngine.Random.insideUnitCircle * searchRadius;
                var p = center + new Vector3(rnd.x, 0f, rnd.y);
                if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    p = hit.point;
                    var origin = p + Vector3.up * 1.0f;
                    var target = player.transform.position + Vector3.up * 1.2f;
                    var dir = target - origin;
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

        public LeaderPref GetLeaderPref(int id)
        {
            for (int i = 0; i < LeaderPrefs.Count; i++)
            {
                if (LeaderPrefs[i].id == id) return LeaderPrefs[i];
            }
            var lp = new LeaderPref { id = id, baseRadius = 3.0f, sideAngle = 30f, spacing = 1.2f, lastUpdate = Time.time };
            LeaderPrefs.Add(lp);
            return lp;
        }

        public void UpdateLeaderPref(int id, float pressureScore, Vector3 center)
        {
            int idx = -1;
            for (int i = 0; i < LeaderPrefs.Count; i++)
            {
                if (LeaderPrefs[i].id == id) { idx = i; break; }
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
            if (idx >= 0) LeaderPrefs[idx] = lp; else LeaderPrefs.Add(lp);
        }

        public void SetLeaderPrefBaseline(int id, float baseRadius, float sideAngle, float spacing)
        {
            int idx = -1;
            for (int i = 0; i < LeaderPrefs.Count; i++)
            {
                if (LeaderPrefs[i].id == id) { idx = i; break; }
            }
            var lp = new LeaderPref { id = id, baseRadius = baseRadius, sideAngle = sideAngle, spacing = spacing, lastUpdate = Time.time };
            if (idx >= 0) LeaderPrefs[idx] = lp; else LeaderPrefs.Add(lp);
        }

        public void RecordApproachOutcome(Vector3 point, bool success)
        {
            int idx = -1; float minDist = 1.5f;
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
            if (ApproachStats.Count > 60) ApproachStats.RemoveAt(0);
        }

        public float GetApproachWeight(Vector3 point)
        {
            float w = 1f; float minDist = 1.5f;
            for (int i = 0; i < ApproachStats.Count; i++)
            {
                var st = ApproachStats[i];
                if (Vector3.Distance(st.pos, point) < minDist)
                {
                    float s = Mathf.Max(0, st.success);
                    float f = Mathf.Max(0, st.fail);
                    float recency = Mathf.Exp(-Mathf.Max(0f, Time.time - st.lastTime) / 120f);
                    w = (1f + s) / (1f + f) * (1f + 0.3f * recency);
                    break;
                }
            }
            return w;
        }
    }
}
