using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Duckov;
using Duckov.Utilities;
using Duckov.Scenes;
using ItemStatsSystem;
using Duckov.ItemUsage;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Threading.Tasks;
using BloodMoon.Utils;

namespace BloodMoon
{
    /// <summary>
    /// Boss管理器，负责Boss的生成、增强和管理
    /// </summary>
    public class BossManager
    {
        private readonly AIDataStore _store;
        private bool _initialized;
        private readonly HashSet<CharacterMainControl> _processed = new HashSet<CharacterMainControl>();
        private readonly HashSet<CharacterMainControl> _minionSpawned = new HashSet<CharacterMainControl>(); // 已刷过随从的 Boss（防无限刷）
        private float _recheckSpawnerTimer = 15f;
        
        private int _currentScene = -1;
        private bool _sceneSetupDone;
        private List<Points> _pointsCache = new List<Points>();
        private float _scanCooldown;
        private bool _setupRunning;
        private int _minionsPerBossCap => ModConfig.Instance.BossMinionCount;
        private readonly List<CharacterRandomPreset> _selectedBossPresets = new List<CharacterRandomPreset>();
        private int _selectedScene = -1;
        private bool _selectionInitialized;
        private readonly Dictionary<CharacterMainControl, Vector3> _groupAnchors = new Dictionary<CharacterMainControl, Vector3>();
        private float _bloodMoonStartTime = -1f;
        private float _bloodMoonDurationSec => ModConfig.Instance.ActiveHours * 3600f;
        private System.TimeSpan _bloodMoonStartGameTime;
        
        private bool _bloodMoonActive;
        private float _strategyDecayTimer;
        private readonly List<CharacterMainControl> _charactersCache = new List<CharacterMainControl>();
        private List<MonoBehaviour> _disabledSpawners = new List<MonoBehaviour>();
        private float _weightCheckTimer;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly string EmissionKeyword = "_EMISSION";

        /// <summary>
        /// 构造函数，初始化Boss管理器
        /// </summary>
        /// <param name="store">AI数据存储</param>
        public BossManager(AIDataStore store)
        {
            _store = store;
        }

        /// <summary>
        /// 初始化Boss管理器
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            RaidUtilities.OnRaidEnd += OnRaidEnded;
            RaidUtilities.OnRaidDead += OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded += OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy += OnMultiSceneDestroyed;
            _initialized = true;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            RaidUtilities.OnRaidEnd -= OnRaidEnded;
            RaidUtilities.OnRaidDead -= OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded -= OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy -= OnMultiSceneDestroyed;
            _initialized = false;
        }

        /// <summary>
        /// 重置会话状态
        /// </summary>
        private void ResetSession()
        {
            _setupRunning = false;
            _sceneSetupDone = false;
            _bloodMoonActive = false;
            _bloodMoonStartTime = -1f;
            _bloodMoonStartGameTime = default;
            _processed.Clear();
            _minionSpawned.Clear();
            _groupAnchors.Clear();
            _pointsCache.Clear();
            _charactersCache.Clear();
            _selectedBossPresets.Clear();
            _selectionInitialized = false;
            _disabledSpawners.Clear();
        }

