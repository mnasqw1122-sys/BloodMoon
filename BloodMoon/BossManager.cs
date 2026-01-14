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
        private float _bloodMoonDurationSec => ModConfig.Instance.ActiveHours * 3600f; // Config is in hours? No, likely game hours. 

        
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
            
            // Extra safety: Try to disable NodeCanvas Owner via reflection
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

            // Also disable FlowScriptController if present
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

                // Periodic Weight Enforcement
                _weightCheckTimer -= Time.deltaTime;
                if (_weightCheckTimer <= 0f)
                {
                    _weightCheckTimer = 10.0f; // Force fix every 10 seconds to persist against system resets
                    foreach(var c in _processed)
                    {
                        if (c != null && c.gameObject.activeInHierarchy && c.Health.CurrentHealth > 0)
                        {
                            FixWeight(c, c.CharacterItem);
                        }
                    }
                }

                // Initialize start time if active and not set, or reset if expired and new raid?
                // For now, let's rely on StartSceneSetupParallel to set the start time for the raid.
                
                _scanCooldown -= Time.deltaTime;
                if (_scanCooldown > 0f)
                {
                    return;
                }
                var player = CharacterMainControl.Main;
                if (!_sceneSetupDone || _setupRunning)
                {
                    // Wait for setup to complete
                    return;
                }
                
                // Centralized cleanup (every few seconds)
                if (_scanCooldown > 0.9f) // Do this once per scan cycle
                {
                     _store.DecayAndPrune(Time.time, 120f);
                     
                     // Backup: If cache empty, try to find points
                     if (_pointsCache.Count == 0)
                     {
                        var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                        foreach (var s in spawners) { if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints); }
                     }
                }

                // Use Centralized Store Cache
                var all = _store.AllCharacters;
                int count = all.Count;
                
                for (int i = 0; i < count; i++)
                {
                    var c = all[i];
                    if (c == null || c.IsMainCharacter) continue;
                    
                    // Optimization: Check if already processed before doing heavy checks
                    if (_processed.Contains(c)) continue;

                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (!isBoss) continue;
                    
                    if (_selectionInitialized && preset != null && !_selectedBossPresets.Contains(preset))
                    {
                        continue;
                    }
                    
                    EnhanceBoss(c);
                    // SetupBossLocation(c); // Removed
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
                 
                // Always spawn minions near the boss to ensure they stay together
                 Vector3 spawnPos = anchor;
                 
                // Use boss's current position as the anchor, not the initial spawn point
                 Vector3 bossPos = boss.transform.position;
                 
                // Generate random offset near the boss (3-8 meters)
                 var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(3f, 8f);
                 spawnPos = bossPos + new Vector3(offset.x, 0, offset.y);
                 
                 // Basic ground check for safety
                 if (UnityEngine.AI.NavMesh.SamplePosition(spawnPos, out var hit, 10f, UnityEngine.AI.NavMesh.AllAreas))
                 {
                     spawnPos = hit.position;
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
                         await UniTask.Delay(100); 
                         if (clone == null) continue;
                         var charItem = clone.CharacterItem;
                         if (charItem == null) continue;

                         DisableVanillaAI(clone);
                         
                         Multiply(charItem, "WalkSpeed", 1.2f);
                         Multiply(charItem, "RunSpeed", 1.2f);
                         
                         BoostDefense(charItem, false);
                         
                         var mh = charItem.GetStat("MaxHealth".GetHashCode());
                         if (mh != null) mh.BaseValue *= ModConfig.Instance.MinionHealthMultiplier;
                         clone.Health.SetHealth(clone.Health.MaxHealth);
                         
                         // Fix Weight Issues for Minion
                         FixWeight(clone, charItem);

                         clone.SetTeam(Teams.wolf);
                        
                        // Ensure minion has weapons
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
            // Disable default spawner system to prevent new waves
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

            // 1. Set massive MaxWeight
            var mw = item.GetStat("MaxWeight".GetHashCode());
            if (mw != null) mw.BaseValue = 10000f; // 10000kg

            // 2. Set massive Inventory Capacity to prevent looting block
            var cap = item.GetStat("InventoryCapacity".GetHashCode());
            if (cap != null) cap.BaseValue = 200f; // 200 slots

            // 3. Remove existing Overweight Buffs
            c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
            
            // 4. Force Update Weight State
            c.UpdateWeightState();

            // 5. Apply Walk/Run Speed Multipliers again just in case weight system overrides them
            //    Usually weight reduces speed by percentage, so boosting base speed helps
            //    But if MaxWeight is 10000, current weight should be 0% load, so no penalty.
        }

        private void EnhanceBoss(CharacterMainControl c)
        {
            // IMMEDIATELY disable vanilla AI to prevent logic conflict during async setup
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
                
                // Fix Weight Issues
                FixWeight(c, item);
                
                c.Health.SetHealth(c.Health.MaxHealth);
                
                // Ensure AI is attached to the boss if not present
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
                // Search for high quality items (Quality 4+)
                // Search() automatically downgrades quality if no items are found at the requested level.
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
                    // Add 1-3 random high-quality items
                    int count = UnityEngine.Random.Range(1, 4);
                    for (int i = 0; i < count; i++)
                    {
                        int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (!c.CharacterItem.Inventory.AddAndMerge(item))
                            {
                                // If inventory is full, just drop it at the boss's feet
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
            // Add Point Light
            var lightObj = new GameObject("BossGlowLight");
            lightObj.transform.SetParent(c.transform);
            lightObj.transform.localPosition = Vector3.up * 1.5f;
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.1f, 0.1f);
            light.range = 6.0f;
            light.intensity = 4.0f;
            light.shadows = LightShadows.Soft;

            // Modify Materials for Emission
            // Optimization: Only runs once per boss initialization
            var renderers = c.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                
                // Using .materials creates a unique instance, ensuring we only glow this specific Boss
                // and not other enemies sharing the same model.
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
            if (player == null) return;

            var ai = c.GetComponent<AICharacterController>();
            if (ai != null)
            {
                ai.SetTarget(player.mainDamageReceiver.transform);
                ai.forceTracePlayerDistance = 100f;
            }
            
            // Ensure AI is initialized (Should be handled by EnhanceBoss, but double check)
            var custom = c.GetComponent<BloodMoonAIController>();
            if (custom != null && custom.CanChase == false)
            {
                custom.SetChaseDelay(0f);
            }
            
            // Random Taunt
            string[] taunts = { "Boss_Revenge", "Boss_Taunt_1", "Boss_Taunt_2", "Boss_Taunt_3" };
            string key = taunts[UnityEngine.Random.Range(0, taunts.Length)];
            c.PopText(Localization.Get(key));
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
            
            // Check if character already has weapons
            if (character.MeleeWeaponSlot()?.Content != null) hasMelee = true;
            if (character.PrimWeaponSlot()?.Content != null) hasGun = true;
            if (character.SecWeaponSlot()?.Content != null) hasGun = true;
            
            // If character has both melee and gun, we're good
            if (hasMelee && hasGun) return;
            
            // Try to add melee weapon if missing
            if (!hasMelee)
            {
                await TryAddMeleeWeapon(character);
            }
            
            // Try to add gun if missing
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
                    // 1. Try Specific Tags First
                    Tag meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Melee");
                    if (meleeTag == null) meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Weapon");

                    var filter = new ItemFilter
                    {
                        minQuality = 1,
                        maxQuality = 5,
                        requireTags = meleeTag != null ? new Tag[] { meleeTag } : null
                    };
                    
                    int[]? ids = null;
                    try 
                    {
                        ids = ItemAssetsCollection.Search(filter);
                    }
                    catch (System.Exception) 
                    { 
                        // Swallow search errors (like IndexOutOfRange from StringBuilder race conditions)
                    }
                    
                    // 2. Fallback: Search Broadly if specific search failed
                    if (ids == null || ids.Length == 0)
                    {
                        // Try searching for "Weapon" if we tried "Melee" before
                        if (meleeTag != null && meleeTag.name == "Melee")
                        {
                            var weaponTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Weapon");
                            if (weaponTag != null)
                            {
                                filter.requireTags = new Tag[] { weaponTag };
                                try 
                                {
                                    ids = ItemAssetsCollection.Search(filter);
                                }
                                catch {}
                            }
                        }
                    }
                    
                    // 3. Desperate Fallback: Search EVERYTHING (No tags)
                    if (ids == null || ids.Length == 0)
                    {
                        filter.requireTags = null; // Clear tags
                        try 
                        {
                            ids = ItemAssetsCollection.Search(filter);
                        }
                        catch {}
                    }

                    if (ids != null && ids.Length > 0)
                    {
                        // Try up to 15 times to find a valid weapon (increased from 3)
                        // Since we might be searching broadly, we need more attempts to hit a valid weapon
                        int attempts = filter.requireTags == null ? 15 : 5;
                        
                        for(int i=0; i<attempts; i++)
                        {
                            int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                            var item = await ItemAssetsCollection.InstantiateAsync(id);
                            if (item != null)
                            {
                                var slot = character.MeleeWeaponSlot();
                                if (slot != null && slot.CanPlug(item))
                                {
                                    if (character.CharacterItem.Inventory.AddAndMerge(item))
                                    {
                                        slot.Plug(item, out var _);
                                        return; // Success
                                    }
                                }
                                UnityEngine.Object.Destroy(item.gameObject);
                            }
                        }
                    }

                    Debug.LogWarning($"[BloodMoon] Minion missing melee weapon: {character.name} (All searches failed)");
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
                    // 1. Try Specific Tags
                    var filter = new ItemFilter
                    {
                        minQuality = 1,
                        maxQuality = 6,
                        requireTags = new Tag[] { GameplayDataSettings.Tags.Gun }
                    };
                    
                    int[]? ids = null;
                    try
                    {
                        ids = ItemAssetsCollection.Search(filter);
                    }
                    catch {}
                    
                    // 2. Fallback: Broad Search if Gun tag fails (unlikely but safe)
                    if (ids == null || ids.Length == 0)
                    {
                         filter.requireTags = null;
                         try 
                         {
                             ids = ItemAssetsCollection.Search(filter);
                         }
                         catch {}
                    }

                    if (ids != null && ids.Length > 0)
                    {
                        int attempts = filter.requireTags == null ? 15 : 5;
                        
                        for(int i=0; i<attempts; i++)
                        {
                            int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                            var item = await ItemAssetsCollection.InstantiateAsync(id);
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
                        }
                    }

                    Debug.LogWarning($"[BloodMoon] Minion missing guns: {character.name} (All searches failed)");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BloodMoon] Failed to add gun: {ex}");
            }
        }

        private async UniTask AddAmmoForGun(CharacterMainControl c, Item gun)
        {
            try
            {
                var bullet = await ItemUtilities.GenerateBullet(gun);
                if (bullet != null)
                {
                    bullet.StackCount = bullet.MaxStackCount; // Full stack
                    c.CharacterItem.Inventory.AddAndMerge(bullet);
                    
                    // Add a few more stacks
                    for(int i=0; i<2; i++)
                    {
                        var extra = await ItemUtilities.GenerateBullet(gun);
                        if (extra != null)
                        {
                             extra.StackCount = extra.MaxStackCount;
                             c.CharacterItem.Inventory.AddAndMerge(extra);
                        }
                    }
                }
            }
            catch {}
        }











        private void AssignWingIndicesForLeader(CharacterMainControl boss)
        {
            // Optimization: Use cached characters from Store
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
                // Use Persistent ID (Preset Name) instead of transient InstanceID
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
                if (f == null) continue; // Safety check
                
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
            
            // Ensure clean state before starting
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
                // Wait for Level Initialization to settle (Avoid race conditions with Game's Spawners)
                await UniTask.Delay(3000); 

                await UniTask.Yield();

                // --- Synchronous Initialization Phase ---
                // Immediately capture and disable vanilla spawners to prevent interference
                _pointsCache.Clear();
                _disabledSpawners.Clear();

                // 1. Capture Points & Limit Native Spawners (Do not disable)
                var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                foreach (var s in spawners) 
                { 
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    // Limit quantity to avoid overcrowding
                    // s.spawnCountRange = new Vector2Int(1, 1);
                }
                
                await UniTask.Yield();

                var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
                foreach (var s in waveSpawners) 
                { 
                    if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    // Limit quantity to avoid overcrowding
                    // s.spawnCountRange = new Vector2Int(1, 1);
                }

                // -----------------------------------

                // 2. Initial Map Metrics Calculation
                // RecalculateMapMetrics(); // Removed
                
                // 3. Initial Disable of any pre-existing vanilla AI
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                // DisableVanillaControllersCached();
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
                    
                    // Re-scan characters in case player appeared
                    _charactersCache.Clear();
                    _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                    // DisableVanillaControllersCached();

                    _currentScene = scene;
                    _sceneSetupDone = false;
                    
                    // Identify all available raid scenes from points
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

                    await Task.Run(() =>
                    {
                        var rnd = new System.Random();
                        int bossCount = ModConfig.Instance.BossCount;
                        bossCount = Mathf.Clamp(bossCount, 1, 5); // Limit between 1 and 5 bosses
                        selected = allBossPresets.OrderBy(_ => rnd.Next()).Take(bossCount).ToArray();
                    });

                    // Force return to Main Thread to ensure Unity API safety
                    await UniTask.SwitchToMainThread();

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

                            // Use default behavior (Vector3.zero usually implies random/default spawn point in CreateCharacterAsync if supported, 
                            // but CreateCharacterAsync usually requires a position. 
                            // However, the user request says "Official system determines location". 
                            // If we pass Vector3.zero, it might spawn at (0,0,0). 
                            // But CharacterRandomPreset.CreateCharacterAsync typically takes a position.
                            // If we want "Official System", we might need to find a valid random point using Game's own utility or just use one of the Points we found earlier without custom logic.
                            // Actually, the user says "Delete our mod's code and logic for interfering with enemy position".
                            // This implies we should just pick a valid random spawn point from the map's defined points (which is what the official system would do) 
                            // OR just pass a simple point and let the navmesh/game handle it.
                            // Since `CreateCharacterAsync` REQUIRES a position, we must provide one.
                            // The "Official System" for random spawns usually picks a `Points` object.
                            // So we will just pick a random `Points` from `_pointsCache` and use its position, removing all our custom "Search/Filter/Avoid/Fallback" logic.
                            
                            Vector3 anchor = Vector3.zero;
                            bool found = false;
                            
                            // Simple Random Pick from Cache
                            if (_pointsCache.Count > 0)
                            {
                                var pt = _pointsCache[UnityEngine.Random.Range(0, _pointsCache.Count)];
                                if (pt != null)
                                {
                                    anchor = pt.GetRandomPoint();
                                    targetScene = pt.gameObject.scene.buildIndex;
                                    found = true;
                                }
                            }
                            
                            if (!found)
                            {
                                // Fallback if no points cache (unlikely in raid)
                                if (player != null) anchor = player.transform.position; 
                            }
                            
                            try
                            {
                                var clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, targetScene, null, false);
                                if (clone != null)
                                {
                                    // Wait longer to ensure full initialization (Animator, MagicBlend, etc.)
                                    await UniTask.Yield(PlayerLoopTiming.Update); 
                                    await UniTask.Delay(1000); 
                                    
                                    if (clone == null) continue; 
                                    
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

                    // DisableVanillaControllersCached();
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

    }
}
