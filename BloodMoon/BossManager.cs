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
    public class BossManager
    {
        private readonly AIDataStore _store;
        private bool _initialized;
        private readonly HashSet<CharacterMainControl> _processed = new HashSet<CharacterMainControl>();
        
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
        private float _bloodMoonDurationSec => ModConfig.Instance.ActiveHours * 3600f; // 配置是以小时为单位？不，可能是游戏小时。 

        
        private bool _bloodMoonActive;
        private float _strategyDecayTimer;
        private readonly List<CharacterMainControl> _charactersCache = new List<CharacterMainControl>();
        private List<MonoBehaviour> _disabledSpawners = new List<MonoBehaviour>();



        private void DisableVanillaAI(CharacterMainControl c)
        {
            if (c == null || c.IsMainCharacter) return;
            
            var ai = c.GetComponent<AICharacterController>();
            if (ai != null)
            {
                if (ai.enabled) ai.enabled = false;
            }
            
            // 额外安全措施：尝试通过反射禁用NodeCanvas Owner
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

            // 如果存在也禁用FlowScriptController
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

        public BossManager(AIDataStore store)
        {
            _store = store;
        }

        public void Initialize()
        {
            if (_initialized) return;
            RaidUtilities.OnRaidEnd += OnRaidEnded;
            RaidUtilities.OnRaidDead += OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded += OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy += OnMultiSceneDestroyed;
            _initialized = true;
        }

        public void Dispose()
        {
            RaidUtilities.OnRaidEnd -= OnRaidEnded;
            RaidUtilities.OnRaidDead -= OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded -= OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy -= OnMultiSceneDestroyed;
            _initialized = false;
        }

        private void ResetSession()
        {
            _setupRunning = false;
            _sceneSetupDone = false;
            _bloodMoonActive = false;
            _bloodMoonStartTime = -1f;
            _processed.Clear();
            _groupAnchors.Clear();
            _pointsCache.Clear();
            _charactersCache.Clear();
            _selectedBossPresets.Clear();
            _selectionInitialized = false;
            _disabledSpawners.Clear();
        }

        private float _weightCheckTimer;

        public void Tick()
        {
            try 
            {
                if (!_initialized) return;

                if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap || LevelManager.Instance.IsBaseLevel)
                {
                    return;
                }

                // 定期重量强制执行
                _weightCheckTimer -= Time.deltaTime;
                if (_weightCheckTimer <= 0f)
                {
                    _weightCheckTimer = 10.0f; // 每10秒强制修复以抵抗系统重置
                    foreach(var c in _processed)
                    {
                        if (c != null && c.gameObject.activeInHierarchy && c.Health.CurrentHealth > 0)
                        {
                            FixWeight(c, c.CharacterItem);
                        }
                    }
                }

                // 如果活动且未设置则初始化开始时间，或者如果过期且是新突袭则重置？
                // 目前，我们依赖StartSceneSetupParallel来设置突袭的开始时间。
                
                _scanCooldown -= Time.deltaTime;
                if (_scanCooldown > 0f)
                {
                    return;
                }
                var player = CharacterMainControl.Main;
                if (!_sceneSetupDone || _setupRunning)
                {
                    // 等待设置完成
                    return;
                }
                
                // 集中清理（每几秒一次）
                if (_scanCooldown > 0.9f) // 每个扫描周期执行一次
                {
                     _store.DecayAndPrune(Time.time, 120f);
                     
                     // 备份：如果缓存为空，尝试查找点
                     if (_pointsCache.Count == 0)
                     {
                        var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                        foreach (var s in spawners) { if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints); }
                     }
                }

                // 使用集中存储缓存
                var all = _store.AllCharacters;
                int count = all.Count;
                
                for (int i = 0; i < count; i++)
                {
                    var c = all[i];
                    if (c == null || c.IsMainCharacter) continue;
                    
                    // 优化：在执行重量级检查之前检查是否已处理
                    if (_processed.Contains(c)) continue;

                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (!isBoss) continue;
                    
                    if (_selectionInitialized && preset != null && !_selectedBossPresets.Contains(preset))
                    {
                        continue;
                    }
                    
                    EnhanceBoss(c);
                    // SetupBossLocation(c); // 已移除
                    EnableRevenge(c, player);
                    SpawnMinionsForBoss(c, c.transform.position, c.gameObject.scene.buildIndex).Forget();
                    _processed.Add(c);
                }
                
                _scanCooldown = 1.0f;
                
                _strategyDecayTimer -= Time.deltaTime;
                if (_strategyDecayTimer <= 0f)
                {
                    _store.DecayWeights(0.98f);
                    _strategyDecayTimer = 30f;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] Tick Error: {e}");
            }
        }

        private async UniTask SpawnMinionsForBoss(CharacterMainControl boss, Vector3 anchor, int scene)
        {
            if (boss == null) return;

            var minionPresets = GameplayDataSettings.CharacterRandomPresetData.presets
                .Where(p => p != null && 
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.BossCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.MerchantCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.PetCharacterIcon)
                .ToList();

            if (minionPresets.Count == 0) return;

            int count = ModConfig.Instance.BossMinionCount;
            count = Mathf.Clamp(count, 3, 10); 

            for (int i = 0; i < count; i++)
            { 
                 var preset = minionPresets[UnityEngine.Random.Range(0, minionPresets.Count)];
                 
                // 始终在Boss附近生成随从以确保它们在一起
                 Vector3 spawnPos = anchor;
                 
                // 使用Boss当前位置作为锚点，而不是初始生成点
                 Vector3 bossPos = boss.transform.position;
                 
                // 在Boss附近生成随机偏移（3-8米）
                 var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(3f, 8f);
                 spawnPos = bossPos + new Vector3(offset.x, 0, offset.y);
                 
                 // 使用A* Pathfinding Project寻找最近的有效节点
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
                     var clone = await preset.CreateCharacterAsync(spawnPos, Vector3.forward, scene, null, false);
                     if (clone != null)
                     {
                         // 等待初始化（Animator、MagicBlend等）
                         await UniTask.Yield(PlayerLoopTiming.Update);
                         await UniTask.Delay(500); 
                         
                         if (clone == null) continue;
                         
                         // 检查是否是宠物或NPC，如果是则跳过
                         if (IsPetOrNPC(clone))
                         {
                             BloodMoon.Utils.Logger.Warning($"[BossManager] Skipping pet/NPC minion: {clone.name}");
                             // 销毁这个角色
                             if (clone != null && clone.gameObject != null)
                             {
                                 UnityEngine.Object.Destroy(clone.gameObject);
                             }
                             continue;
                         }
                         
                         var anim = clone.GetComponent<Animator>();
                         if (anim != null && !anim.isInitialized) await UniTask.Delay(200);

                         var charItem = clone.CharacterItem;
                         if (charItem == null) continue;

                         DisableVanillaAI(clone);
                         
                         Multiply(charItem, "WalkSpeed", 1.2f);
                         Multiply(charItem, "RunSpeed", 1.2f);
                         
                         BoostDefense(charItem, false);
                         
                         var mh = charItem.GetStat("MaxHealth".GetHashCode());
                         if (mh != null) mh.BaseValue *= ModConfig.Instance.MinionHealthMultiplier;
                         clone.Health.SetHealth(clone.Health.MaxHealth);
                         
                         // 修复随从的重量问题
                         FixWeight(clone, charItem);

                         clone.SetTeam(Teams.wolf);
                        
                        // 确保随从有武器
                        await EnsureMinionHasWeapons(clone);
                        
                        var custom = clone.gameObject.AddComponent<BloodMoonAIController>();
                        custom.Init(clone, _store);
                        custom.SetChaseDelay(0f);
                        custom.SetWingAssignment(boss, i);
                        
                        var ai = clone.GetComponent<AICharacterController>();
                        if (ai != null) ai.leader = boss;

                        _processed.Add(clone);
                        _charactersCache.Add(clone);
                     }
                 }
                 catch (System.Exception ex)
                 {
                     Debug.LogError($"[BloodMoon] Minion Spawn Error: {ex}");
                 }
            }
            
            AssignWingIndicesForLeader(boss);
        }

        private void OnRaidEnded(RaidUtilities.RaidInfo info)
        {
            EndBloodMoon();
            _store.Save();
            ResetSession();
        }

        private void OnSubSceneWillBeUnloaded(Duckov.Scenes.MultiSceneCore core, UnityEngine.SceneManagement.Scene scene)
        {
            _store.Save();
        }

        private void OnMultiSceneDestroyed(Duckov.Scenes.MultiSceneCore core)
        {
            _store.Save();
            ResetSession();
        }

        private void EndBloodMoon()
        {
            _bloodMoonActive = false;
            EnableDefaultSpawner();
            _store.Save();
        }

        private void EnableDefaultSpawner()
        {
            foreach (var s in _disabledSpawners)
            {
                if (s != null) s.gameObject.SetActive(true);
            }
            _disabledSpawners.Clear();
        }

        private void DisableDefaultSpawner()
        {
            // 禁用默认生成器系统以防止新波次生成
            var spawners = UnityEngine.Object.FindObjectsOfType<CharacterSpawnerRoot>();
            if (spawners != null)
            {
                foreach (var s in spawners)
                {
                    if (s.gameObject.activeSelf)
                    {
                        s.gameObject.SetActive(false);
                        if (!_disabledSpawners.Contains(s)) _disabledSpawners.Add(s);
                    }
                }
            }
        }

        private void FixWeight(CharacterMainControl c, Item item)
        {
            if (c == null || item == null) return;

            // 1. 设置负重
            var mw = item.GetStat("MaxWeight".GetHashCode());
            if (mw != null) mw.BaseValue = 10000f; // 10000kg

            // 2. 设置大量库存容量
            var cap = item.GetStat("InventoryCapacity".GetHashCode());
            if (cap != null) cap.BaseValue = 200f; // 200 slots

            // 3. 移除现有的超重增益效果
            c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
            
            // 4. 强制更新权重状态
            c.UpdateWeightState();

            // 5. 再次应用行走/奔跑速度倍率，以防重量系统覆盖它们
            // 通常重量会按百分比降低速度，因此提升基础速度是有帮助的
            // 但如果最大重量为10000，当前重量应为0%负载，因此不会产生惩罚。
        }

        private void EnhanceBoss(CharacterMainControl c)
        {
            // 立即禁用原生AI，以防止在异步设置期间发生逻辑冲突
            DisableVanillaAI(c);
            
            try
            {
                var item = c.CharacterItem;
                if (item == null) return;
                var maxHealth = item.GetStat("MaxHealth".GetHashCode());
                if (maxHealth != null) maxHealth.BaseValue *= ModConfig.Instance.BossHealthMultiplier;
                
                Multiply(item, "WalkSpeed", 1.35f);
                Multiply(item, "RunSpeed", 1.35f);
                Multiply(item, "TurnSpeed", 1.35f);
                BoostDefense(item, true);
                
                // 解决体重问题
                FixWeight(c, item);
                
                c.Health.SetHealth(c.Health.MaxHealth);
                
                // 确保AI与主管关联，若主管未在场则进行关联
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

        private async UniTaskVoid AddRandomLoot(CharacterMainControl c)
        {
            if (c == null || c.CharacterItem == null || c.CharacterItem.Inventory == null) return;

            try
            {
                // 搜索高质量物品（质量等级4+）
                // Search() 会在请求级别未找到任何项目时自动降低质量。
                var filter = new ItemFilter
                {
                    minQuality = 4,
                    maxQuality = 10
                };

                int[]? ids = null;
                try
                {
                    ids = ItemAssetsCollection.Search(filter);
                }
                catch {}
                
                if (ids != null && ids.Length > 0)
                {
                    // 添加1-3件随机高质量物品
                    int count = UnityEngine.Random.Range(1, 4);
                    for (int i = 0; i < count; i++)
                    {
                        int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (!c.CharacterItem.Inventory.AddAndMerge(item))
                            {
                                // 若库存已满，则将其直接放置于领袖脚下
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

        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly string EmissionKeyword = "_EMISSION";

        private void AddBossGlow(CharacterMainControl c)
        {
            // 添加点光源
            var lightObj = new GameObject("BossGlowLight");
            lightObj.transform.SetParent(c.transform);
            lightObj.transform.localPosition = Vector3.up * 1.5f;
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.1f, 0.1f);
            light.range = 6.0f;
            light.intensity = 4.0f;
            light.shadows = LightShadows.Soft;

            // 修改材料以实现排放
            // 优化：仅在 boss 初始化时运行一次
            var renderers = c.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                
                // 使用.materials 创建一个唯一实例，确保我们仅使这个特定 Boss 发光
                // 且不存在其他具有相同模型的敌人。
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

        private void Multiply(Item item, string stat, float m)
        {
            var s = item.GetStat(stat.GetHashCode());
            if (s != null) s.BaseValue *= m;
        }

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





        private void EnableRevenge(CharacterMainControl c, CharacterMainControl? player)
        {
            if (player == null || c == null) return;
            
            // 安全检查
            if (player.mainDamageReceiver == null) return;

            var ai = c.GetComponent<AICharacterController>();
            if (ai != null && player.mainDamageReceiver.transform != null)
            {
                ai.SetTarget(player.mainDamageReceiver.transform);
                ai.forceTracePlayerDistance = 100f;
            }
            
            // 确保AI已初始化（应由EnhanceBoss处理，但需进行双重检查）
            var custom = c.GetComponent<BloodMoonAIController>();
            if (custom != null && custom.CanChase == false)
            {
                custom.SetChaseDelay(0f);
            }
            
            // 随机嘲讽
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







        private void BoostDefense(Item item, bool isBoss)
        {
            var body = item.GetStat("BodyArmor".GetHashCode());
            var head = item.GetStat("HeadArmor".GetHashCode());
            float bodyTarget = isBoss ? ModConfig.Instance.BossBodyArmor : ModConfig.Instance.MinionBodyArmor;
            float headTarget = isBoss ? ModConfig.Instance.BossHeadArmor : ModConfig.Instance.MinionHeadArmor;
            if (body != null) body.BaseValue = Mathf.Max(body.BaseValue, bodyTarget);
            if (head != null) head.BaseValue = Mathf.Max(head.BaseValue, headTarget);
        }
        
        private async UniTask EnsureMinionHasWeapons(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            bool hasMelee = false;
            bool hasGun = false;
            
            // 检查角色是否已拥有武器
            if (character.MeleeWeaponSlot()?.Content != null) hasMelee = true;
            if (character.PrimWeaponSlot()?.Content != null) hasGun = true;
            if (character.SecWeaponSlot()?.Content != null) hasGun = true;
            
            // 如果角色同时具备近战和枪械能力，则符合要求
            if (hasMelee && hasGun) return;
            
            // 尝试添加近战武器（若缺失）
            if (!hasMelee)
            {
                await TryAddMeleeWeapon(character);
            }
            
            // 若缺失，请尝试添加枪支
            if (!hasGun)
            {
                await TryAddGun(character);
            }
        }
        
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

        private async UniTask AddAmmoForGun(CharacterMainControl c, Item gun)
        {
            await BloodMoon.AI.EnhancedWeaponManager.Instance.EnsureAmmo(c, gun);
        }











        private void AssignWingIndicesForLeader(CharacterMainControl boss)
        {
            // 优化：使用存储中的缓存字符
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
                // 使用持久ID（预设名称）而非瞬态InstanceID
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
                if (f == null) continue; // 安全检查
                
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



        

        

        

        

        






        public void StartSceneSetupParallel()
        {
            if (!_initialized) return;
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap || LevelManager.Instance.IsBaseLevel) return;
            
            // 在开始前确保处于清洁状态
            if (!_setupRunning && !_sceneSetupDone) 
            {
                 if (_bloodMoonActive && Time.time - _bloodMoonStartTime > _bloodMoonDurationSec)
                 {
                     ResetSession();
                 }
            }

            if (_setupRunning) return;
            _setupRunning = true;

            _bloodMoonStartTime = Time.time;
            _bloodMoonActive = true;
            _strategyDecayTimer = 30f;

            UniTask.Void(async () =>
            {
                // 等待关卡初始化完成（避免与游戏生成器的竞态条件）
                await UniTask.Delay(3000); 

                await UniTask.Yield();

                // --- 同步初始化阶段 ---
                // 立即捕获并禁用原生生成器以防止干扰
                _pointsCache.Clear();
                _disabledSpawners.Clear();

                // 1. 捕获生成点并限制原生生成器（不禁用）
                var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                foreach (var s in spawners) 
                {
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    // 限制数量以避免过度拥挤
                    // s.spawnCountRange = new Vector2Int(1, 1);
                }
                
                await UniTask.Yield();

                var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
                foreach (var s in waveSpawners) 
                {
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    // 限制数量以避免过度拥挤
                    // s.spawnCountRange = new Vector2Int(1, 1);
                }

                // -----------------------------------

                // 2. 初始地图指标计算
                // RecalculateMapMetrics(); // 已移除
                
                // 3. 初始禁用任何预先存在的原生AI
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                // 禁用Vanilla控制器缓存();
                try 
                {
                    if (CharacterMainControl.Main == null) await UniTask.WaitUntil(() => CharacterMainControl.Main != null);
                    
                    var player = CharacterMainControl.Main;
                    if (player == null)
                    {
                         _setupRunning = false;
                         return;
                    }

                    int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : SceneManager.GetActiveScene().buildIndex;
                    _processed.Clear();
                    
                    // 重新扫描角色，以防玩家出现
                    _charactersCache.Clear();
                    _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                    // 禁用Vanilla控制器缓存();

                    _currentScene = scene;
                    _sceneSetupDone = false;
                    
                    // 从生成点识别所有可用的突袭场景
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
                        .Where(p => p != null && p.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon)
                        .ToArray();

                    CharacterRandomPreset[] selected = System.Array.Empty<CharacterRandomPreset>();

                    // 在主线程上直接选择，避免后台线程访问Unity对象的潜在风险
                    var rnd = new System.Random();
                    int bossCount = ModConfig.Instance.BossCount;
                    bossCount = Mathf.Clamp(bossCount, 1, 5); // 限制在1到5个Boss之间
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

                            // 使用默认行为（Vector3.zero通常意味着CreateCharacterAsync中的随机/默认生成点（如果支持），
                            // 但CreateCharacterAsync通常需要一个位置。
                            // 然而，"官方系统决定位置"。
                            // 如果我们传递Vector3.zero，它可能会生成在(0,0,0)。
                            // 但CharacterRandomPreset.CreateCharacterAsync通常需要一个位置。
                            // 如果我们想要"官方系统"，我们可能需要使用游戏自己的工具找到一个有效的随机点，或者直接使用我们之前找到的一个生成点，不使用自定义逻辑。
                            // 实际上，"删除我们模组中干扰敌人位置的代码和逻辑"。
                            // 这意味着我们应该从地图定义的生成点中选择一个有效的随机生成点（这是官方系统会做的）
                            // 或者只传递一个简单的点，让导航网格/游戏处理它。
                            // 由于`CreateCharacterAsync`需要一个位置，我们必须提供一个。
                            // 随机生成的"官方系统"通常会选择一个`Points`对象。
                            // 所以我们将从`_pointsCache`中选择一个随机的`Points`并使用其位置，移除所有自定义的"搜索/过滤/躲避/回退"逻辑。
                            
                            Vector3 anchor = Vector3.zero;
                            bool found = false;
                            
                            // 从缓存中简单随机选择
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
                                // 若无生成点缓存则回退（在突袭中不太可能）
                                if (player != null) anchor = player.transform.position;
                            }
                            
                            try
                            {
                                var clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, targetScene, null, false);
                                if (clone != null)
                                {
                                    // 等待更长时间以确保完全初始化（Animator、MagicBlend等）
                                    await UniTask.Yield(PlayerLoopTiming.Update); 
                                    await UniTask.Delay(1000); 
                                    
                                    if (clone == null) continue; 
                                    
                                    // 检查是否是宠物或NPC，如果是则跳过
                                    if (IsPetOrNPC(clone))
                                    {
                                        BloodMoon.Utils.Logger.Warning($"[BossManager] Skipping pet/NPC character: {clone.name}");
                                        // 销毁这个角色
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
                                    _groupAnchors[clone] = anchor;
                                    _charactersCache.Add(clone);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"[BloodMoon] Create Boss Error: {ex}");
                            }
            }
        }

                    // 禁用Vanilla控制器缓存();
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
        private bool IsPetOrNPC(CharacterMainControl character)
        {
            if (character == null) return false;
            
            try
            {
                // 调试：记录检查开始
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Checking if character {character.name} is pet/NPC");
                }
                
                // 检查角色预设
                var preset = character.characterPreset;
                if (preset != null)
                {
                    // 获取角色图标
                    var icon = preset.GetCharacterIcon();
                    if (icon != null)
                    {
                        // 检查是否是宠物图标
                        var petIcon = GameplayDataSettings.UIStyle.PetCharacterIcon;
                        if (petIcon != null && icon == petIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is a pet (pet icon detected)");
                            return true;
                        }
                        
                        // 检查是否是商人图标
                        var merchantIcon = GameplayDataSettings.UIStyle.MerchantCharacterIcon;
                        if (merchantIcon != null && icon == merchantIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} is a merchant (merchant icon detected)");
                            return true;
                        }
                        
                        // 调试：记录图标检查结果
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} icon check passed (not pet/merchant)");
                        }
                    }
                    else
                    {
                        // 调试：没有图标
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no icon");
                        }
                    }
                }
                else
                {
                    // 调试：没有预设
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no characterPreset");
                    }
                }
                
                // 检查角色名称（备用方法）
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
                
                // 调试：名称检查通过
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} name check passed (no pet/NPC keywords)");
                }
                
                // 检查是否有PetAI组件
                var petAI = character.GetComponent<PetAI>();
                if (petAI != null)
                {
                    BloodMoon.Utils.Logger.Warning($"[BossManager] Character {character.name} has PetAI component");
                    return true;
                }
                
                // 调试：组件检查通过
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} component check passed (no PetAI)");
                }
                
                // 所有检查通过，不是宠物或NPC
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