        /// <summary>
        /// 主更新循环
        /// </summary>
        public void Tick()
        {
            try 
            {
                if (!_initialized) return;

                if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap || LevelManager.Instance.IsBaseLevel)
                {
                    return;
                }

                // 血月期间每 15 秒重新禁用一次默认生成器：
                // 多场景结构的 raid 会在玩家进入新区域时加载新子场景，新子场景的原版生成器
                // 不在首次禁用范围内 → 会持续在玩家身边刷怪（无限刷的根因之一）
                _recheckSpawnerTimer -= Time.deltaTime;
                if (_recheckSpawnerTimer <= 0f)
                {
                    _recheckSpawnerTimer = 15f;
                    if (ModConfig.Instance.DisableDefaultSpawners) DisableDefaultSpawner();
                }

                _weightCheckTimer -= Time.deltaTime;
                if (_weightCheckTimer <= 0f)
                {
                    _weightCheckTimer = 10.0f;
                    // 安全的集合遍历
                    var list = _processed.ToList();
                    foreach(var c in list)
                    {
                        if (c != null && c.gameObject.activeInHierarchy && c.Health.CurrentHealth > 0)
                        {
                            FixWeight(c, c.CharacterItem);
                        }
                        else
                        {
                            if (c != null) _processed.Remove(c);
                        }
                    }
                }
                
                _scanCooldown -= Time.deltaTime;
                if (_scanCooldown > 0f)
                {
                if (_scanCooldown > 0.9f)
                {
                     if (_store != null)
                     {
                         _store.DecayAndPrune(Time.time, 120f);
                     }
                }
                    return;
                }

                var player = CharacterMainControl.Main;
                if (!_sceneSetupDone || _setupRunning)
                {
                    return;
                }

                if (_store == null) return;
                // 优化：仅处理有效的角色
                var all = _store.AllCharacters;
                int count = all.Count;
                var playerPos = player != null ? player.transform.position : Vector3.zero;
                bool hasPlayer = player != null;
                
                for (int i = 0; i < count; i++)
                {
                    var c = all[i];
                    if (c == null || c.IsMainCharacter) continue;
                    
                    if (_processed.Contains(c)) continue;
                    // 已刷过随从的 Boss 不再刷（配合 _processed 双保险，防"每秒新 Boss 每秒刷随从"的无限循环）
                    if (_minionSpawned.Contains(c)) continue;

                    // 距离检查优化：距离玩家太远的角色不处理
                    if (hasPlayer && Vector3.SqrMagnitude(c.transform.position - playerPos) > 40000f) // 200m^2
                    {
                        continue;
                    }

                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (!isBoss) continue;
                    
                    if (_selectionInitialized && preset != null && !_selectedBossPresets.Contains(preset))
                    {
                        continue;
                    }
                    
                    EnhanceBoss(c);
                    EnableRevenge(c, player);
                    SpawnMinionsForBoss(c, c.transform.position, c.gameObject.scene.buildIndex).Forget();
                    _processed.Add(c);
                    _minionSpawned.Add(c);
                    BloodMoon.Utils.Logger.Log($"[BossManager] Enhanced existing boss: {c.name}");
                }
                
                _scanCooldown = 1.0f;
                
                _strategyDecayTimer -= Time.deltaTime;
                if (_strategyDecayTimer <= 0f)
                {
                    if (_store != null) _store.DecayWeights(0.98f);
                    _strategyDecayTimer = 30f;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] Tick Error: {e}");
            }
        }

        /// <summary>
        /// 禁用原生AI
        /// </summary>
        /// <param name="c">角色控制器</param>
        private void DisableVanillaAI(CharacterMainControl c)
        {
            if (c == null || c.IsMainCharacter) return;
            
            var ai = c.GetComponent<AICharacterController>();
            if (ai != null)
            {
                if (ai.enabled) ai.enabled = false;
            }
            
            var owner = c.GetComponent("GraphOwner") as MonoBehaviour;
            if (owner != null && owner.enabled)
            {
                try 
                {
                    var method = owner.GetType().GetMethod("StopBehaviour");
                    if (method != null) method.Invoke(owner, null);
                } 
                catch {}
                owner.enabled = false;
            }

            var flow = c.GetComponent("FlowScriptController") as MonoBehaviour;
            if (flow != null && flow.enabled)
            {
                 try 
                {
                    var method = flow.GetType().GetMethod("StopBehaviour");
                    if (method != null) method.Invoke(flow, null);
                } 
                catch {}
                flow.enabled = false;
            }
        }

        /// <summary>
        /// 为Boss生成随从
        /// </summary>
        /// <param name="boss">Boss角色</param>
        /// <param name="anchor">生成锚点</param>
        /// <param name="scene">场景</param>
        private async UniTask SpawnMinionsForBoss(CharacterMainControl boss, Vector3 anchor, int scene)
        {
            if (boss == null) return;

            var presets = GameplayDataSettings.CharacterRandomPresetData.presets;
            if (presets == null) return;

            var minionPresets = presets
                .Where(p => p != null && 
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.BossCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.MerchantCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.PetCharacterIcon &&
                       !p.name.ToLower().Contains("pet") &&
                       !p.name.ToLower().Contains("dog") &&
                       !p.name.ToLower().Contains("cat") &&
                       !p.name.ToLower().Contains("animal") &&
                       !p.name.ToLower().Contains("companion"))
                .ToList();

            if (minionPresets.Count == 0) return;

            int count = ModConfig.Instance.BossMinionCount;
            count = Mathf.Clamp(count, 1, 10); // 下限降到 1：尊重配置，避免至少 3 个随从的固定开销

            // 批量生成以减少每帧开销
            for (int i = 0; i < count; i++)
            { 
                 var preset = minionPresets[UnityEngine.Random.Range(0, minionPresets.Count)];
                 
                 Vector3 bossPos = boss.transform.position;
                 var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(3f, 8f);
                 Vector3 spawnPos = bossPos + new Vector3(offset.x, 0, offset.y);
                 
                 if (AstarPath.active != null)
                 {
                     var nn = Pathfinding.NNConstraint.Walkable;
                     var node = AstarPath.active.GetNearest(spawnPos, nn).node;
                     if (node != null && node.Walkable)
                     {
                         spawnPos = (Vector3)node.position;
                     }
                 }
                 else if (Physics.Raycast(spawnPos + Vector3.up * 5f, Vector3.down, out var groundHit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                 {
                     spawnPos = groundHit.point;
                 }

                 try
                 {
                     var groupObj = new GameObject("BloodMoon_SpawnerGroup");
                     var group = groupObj.AddComponent<CharacterSpawnerGroup>();
                     CharacterMainControl clone = null!;
                     try
                     {
                         clone = await preset.CreateCharacterAsync(spawnPos, Vector3.forward, scene, group, false);
                     }
                     finally
                     {
                         if (groupObj != null) UnityEngine.Object.Destroy(groupObj);
                     }
                     if (clone != null)
                     {
                         // 减少等待时间
                         await UniTask.Yield(PlayerLoopTiming.Update);
                         
                         if (clone == null) continue;
                         
                         if (IsPetOrNPC(clone))
                         {
                             BloodMoon.Utils.Logger.Warning($"[BossManager] Skipping pet/NPC minion: {clone.name}");
                             if (clone != null && clone.gameObject != null)
                             {
                                 UnityEngine.Object.Destroy(clone.gameObject);
                             }
                             continue;
                         }
                         
                         // 快速初始化
                         var anim = clone.GetComponent<Animator>();
                         if (anim != null && !anim.isInitialized) 
                         {
                             // 不再长时间等待，而是让它在后台初始化
                             // await UniTask.Delay(200); 
                         }

                         var charItem = clone.CharacterItem;
                         if (charItem == null) continue;

                         DisableVanillaAI(clone);
                         
                         Multiply(charItem, "WalkSpeed", ModConfig.Instance.MinionSpeedMultiplier);
                         Multiply(charItem, "RunSpeed", ModConfig.Instance.MinionSpeedMultiplier);
                         
                         BoostDefense(charItem, false);
                         
                         var mh = charItem.GetStat("MaxHealth".GetHashCode());
                         if (mh != null) mh.BaseValue *= ModConfig.Instance.MinionHealthMultiplier;
                         clone.Health.SetHealth(clone.Health.MaxHealth);
                         
                         FixWeight(clone, charItem);

                         clone.SetTeam(Teams.wolf);
                         
                        // 并行武器检查
                        EnsureMinionHasWeapons(clone).Forget();
                        
                        var custom = clone.gameObject.AddComponent<BloodMoonAIController>();
                        custom.Init(clone, _store);
                        custom.SetChaseDelay(0f);
                        custom.SetWingAssignment(boss, i);
                        
                        var ai = clone.GetComponent<AICharacterController>();
                        if (ai != null) ai.leader = boss;

                        _processed.Add(clone);
                        _charactersCache.Add(clone);
                        BloodMoon.Utils.Logger.Log($"[BossManager] Minion spawned: {clone.name}");
                     }
                 }
                 catch (System.Exception ex)
                 {
                     Debug.LogError($"[BloodMoon] Minion Spawn Error: {ex}");
                 }
            }
            
            AssignWingIndicesForLeader(boss);
        }

        /// <summary>
        /// 突袭结束事件处理
        /// </summary>
        /// <param name="info">突袭信息</param>
        private void OnRaidEnded(RaidUtilities.RaidInfo info)
        {
            EndBloodMoon();
            _store.Save();
            ResetSession();
        }

        /// <summary>
        /// 子场景将卸载事件处理
        /// </summary>
        /// <param name="core">多场景核心</param>
        /// <param name="scene">场景</param>
        private void OnSubSceneWillBeUnloaded(Duckov.Scenes.MultiSceneCore core, UnityEngine.SceneManagement.Scene scene)
        {
            _store.Save();
        }

        /// <summary>
        /// 多场景销毁事件处理
        /// </summary>
        /// <param name="core">多场景核心</param>
        private void OnMultiSceneDestroyed(Duckov.Scenes.MultiSceneCore core)
        {
            _store.Save();
            ResetSession();
        }

        /// <summary>
        /// 结束血月
        /// </summary>
        private void EndBloodMoon()
        {
            _bloodMoonActive = false;
            EnableDefaultSpawner();
            _store.Save();
        }

        /// <summary>
        /// 启用默认生成器
        /// </summary>
        private void EnableDefaultSpawner()
        {
            foreach (var s in _disabledSpawners)
            {
                if (s != null) s.gameObject.SetActive(true);
            }
            _disabledSpawners.Clear();
        }

        /// <summary>
        /// 禁用默认生成器（覆盖全部生成器类型：根组件 + 独立 Spawner + 组生成器）
        /// </summary>
        private void DisableDefaultSpawner()
        {
            int count = 0;
            count += DisableSpawnerType<CharacterSpawnerRoot>();
            count += DisableSpawnerType<RandomCharacterSpawner>();
            count += DisableSpawnerType<WaveCharacterSpawner>();
            count += DisableSpawnerType<CharacterSpawnerGroup>();
            count += DisableSpawnerType<CharacterSpawnerGroupSelector>();
            if (count > 0)
                BloodMoon.Utils.Logger.Log($"[BossManager] Disabled {count} default spawners.");
        }

        private int DisableSpawnerType<T>() where T : MonoBehaviour
        {
            int count = 0;
            var spawners = UnityEngine.Object.FindObjectsOfType<T>();
            if (spawners == null) return 0;
            foreach (var s in spawners)
            {
                if (s != null && s.gameObject.activeSelf)
                {
                    s.gameObject.SetActive(false);
                    if (!_disabledSpawners.Contains(s)) _disabledSpawners.Add(s);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 在玩家周围 [minDist, maxDist] 环带找导航网格可达点（Boss 生成无刷怪点时的兜底）
        /// </summary>
        private static Vector3? FindSpawnPosNearPlayer(CharacterMainControl player, float minDist, float maxDist)
        {
            if (player == null) return null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(minDist, maxDist);
                Vector3 candidate = player.transform.position + new Vector3(offset.x, 0f, offset.y);

                if (AstarPath.active != null)
                {
                    var nn = Pathfinding.NNConstraint.Walkable;
                    var node = AstarPath.active.GetNearest(candidate, nn).node;
                    if (node != null && node.Walkable)
                    {
                        candidate = (Vector3)node.position;
                        float dist = Vector3.Distance(candidate, player.transform.position);
                        if (dist >= minDist) return candidate;
                    }
                }
                else if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    return hit.point;
                }
            }
            return null;
        }

        /// <summary>
        /// 修复角色重量问题
        /// </summary>
        /// <param name="c">角色控制器</param>
        /// <param name="item">角色物品</param>
        private void FixWeight(CharacterMainControl c, Item item)
        {
            if (c == null || item == null) return;

            var mw = item.GetStat("MaxWeight".GetHashCode());
            if (mw != null) mw.BaseValue = 10000f;

            var cap = item.GetStat("InventoryCapacity".GetHashCode());
            if (cap != null) cap.BaseValue = 200f;

            c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
            
            c.UpdateWeightState();
        }

        /// <summary>
        /// 增强Boss
        /// </summary>
        /// <param name="c">Boss角色</param>
        private void EnhanceBoss(CharacterMainControl c)
        {
            DisableVanillaAI(c);
            
            try
            {
                var item = c.CharacterItem;
                if (item == null) return;
                var maxHealth = item.GetStat("MaxHealth".GetHashCode());
                if (maxHealth != null) maxHealth.BaseValue *= ModConfig.Instance.BossHealthMultiplier;
                
                Multiply(item, "WalkSpeed", ModConfig.Instance.BossSpeedMultiplier);
                Multiply(item, "RunSpeed", ModConfig.Instance.BossSpeedMultiplier);
                Multiply(item, "TurnSpeed", ModConfig.Instance.BossSpeedMultiplier);
                BoostDefense(item, true);
                
                FixWeight(c, item);
                
                c.Health.SetHealth(c.Health.MaxHealth);
                
                var custom = c.GetComponent<BloodMoonAIController>();
                if (custom == null)
                {
                    custom = c.gameObject.AddComponent<BloodMoonAIController>();
                    custom.Init(c, _store);
                    custom.SetChaseDelay(0f);
                }
                if (c.Team != Teams.wolf) c.SetTeam(Teams.wolf);
                if (ModConfig.Instance.EnableBossGlow) AddBossGlow(c);

                AddRandomLoot(c).Forget();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] EnhanceBoss Error: {e}");
            }
        }

        /// <summary>
        /// 为Boss添加随机战利品
        /// </summary>
        /// <param name="c">Boss角色</param>
        private async UniTaskVoid AddRandomLoot(CharacterMainControl c)
        {
            if (c == null || c.CharacterItem == null || c.CharacterItem.Inventory == null) return;

            try
            {
                var filter = new ItemFilter
                {
                    minQuality = ModConfig.Instance.BossLootMinQuality,
                    maxQuality = ModConfig.Instance.BossLootMaxQuality
                };

                int[]? ids = null;
                try
                {
                    ids = ItemAssetsCollection.Search(filter);
                }
                catch {}
                
                if (ids != null && ids.Length > 0)
                {
                    int count = UnityEngine.Random.Range(ModConfig.Instance.BossLootMinCount, ModConfig.Instance.BossLootMaxCount + 1);
                    for (int i = 0; i < count; i++)
                    {
                        int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (!c.CharacterItem.Inventory.AddAndMerge(item))
                            {
                                item.Drop(c.transform.position, true, Vector3.up, 360f);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] Add Loot Error: {e}");
            }
        }

        /// <summary>
        /// 为Boss添加发光效果
        /// </summary>
        /// <param name="c">Boss角色</param>
        private void AddBossGlow(CharacterMainControl c)
        {
            var lightObj = new GameObject("BossGlowLight");
            lightObj.transform.SetParent(c.transform);
            lightObj.transform.localPosition = Vector3.up * 1.5f;
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.1f, 0.1f);
            light.range = ModConfig.Instance.BossGlowRange;
            light.intensity = ModConfig.Instance.BossGlowIntensity;
            light.shadows = LightShadows.Soft;

            var renderers = c.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                
                var mats = r.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat.HasProperty(EmissionColorId))
                    {
                        mat.EnableKeyword(EmissionKeyword);
                        mat.SetColor(EmissionColorId, new Color(0.8f, 0.1f, 0.1f) * 2f);
                    }
                }
            }
        }

        /// <summary>
        /// 乘以属性值
        /// </summary>
        /// <param name="item">角色物品</param>
        /// <param name="stat">属性名</param>
        /// <param name="m">乘数</param>
        private void Multiply(Item item, string stat, float m)
        {
            var s = item.GetStat(stat.GetHashCode());
            if (s != null) s.BaseValue *= m;
        }

        /// <summary>
        /// 增加库存容量
        /// </summary>
        /// <param name="item">角色物品</param>
        /// <param name="amount">增加的容量</param>
        private void IncreaseInventoryCapacity(Item item, int amount)
        {
            if (item == null) return;
            var stat = item.GetStat("InventoryCapacity".GetHashCode());
            if (stat != null)
            {
                stat.BaseValue += amount;
                if (item.Inventory != null)
                {
                    item.Inventory.SetCapacity(Mathf.RoundToInt(stat.BaseValue));
                }
            }
            else if (item.Inventory != null)
            {
                 item.Inventory.SetCapacity(item.Inventory.Capacity + amount);
            }
        }

        /// <summary>
        /// 启用复仇机制
        /// </summary>
        /// <param name="c">Boss角色</param>
        /// <param name="player">玩家</param>
        private void EnableRevenge(CharacterMainControl c, CharacterMainControl? player)
        {
            if (player == null || c == null) return;
            
            if (player.mainDamageReceiver == null) return;

            var ai = c.GetComponent<AICharacterController>();
            if (ai != null && player.mainDamageReceiver.transform != null)
            {
                ai.SetTarget(player.mainDamageReceiver.transform);
                ai.forceTracePlayerDistance = 100f;
            }
            
            var custom = c.GetComponent<BloodMoonAIController>();
            if (custom != null && custom.CanChase == false)
            {
                custom.SetChaseDelay(0f);
            }
            
            string[] taunts = { "Boss_Revenge", "Boss_Taunt_1", "Boss_Taunt_2", "Boss_Taunt_3" };
            string key = taunts[UnityEngine.Random.Range(0, taunts.Length)];
            
            try
            {
                c.PopText(Localization.Get(key));
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Warning($"[BossManager] Failed to show taunt text: {ex.Message}");
            }
        }

        /// <summary>
        /// 增强防御
        /// </summary>
        /// <param name="item">角色物品</param>
        /// <param name="isBoss">是否是Boss</param>
        private void BoostDefense(Item item, bool isBoss)
        {
            var body = item.GetStat("BodyArmor".GetHashCode());
            var head = item.GetStat("HeadArmor".GetHashCode());
            float bodyTarget = isBoss ? ModConfig.Instance.BossBodyArmor : ModConfig.Instance.MinionBodyArmor;
            float headTarget = isBoss ? ModConfig.Instance.BossHeadArmor : ModConfig.Instance.MinionHeadArmor;
            if (body != null) body.BaseValue = Mathf.Max(body.BaseValue, bodyTarget);
            if (head != null) head.BaseValue = Mathf.Max(head.BaseValue, headTarget);
        }
        
        /// <summary>
        /// 确保随从有武器
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask EnsureMinionHasWeapons(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            bool hasMelee = false;
            bool hasGun = false;
            
            if (character.MeleeWeaponSlot()?.Content != null) hasMelee = true;
            if (character.PrimWeaponSlot()?.Content != null) hasGun = true;
            if (character.SecWeaponSlot()?.Content != null) hasGun = true;
            
            if (hasMelee && hasGun) return;
            
            if (!hasMelee)
            {
                await TryAddMeleeWeapon(character);
            }
            
            if (!hasGun)
            {
                await TryAddGun(character);
            }
        }
        
        /// <summary>
        /// 尝试添加近战武器
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask TryAddMeleeWeapon(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            try
            {
                if (character.MeleeWeaponSlot()?.Content == null)
                {
                    var item = await BloodMoon.AI.EnhancedWeaponManager.Instance.SpawnRandomMeleeWeapon();
                    if (item != null)
                    {
                        var slot = character.MeleeWeaponSlot();
                        if (slot != null && slot.CanPlug(item))
                        {
                            if (character.CharacterItem.Inventory.AddAndMerge(item))
                            {
                                slot.Plug(item, out var _);
                                return; 
                            }
                        }
                        UnityEngine.Object.Destroy(item.gameObject);
                    }
                    else
                    {
                        Debug.LogWarning($"[BloodMoon] Minion missing melee weapon: {character.name} (All searches failed)");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BloodMoon] Failed to add melee weapon: {ex}");
            }
        }
        
        /// <summary>
        /// 尝试添加枪械
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask TryAddGun(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            try
            {
                if (character.PrimWeaponSlot()?.Content == null && character.SecWeaponSlot()?.Content == null)
                {
                    var item = await BloodMoon.AI.EnhancedWeaponManager.Instance.SpawnRandomGun();
                    if (item != null)
                    {
                        var pSlot = character.PrimWeaponSlot();
                        var sSlot = character.SecWeaponSlot();
                        
                        bool added = character.CharacterItem.Inventory.AddAndMerge(item);
                        if (added)
                        {
                            if (pSlot != null && pSlot.CanPlug(item))
                            {
                                pSlot.Plug(item, out var _);
                                await AddAmmoForGun(character, item);
                                return;
                            }
                            else if (sSlot != null && sSlot.CanPlug(item))
                            {
                                sSlot.Plug(item, out var _);
                                await AddAmmoForGun(character, item);
                                return;
                            }
                        }
                        if (!added) UnityEngine.Object.Destroy(item.gameObject);
                    }
                    else
                    {
                        Debug.LogWarning($"[BloodMoon] Minion missing guns: {character.name} (All searches failed)");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BloodMoon] Failed to add gun: {ex}");
            }
        }

        /// <summary>
        /// 为枪械添加弹药
        /// </summary>
        /// <param name="c">角色控制器</param>
        /// <param name="gun">枪械</param>
        private async UniTask AddAmmoForGun(CharacterMainControl c, Item gun)
        {
            await BloodMoon.AI.EnhancedWeaponManager.Instance.EnsureAmmo(c, gun);
        }

        /// <summary>
        /// 为领袖分配侧翼索引
        /// </summary>
        /// <param name="boss">Boss角色</param>
        private void AssignWingIndicesForLeader(CharacterMainControl boss)
        {
            var controllers = new List<AICharacterController>();
            var all = _store.AllCharacters;
            int count = all.Count;
            for(int i=0; i<count; i++) {
                var c = all[i];
                if (c == null) continue;
                var ai = c.GetComponent<AICharacterController>();
                if (ai != null) controllers.Add(ai);
            }
            
            var followers = new List<CharacterMainControl>();
            foreach (var ai in controllers)
            {
                if (ai == null) continue;
                if (ai.leader == boss)
                {
                    var ch = ai.GetComponent<CharacterMainControl>();
                    if (ch != null) followers.Add(ch);
                }
            }
            try
            {
                string id = boss.characterPreset != null ? boss.characterPreset.name : boss.name;
                
                float armorBody = 0f;
                var body = boss.CharacterItem?.GetStat("BodyArmor".GetHashCode());
                if (body != null) armorBody = body.BaseValue;
                bool heavy = armorBody >= 20f;
                float baseR = heavy ? 4.4f : 3.2f;
                float ang = heavy ? 36f : 28f;
                float sp = heavy ? 1.5f : 1.2f;
                _store.SetLeaderPrefBaseline(id, baseR, ang, sp);
            }
            catch { }
            followers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            for (int i = 0; i < followers.Count; i++)
            {
                var f = followers[i];
                if (f == null) continue;
                
                var ctrl = f.gameObject.GetComponent<BloodMoonAIController>();
                if (ctrl == null)
                {
                    ctrl = f.gameObject.AddComponent<BloodMoonAIController>();
                    ctrl.Init(f, _store);
                    ctrl.SetChaseDelay(0f);
                }
                ctrl.SetWingAssignment(boss, i);
            }
        }

        /// <summary>
        /// 开始场景设置
        /// </summary>
        public void StartSceneSetupParallel()
        {
            if (!_initialized) return;
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap || LevelManager.Instance.IsBaseLevel) return;
            
            if (!_setupRunning && !_sceneSetupDone) 
            {
                 // 用游戏时钟判断血月是否已超时（之前误用 Time.time 现实秒，几乎永不触发）
                 if (_bloodMoonActive && (GameClock.Now - _bloodMoonStartGameTime).TotalHours > ModConfig.Instance.ActiveHours)
                 {
                     ResetSession();
                 }
            }

            if (_setupRunning) return;
            _setupRunning = true;

            _bloodMoonStartTime = Time.time;
            _bloodMoonStartGameTime = GameClock.Now;
            _bloodMoonActive = true;
            _strategyDecayTimer = 30f;

            UniTask.Void(async () =>
            {
                await UniTask.Delay(3000); 

                await UniTask.Yield();

                _pointsCache.Clear();
                _disabledSpawners.Clear();

                // 注意顺序：必须先收集刷怪点，再禁用生成器！
                // （生成器被 SetActive(false) 后 FindObjectsOfType 找不到 → _pointsCache 为空
                //  → Boss 锚点 fallback 到玩家脚下 → 敌人全刷在玩家身边）
                var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                foreach (var s in spawners) 
                {
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                }
                
                await UniTask.Yield();

                var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
                foreach (var s in waveSpawners) 
                {
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                }

                // 收集完成后才禁用生成器（血月期间不再刷怪）
                if (ModConfig.Instance.DisableDefaultSpawners) DisableDefaultSpawner();

                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                try 
                {
                    if (CharacterMainControl.Main == null)
                    {
                        // 带超时等待玩家角色：避免永久挂起导致血月内容永不生成
                        float waitTimeout = 10f;
                        while (CharacterMainControl.Main == null && waitTimeout > 0f)
                        {
                            await UniTask.Delay(500);
                            waitTimeout -= 0.5f;
                        }
                    }
                    
                    var player = CharacterMainControl.Main;
                    if (player == null)
                    {
                         _setupRunning = false;
                         return;
                    }

                    int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : SceneManager.GetActiveScene().buildIndex;
                    _processed.Clear();
                    
                    _charactersCache.Clear();
                    _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());

                    _currentScene = scene;
                    _sceneSetupDone = false;
                    
                    var availableScenes = new List<int>();
                    foreach(var p in _pointsCache)
                    {
                        if (p != null)
                        {
                            int sIdx = p.gameObject.scene.buildIndex;
                            if (!availableScenes.Contains(sIdx)) availableScenes.Add(sIdx);
                        }
                    }
                    if (availableScenes.Count == 0) availableScenes.Add(scene);
                    
                    var allBossPresets = GameplayDataSettings.CharacterRandomPresetData.presets
                        .Where(p => p != null && 
                               p.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon &&
                               !p.name.ToLower().Contains("pet") &&
                               !p.name.ToLower().Contains("dog") &&
                               !p.name.ToLower().Contains("cat") &&
                               !p.name.ToLower().Contains("animal") &&
                               !p.name.ToLower().Contains("companion"))
                        .ToArray();

                    CharacterRandomPreset[] selected = System.Array.Empty<CharacterRandomPreset>();

                    var rnd = new System.Random();
                    int bossCount = ModConfig.Instance.BossCount;
                    bossCount = Mathf.Clamp(bossCount, 1, 5);
                    selected = allBossPresets.OrderBy(_ => rnd.Next()).Take(bossCount).ToArray();

                    _selectedBossPresets.Clear();
                    _selectedBossPresets.AddRange(selected);
                    _selectedScene = scene;
                    _selectionInitialized = true;

                    int sceneCursor = 0;
                    foreach (var preset in _selectedBossPresets)
                    {
                        bool exists = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>().Any(c => c.characterPreset == preset);
                        if (!exists)
                        {
                            int targetScene = availableScenes[sceneCursor % availableScenes.Count];
                            sceneCursor++;
                            
                            Vector3 anchor = Vector3.zero;
                            bool found = false;
                            
                            if (_pointsCache != null && _pointsCache.Count > 0)
                            {
                                try 
                                {
                                    var pt = _pointsCache[UnityEngine.Random.Range(0, _pointsCache.Count)];
                                    if (pt != null)
                                    {
                                        anchor = pt.GetRandomPoint();
                                        targetScene = pt.gameObject.scene.buildIndex;
                                        found = true;
                                    }
                                }
                                catch {}
                            }
                            
                            if (!found)
                            {
                                // 没有刷怪点可用时：在玩家周围 40-90m 找导航网格可达点。
                                // 旧版直接 fallback 到玩家位置 → Boss 刷在玩家脚下（玩家身边刷怪的根因）
                                var nearPos = FindSpawnPosNearPlayer(player, 40f, 90f);
                                if (nearPos == null)
                                {
                                    BloodMoon.Utils.Logger.Warning("[BossManager] No valid spawn position for boss, skipping.");
                                    continue;
                                }
                                anchor = nearPos.Value;
                                targetScene = scene;
                            }
                            
                            try
                            {
                                var groupObj = new GameObject("BloodMoon_SpawnerGroup");
                                var group = groupObj.AddComponent<CharacterSpawnerGroup>();
                                CharacterMainControl clone = null!;
                                try
                                {
                                    clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, targetScene, group, false);
                                }
                                finally
                                {
                                    if (groupObj != null) UnityEngine.Object.Destroy(groupObj);
                                }
                                if (clone != null)
                                {
                                    await UniTask.Yield(PlayerLoopTiming.Update); 
                                    await UniTask.Delay(1000); 
                                    
                                    if (clone == null) continue; 
                                    
                                    if (IsPetOrNPC(clone))
                                    {
                                        BloodMoon.Utils.Logger.Warning($"[BossManager] Skipping pet/NPC character: {clone.name}");
                                        if (clone != null && clone.gameObject != null)
                                        {
                                            UnityEngine.Object.Destroy(clone.gameObject);
                                        }
                                        continue;
                                    }
                                    
                                    var anim = clone.GetComponent<Animator>();
                                    if (anim == null || !anim.isInitialized)
                                    {
                                         await UniTask.Delay(500);
                                    }

                                    EnhanceBoss(clone);
                                    EnableRevenge(clone, player);
                                    SpawnMinionsForBoss(clone, anchor, targetScene).Forget();
                                    _processed.Add(clone);
                                    _minionSpawned.Add(clone);
                                    _groupAnchors[clone] = anchor;
                                    _charactersCache.Add(clone);
                                    BloodMoon.Utils.Logger.Log($"[BossManager] Boss spawned: {clone.name} at {clone.transform.position}");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"[BloodMoon] Create Boss Error: {ex}");
                            }
                    }
                }

                _sceneSetupDone = true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] Setup Failed: {e}");
            }
            finally
            {
                _setupRunning = false;
            }
        });
        }
        
        /// <summary>
        /// 检查角色是否是宠物或NPC
        /// </summary>
        /// <param name="character">角色控制器</param>
        /// <returns>如果是宠物或NPC则返回true</returns>
        private bool IsPetOrNPC(CharacterMainControl character)
        {
            if (character == null) return false;
            
            try
            {
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Checking if character {character.name} is pet/NPC");
                }
                
                var preset = character.characterPreset;
                if (preset != null)
                {
                    var icon = preset.GetCharacterIcon();
                    if (icon != null)
                    {
                        var petIcon = GameplayDataSettings.UIStyle.PetCharacterIcon;
                        if (petIcon != null && icon == petIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is a pet (pet icon detected)");
                            return true;
                        }
                        
                        var merchantIcon = GameplayDataSettings.UIStyle.MerchantCharacterIcon;
                        if (merchantIcon != null && icon == merchantIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is a merchant (merchant icon detected)");
                            return true;
                        }
                        
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} icon check passed (not pet/merchant)");
                        }
                    }
                    else
                    {
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no icon");
                        }
                    }
                }
                else
                {
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no characterPreset");
                    }
                }
                
                string characterName = character.name.ToLower();
                string[] petKeywords = { "pet", "dog", "cat", "animal", "companion" };
                string[] npcKeywords = { "npc", "merchant", "trader", "quest", "civilian" };
                
                foreach (var keyword in petKeywords)
                {
                    if (characterName.Contains(keyword))
                    {
                        BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is likely a pet (name contains '{keyword}')");
                        return true;
                    }
                }
                
                foreach (var keyword in npcKeywords)
                {
                    if (characterName.Contains(keyword))
                    {
                        BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is likely an NPC (name contains '{keyword}')");
                        return true;
                    }
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} name check passed (no pet/NPC keywords)");
                }
                
                var petAI = character.GetComponent<PetAI>();
                if (petAI != null)
                {
                    BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} has PetAI component");
                    return true;
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} component check passed (no PetAI)");
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} is NOT a pet/NPC - allowing generation");
                }
                
                return false;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[BossManager] Error checking if character is pet/NPC: {ex.Message}");
                return false;
            }
        }
    }
}
