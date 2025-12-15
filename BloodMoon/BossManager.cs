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
        private float _charactersRescanCooldown;
        private List<MonoBehaviour> _disabledSpawners = new List<MonoBehaviour>();

        private float _estimatedMapRadius = 150f;

        private void RecalculateMapMetrics()
        {
            if (_pointsCache.Count == 0) return;
            
            Vector3 min = new Vector3(float.MaxValue, 0, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, 0, float.MinValue);
            bool any = false;

            foreach (var p in _pointsCache)
            {
                if (p == null) continue;
                Vector3 pos = p.transform.position;
                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
                any = true;
            }
            
            if (any)
            {
                float sizeX = Mathf.Abs(max.x - min.x);
                float sizeZ = Mathf.Abs(max.z - min.z);
                _estimatedMapRadius = Mathf.Max(sizeX, sizeZ) * 0.5f;
                _estimatedMapRadius = Mathf.Clamp(_estimatedMapRadius, 80f, 800f); // Allow for very large maps
            }
        }

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

        public void Tick()
        {
            try 
            {
                if (!_initialized) return;

                if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap || LevelManager.Instance.IsBaseLevel)
                {
                    return;
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
                if (_charactersRescanCooldown <= 0f)
                {
                    // Simple optimization: only scan if we are not overloaded
                    _charactersCache.Clear();
                    _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                    
                    // Refresh points cache if empty (backup safety)
                    if (_pointsCache.Count == 0)
                    {
                        var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                        foreach (var s in spawners) { if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints); }
                        var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
                        foreach (var s in waveSpawners) { if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints); }
                    }
                    
                    // Scan more frequently to catch new spawns (waves)
                    _charactersRescanCooldown = 2.0f;
                    // DisableVanillaControllersCached();
                    
                    // Centralized cleanup
                    _store.DecayAndPrune(Time.time, 120f);
                }
                _charactersRescanCooldown -= Time.deltaTime;
                var all = _charactersCache;
                foreach (var c in all)
                {
                    if (c == null || c.IsMainCharacter) continue;
                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (!isBoss) continue;
                    if (_selectionInitialized && preset != null && !_selectedBossPresets.Contains(preset))
                    {
                        continue;
                    }
                    if (!_processed.Contains(c))
                    {
                        EnhanceBoss(c).Forget();
                        SetupBossLocation(c);
                        EnableRevenge(c, player);
                        SpawnMinionsForBoss(c, c.transform.position, c.gameObject.scene.buildIndex).Forget();
                        _processed.Add(c);
                    }
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
                .Where(p => p != null && p.GetCharacterIcon() != GameplayDataSettings.UIStyle.BossCharacterIcon)
                .ToList();

            if (minionPresets.Count == 0) return;

            int count = ModConfig.Instance.BossMinionCount;
            count = Mathf.Clamp(count, 3, 10); 

            for (int i = 0; i < count; i++)
            {
                 var preset = minionPresets[UnityEngine.Random.Range(0, minionPresets.Count)];
                 
                 var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(3f, 8f);
                 var spawnPos = anchor + new Vector3(offset.x, 0, offset.y);
                 
                 if (UnityEngine.AI.NavMesh.SamplePosition(spawnPos, out var hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
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

                         DisableVanillaAI(clone);
                         
                         Multiply(clone.CharacterItem, "WalkSpeed", 1.2f);
                         Multiply(clone.CharacterItem, "RunSpeed", 1.2f);
                         await EquipArmorLevel(clone, 6);
                         BoostDefense(clone.CharacterItem, false);
                         // Give minions some meds too!
                         await EnsureMedicalSupplies(clone, 2); 
                        await EnsureMinionWeapon(clone);
                        await EquipMeleeWeaponLevel(clone, 5);
                        await FillHighAmmo(clone, 5);
                         await EnsureGunLoaded(clone);
                         
                         var mh = clone.CharacterItem.GetStat("MaxHealth".GetHashCode());
                         if (mh != null) mh.BaseValue *= ModConfig.Instance.MinionHealthMultiplier;
                         clone.Health.SetHealth(clone.Health.MaxHealth);
                         
                         var mw = clone.CharacterItem.GetStat("MaxWeight".GetHashCode());
                         if (mw != null) mw.BaseValue = 1000f;
                         clone.UpdateWeightState();
                         clone.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);

                         clone.SetTeam(Teams.wolf);
                         
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

        private async UniTask EnhanceBoss(CharacterMainControl c)
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
                var mw = item.GetStat("MaxWeight".GetHashCode());
                if (mw != null) mw.BaseValue = 1000f;
                
                // Ensure backpack is equipped first to provide space
                await EquipTopArmor(c);
                
                // PRIORITY: Ensure Medical Supplies BEFORE Ammo fills the bag
                await EnsureMedicalSupplies(c, 4);

                await EnsureTwoGuns(c);
                await EquipMeleeWeaponLevel(c, 6);
                await FillBestAmmo(c);
                await EnsureGunLoaded(c);
                
                c.Health.SetHealth(c.Health.MaxHealth);
                c.UpdateWeightState();
                c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
                
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
                
                // Adjust supplies based on backpack status - Dynamic High Value Selection
                await EnsureProvisions(c);
                await AddHighValueLoot(c);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] EnhanceBoss Error: {e}");
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

        private async System.Threading.Tasks.Task EquipBestItemByTag(CharacterMainControl c, string tag, int minVal)
        {
             var ids = ItemSelector.GetBestItemsByTags(new[] { tag }, 1, minValue: minVal);
             if (ids.Count == 0)
             {
                 var t = TagUtilities.TagFromString(tag);
                 if (t != null)
                 {
                     var filter = new ItemFilter { requireTags = new[] { t } };
                     ids = ItemSelector.GetBestItems(filter, 1);
                 }
             }

             if (ids.Count > 0)
             {
                var item = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(ids[0]);
                if (item != null)
                {
                    if (!c.CharacterItem.TryPlug(item, emptyOnly: true)) item.DestroyTree();
                }
             }
        }

        private async System.Threading.Tasks.Task EquipTopArmor(CharacterMainControl c)
        {
            await EquipBestItemByTag(c, "Helmat", 10);
            await EquipBestItemByTag(c, "Armor", 10);
            await EquipBestBackpack(c);
        }

        private async System.Threading.Tasks.Task EquipMeleeWeaponLevel(CharacterMainControl c, int level)
        {
            await EquipBestItemByTag(c, "MeleeWeapon", 10);
        }

        private async System.Threading.Tasks.Task EnsureTwoGuns(CharacterMainControl c)
        {
            // High Value Guns
            var tags = new List<Tag> { GameplayDataSettings.Tags.Gun };
            var filter = new ItemFilter {
                requireTags = tags.ToArray(),
                excludeTags = new Tag[] { TagUtilities.TagFromString("GunType_PST") }
            };
            var guns = ItemSelector.GetBestItems(filter, 10, minValue: 100); // Get top 10 to pick from

            Item? prim = c.PrimWeaponSlot()?.Content;
            Item? sec = c.SecWeaponSlot()?.Content;
            
            if (guns.Count > 0)
            {
                if (prim == null)
                {
                    var id = guns[UnityEngine.Random.Range(0, Mathf.Min(3, guns.Count))];
                    prim = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (prim != null)
                    {
                        if (!c.CharacterItem.TryPlug(prim, emptyOnly: true)) prim.DestroyTree();
                    }
                }
                if (sec == null && guns.Count > 1)
                {
                    var id = guns[UnityEngine.Random.Range(0, Mathf.Min(3, guns.Count))];
                    // Ensure different gun if possible?
                    sec = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (sec != null)
                    {
                        if (!c.CharacterItem.TryPlug(sec, emptyOnly: true)) sec.DestroyTree();
                    }
                }
                
                // Replace Pistols if present
                if (prim != null && prim.Tags.Contains("GunType_PST"))
                {
                    var id = guns[UnityEngine.Random.Range(0, Mathf.Min(3, guns.Count))];
                    var rep = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (rep != null)
                    {
                        if (!c.CharacterItem.TryPlug(rep, emptyOnly: false)) rep.DestroyTree();
                    }
                }
                 if (sec != null && sec.Tags.Contains("GunType_PST"))
                {
                    var id = guns[UnityEngine.Random.Range(0, Mathf.Min(3, guns.Count))];
                    var rep = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (rep != null)
                    {
                        if (!c.CharacterItem.TryPlug(rep, emptyOnly: false)) rep.DestroyTree();
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task FillBestAmmo(CharacterMainControl c)
        {
            int count = HasBackpack(c) ? 8 : 2;
            await FillAmmoForGunMultiple(c, c.PrimWeaponSlot()?.Content, 6, count);
            await FillAmmoForGunMultiple(c, c.SecWeaponSlot()?.Content, 6, count);
            PurgeNonMatchingAmmo(c);
        }

        private void SetupBossLocation(CharacterMainControl c)
        {
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap) return;
            // Removed logic that forces bosses to player scene
            GatherLeaderGroup(c);
        }

        private void EnableRevenge(CharacterMainControl c, CharacterMainControl player)
        {
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
            c.PopText(Localization.Get("Boss_Revenge"));
        }


        private async System.Threading.Tasks.Task EnsureMedicalSupplies(CharacterMainControl c, int targetCount)
        {
            try
            {
                if (c == null || c.CharacterItem == null || c.CharacterItem.Inventory == null) return;

                int existing = 0;
                foreach(var it in c.CharacterItem.Inventory)
                {
                    if (it == null) continue;
                    if (it.Tags.Contains("Food") || it.Tags.Contains("Drink") || it.GetComponent<Drug>() != null) existing++;
                }
                
                int needed = targetCount - existing;
                if (needed <= 0) return;

                // "Medicine" tag is not standard. Using Food/Drink as reliable healing sources
                var tags = new string[] { "Food", "Drink" };
                var medIds = ItemSelector.GetBestItemsByTags(tags, 10, minValue: 10);
                
                if (medIds.Count == 0) return;
                
                for (int i = 0; i < needed; i++)
                {
                    int id = medIds[UnityEngine.Random.Range(0, Mathf.Min(5, medIds.Count))];
                    var item = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (item != null)
                    {
                        if (!c.CharacterItem.Inventory.AddItem(item))
                        {
                            item.DestroyTree();
                            break;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] Medical Supply Error: {e}");
            }
        }
        
        private async System.Threading.Tasks.Task EnsureProvisions(CharacterMainControl c)
        {
             // System supplement: Food/Drink - High Value
             // "Food" and "Drink" are valid tags. "Consumable" is not.
             var tags = new string[] { "Food", "Drink" };
             var items = BloodMoon.Utils.ItemSelector.GetBestItemsByTags(tags, 5, minValue: 5);
             if (items.Count == 0) return;
             
             // Add 1-2 provisions
             int count = UnityEngine.Random.Range(1, 3);
             for(int i=0; i<count; i++)
             {
                 int id = items[UnityEngine.Random.Range(0, items.Count)];
                 var item = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                 if (item != null)
                 {
                     if (!c.CharacterItem.Inventory.AddItem(item)) item.DestroyTree();
                 }
             }
        }

        private async System.Threading.Tasks.Task AddHighValueLoot(CharacterMainControl c)
        {
             // 1-2 random high value items
             int count = UnityEngine.Random.Range(1, 3);
             var filter = new ItemFilter { minQuality = 3 }; 
             var ids = BloodMoon.Utils.ItemSelector.GetRandomHighValueItems(filter, count, minValue: 100); 
             
             foreach(var id in ids)
             {
                 var item = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                 if (item != null)
                 {
                     if (!c.CharacterItem.Inventory.AddItem(item)) item.DestroyTree();
                 }
             }
        }



        private void BoostDefense(Item item, bool isBoss)
        {
            var body = item.GetStat("BodyArmor".GetHashCode());
            var head = item.GetStat("HeadArmor".GetHashCode());
            float bodyTarget = isBoss ? ModConfig.Instance.BossDefense : ModConfig.Instance.MinionDefense;
            float headTarget = isBoss ? ModConfig.Instance.BossDefense : ModConfig.Instance.MinionDefense;
            if (body != null) body.BaseValue = Mathf.Max(body.BaseValue, bodyTarget);
            if (head != null) head.BaseValue = Mathf.Max(head.BaseValue, headTarget);
        }

        private async System.Threading.Tasks.Task EquipArmorLevel(CharacterMainControl c, int level)
        {
             // Ignore level, use High Value
             await EquipBestItemByTag(c, "Helmat", 50);
             await EquipBestItemByTag(c, "Armor", 50);
             await EquipBestBackpack(c);
        }

        private async System.Threading.Tasks.Task EnsureMinionWeapon(CharacterMainControl c)
        {
            var prim = c.PrimWeaponSlot()?.Content;
            
            // If we already have a decent gun (not pistol), we are fine.
            if (prim != null && prim.Tags.Contains(GameplayDataSettings.Tags.Gun) && !prim.Tags.Contains("GunType_PST"))
            {
                return;
            }

            var tags = new List<Tag> { GameplayDataSettings.Tags.Gun };
            var filter = new ItemFilter {
                requireTags = tags.ToArray(), 
                minQuality = 0, // Lowered from 4 to allow basic guns
                excludeTags = new Tag[] { TagUtilities.TagFromString("GunType_PST") }
            };
            
            // Try to find decent guns first
            var guns = ItemSelector.GetBestItems(filter, 10, minValue: 50);
            
            // Fallback: if no non-pistol guns found, try ANY gun including pistols
            if (guns.Count == 0)
            {
                filter.excludeTags = null;
                guns = ItemSelector.GetBestItems(filter, 10, minValue: 20);
            }
            
            if (guns.Count <= 0) 
            {
                // Debug.LogWarning("[BloodMoon] No guns found for minion!");
                return;
            }

            // Pick a random gun from the list
            var id = guns[UnityEngine.Random.Range(0, Mathf.Min(3, guns.Count))];
            var newGun = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
            
            if (newGun != null)
            {
                // If we have a pistol or nothing, replace/fill
                // Using emptyOnly: false to force swap if needed
                if (!c.CharacterItem.TryPlug(newGun, emptyOnly: false)) 
                {
                    newGun.DestroyTree();
                }
            }
        }

        private async System.Threading.Tasks.Task FillHighAmmo(CharacterMainControl c, int level)
        {
            int count = HasBackpack(c) ? 5 : 1;
            await FillAmmoForGunMultiple(c, c.PrimWeaponSlot()?.Content, level, count);
            await FillAmmoForGunMultiple(c, c.SecWeaponSlot()?.Content, level, count);
            PurgeNonMatchingAmmo(c);
        }

        private async System.Threading.Tasks.Task FillAmmoForGun(CharacterMainControl c, Item? gun, int level)
        {
            if (gun == null) return;
            var caliber = gun.Constants?.GetString("Caliber") ?? string.Empty;
            if (string.IsNullOrEmpty(caliber)) return;
            
            var filter = new ItemFilter { caliber = caliber, requireTags = new Tag[] { GameplayDataSettings.Tags.Bullet } };
            // High Value Ammo
            var ids = ItemSelector.GetBestItems(filter, 5, minValue: 1); 
            
            if (ids.Count > 0)
            {
                var id = ids[0]; // Best ammo
                var ammo = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                if (ammo != null)
                {
                    if (!c.CharacterItem.Inventory.AddItem(ammo))
                    {
                        ammo.DestroyTree();
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task FillAmmoForGunMultiple(CharacterMainControl c, Item? gun, int level, int targetCount)
        {
            if (gun == null || targetCount <= 0) return;
            var inventory = c.CharacterItem?.Inventory;
            if (inventory == null) return;

            var caliber = gun.Constants?.GetString("Caliber") ?? string.Empty;
            if (string.IsNullOrEmpty(caliber)) return;

            int existing = 0;
            foreach (var item in inventory)
            {
                if (item == null) continue;
                if (item.Tags.Contains(GameplayDataSettings.Tags.Bullet))
                {
                    var cal = item.Constants?.GetString("Caliber");
                    if (cal == caliber) existing++;
                }
            }

            int needed = targetCount - existing;
            if (needed <= 0) return;

            var filter = new ItemFilter { caliber = caliber, requireTags = new Tag[] { GameplayDataSettings.Tags.Bullet } };
            var ids = ItemSelector.GetBestItems(filter, 3, minValue: 1); 
            if (ids.Count == 0) return;
            
            for (int i = 0; i < needed; i++)
            {
                int id = ids[UnityEngine.Random.Range(0, Mathf.Min(2, ids.Count))];
                var ammo = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                if (ammo != null)
                {
                    if (!inventory.AddItem(ammo))
                    {
                        ammo.DestroyTree();
                        break; 
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task EquipBestBackpack(CharacterMainControl c)
        {
            try
            {
                var tags = new List<Tag> { GameplayDataSettings.Tags.Backpack };
                var filter = new ItemFilter { requireTags = tags.ToArray() };
                var candidates = ItemSelector.GetBestItems(filter, 5, minValue: 50); // High value backpacks
                
                if (candidates.Count == 0) return; 

                int backpackHash = "Backpack".GetHashCode();
                var slot = c.CharacterItem.Slots.GetSlot(backpackHash);
                var inv = c.CharacterItem.Inventory;

                if (inv != null)
                {
                    var toRemove = new List<Item>();
                    foreach (var it in inv)
                    {
                        if (it != null && it.Tags.Contains(GameplayDataSettings.Tags.Backpack))
                        {
                            toRemove.Add(it);
                        }
                    }
                    for (int i = 0; i < toRemove.Count; i++) toRemove[i].DestroyTree();
                }
                if (slot != null && slot.Content != null)
                {
                    var unplugged = slot.Unplug();
                    if (unplugged != null) unplugged.DestroyTree();
                }

                foreach (var id in candidates)
                {
                    var item = await BloodMoon.Utils.ItemInstantiateSafe.SafeInstantiateById(id);
                    if (item == null) continue;
                    
                    if (item.Slots == null || item.Slots.Count == 0)
                    {
                        item.DestroyTree();
                        continue;
                    }

                    if (slot != null && slot.CanPlug(item))
                    {
                        if (!slot.Plug(item, out var unpluggedItem))
                        {
                            item.DestroyTree();
                            continue;
                        }
                        if (unpluggedItem != null)
                        {
                            if (inv == null || !inv.AddAndMerge(unpluggedItem)) unpluggedItem.DestroyTree();
                        }
                        return;
                    }
                    else
                    {
                        if (!c.CharacterItem.TryPlug(item, emptyOnly: false)) item.DestroyTree();
                        else return;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BloodMoon] EquipBestBackpack Error: {e}");
            }
        }

        private bool HasBackpack(CharacterMainControl c)
        {
            if (c == null || c.CharacterItem == null || c.CharacterItem.Slots == null) return false;
            var slot = c.CharacterItem.Slots.GetSlot("Backpack".GetHashCode());
            return slot != null && slot.Content != null;
        }

        private async System.Threading.Tasks.Task EnsureGunLoaded(CharacterMainControl c)
        {
            await UniTask.Yield();
            var g = c.GetGun();
            var gs = g?.GunItemSetting;
            var inv = c.CharacterItem?.Inventory;
            if (gs != null && inv != null)
            {
                var caliber = g?.Item?.Constants?.GetString("Caliber") ?? string.Empty;
                var best = GetBestAPBulletItem(inv, caliber);
                if (best != null)
                {
                    gs.SetTargetBulletType(best);
                }
                else
                {
                    // Avoid AutoSetTypeInInventory if ID is invalid (-1) which causes ItemAssetCollection error
                    // gs.AutoSetTypeInInventory(inv);
                }
                
                // Safe load
                try
                {
                    gs.LoadBulletsFromInventory(inv).Forget();
                }
                catch (System.Exception ex)
                {
                    // Suppress ItemID -1 errors
                    if (!ex.Message.Contains("-1")) Debug.LogWarning($"[BloodMoon] LoadBullets Warning: {ex.Message}");
                }
                
                PurgeNonMatchingAmmo(c);
            }
        }

        private Item? GetBestAPBulletItem(Inventory? inv, string? caliber)
        {
            if (inv == null) return null;
            float bestGain = -1f; Item? best = null;
            int apHash = "ArmorPiercingGain".GetHashCode();
            int calHash = "Caliber".GetHashCode();
            foreach (var it in inv)
            {
                if (it == null) continue;
                if (!it.Tags.Contains(GameplayDataSettings.Tags.Bullet)) continue;
                string cal = it.Constants?.GetString(calHash) ?? string.Empty;
                if (!string.IsNullOrEmpty(caliber) && cal != caliber) continue;
                float gain = it.Constants?.GetFloat(apHash, 0f) ?? 0f;
                if (gain > bestGain)
                {
                    bestGain = gain; best = it;
                }
            }
            return best;
        }

        private void PurgeNonMatchingAmmo(CharacterMainControl c)
        {
            var inv = c.CharacterItem?.Inventory; if (inv == null) return;
            var prim = c.PrimWeaponSlot()?.Content;
            var sec = c.SecWeaponSlot()?.Content;
            string? primCal = prim != null ? (prim.Constants?.GetString("Caliber") ?? string.Empty) : null;
            string? secCal = sec != null ? (sec.Constants?.GetString("Caliber") ?? string.Empty) : null;
            var toRemove = new List<Item>();
            foreach (var it in inv)
            {
                if (it == null) continue;
                if (!it.Tags.Contains(GameplayDataSettings.Tags.Bullet)) continue;
                string cal = it.Constants?.GetString("Caliber") ?? string.Empty;
                bool matchPrim = !string.IsNullOrEmpty(primCal) && cal == primCal;
                bool matchSec = !string.IsNullOrEmpty(secCal) && cal == secCal;
                if (!matchPrim && !matchSec)
                {
                    toRemove.Add(it);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                var it = toRemove[i];
                it.DestroyTree();
            }
        }



        private void AssignWingIndicesForLeader(CharacterMainControl boss)
        {
            // Optimization: Use cached characters
            var controllers = new List<AICharacterController>();
            foreach(var c in _charactersCache) {
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
                int id = boss.GetInstanceID();
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

        private Vector3 GetOrCreateAnchor(CharacterMainControl boss, Vector3 fallbackCenter)
        {
            if (_groupAnchors.TryGetValue(boss, out var pos)) return pos;
            
            // Dynamic range based on map size
            float minR = Mathf.Clamp(_estimatedMapRadius * 0.15f, 35f, 80f);
            float maxR = Mathf.Clamp(_estimatedMapRadius * 0.6f, 120f, 400f);
            
            // Collect existing anchors to avoid overlap
            var avoid = _groupAnchors.Values.ToList();
            
            if (!TryGetSeparatedMapPoint(fallbackCenter, minR, maxR, avoid, 40f, out pos))
            {
                // Retry with larger range for big maps or tighter constraints
                if (!TryGetSeparatedMapPoint(fallbackCenter, minR, maxR * 1.5f, avoid, 30f, out pos))
                {
                     pos = GetSpawnPointOrFallback(fallbackCenter, avoid);
                }
            }
            _groupAnchors[boss] = pos;
            return pos;
        }

        private Vector3 GetSpawnPointOrFallback(Vector3 fallback) => GetSpawnPointOrFallback(fallback, new List<Vector3>());
        
        private Vector3 GetSpawnPointOrFallback(Vector3 fallback, List<Vector3> avoid)
        {
            float minR = Mathf.Clamp(_estimatedMapRadius * 0.15f, 35f, 60f);
            float maxR = Mathf.Clamp(_estimatedMapRadius * 0.6f, 120f, 400f);

            // 1. Standard range
            if (TryGetSeparatedMapPoint(fallback, minR, maxR, avoid, 40f, out var p)) return p;
            
            // 2. Extended range
            if (TryGetSeparatedMapPoint(fallback, minR, maxR * 1.5f, avoid, 30f, out p)) return p;
            
            // 3. Desperate Map Search: Ignore min range if far enough from avoid, or just find ANY valid point far from player
            if (TryGetSeparatedMapPoint(fallback, 20f, 9999f, avoid, 20f, out p)) return p;

            // 4. Fallback to player relative logic (Procedural search)
            var result = fallback;
            var player = CharacterMainControl.Main;
            if (player != null)
            {
                var playerPos = player.transform.position;
                float pMinR = 35f;
                float pMaxR = 55f;
                
                // Try multiple angles to find a valid spot
                for (int i = 0; i < 20; i++)
                {
                    // Random angle
                    float angle = UnityEngine.Random.Range(0f, 360f);
                    Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                    
                    // Bias towards behind player for first few attempts
                    if (i < 5)
                    {
                        Vector3 back = -player.transform.forward;
                        dir = Vector3.Slerp(back, dir, 0.5f).normalized;
                    }

                    float r = UnityEngine.Random.Range(pMinR, pMaxR);
                    var candidate = playerPos + dir * r;
                    
                    // Validate Ground
                    if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                    {
                        candidate = hit.point;
                        
                        // Validate Line of Sight (FOW)
                        var mask = GameplayDataSettings.Layers.fowBlockLayers;
                        var from = playerPos + Vector3.up * 1.6f;
                        var to = candidate + Vector3.up * 1.2f;
                        var checkDir = to - from;
                        
                        // If we hit something (wall), it's a good hiding spot -> valid
                        // If we DON'T hit anything, line of sight is clear -> might spawn in plain sight (bad but acceptable if desperate)
                        // Actually, we prefer NOT to spawn in plain sight.
                        // But finding a valid navmesh/ground point is higher priority than hiding.
                        
                        // Check distance from other bosses
                        bool conflict = false;
                        foreach(var av in avoid)
                        {
                             if ((av - candidate).sqrMagnitude < 900f) // 30m * 30m
                             {
                                 conflict = true; break;
                             }
                        }
                        if (conflict) continue;

                        result = candidate;
                        return result; // Found a good spot
                    }
                }
            }
            
            // 5. If everything fails, return fallback (Player Pos) BUT log warning
            // Ideally we should move it slightly if it's exactly player pos
            if ((result - fallback).sqrMagnitude < 225f) // 15m * 15m check
            {
                Debug.LogWarning("[BloodMoon] Could not find spawn point. Forcing safe offset.");
                // Try to find a point at least 25m away
                var offset = (result - fallback).normalized;
                if (offset == Vector3.zero) offset = Vector3.forward;
                result = fallback + offset * 25f;
                
                // Final safety: Put it on ground
                if (Physics.Raycast(result + Vector3.up * 10f, Vector3.down, out var hit, 20f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    result = hit.point;
                }
            }
            
            return result;
        }

        

        

        

        

        

        private void GatherLeaderGroup(CharacterMainControl boss)
        {
            if (!_groupAnchors.TryGetValue(boss, out var anchor)) anchor = boss.transform.position;
            
            // Optimization: Use cached characters
            var controllers = new List<AICharacterController>();
            foreach(var c in _charactersCache) {
                if (c == null) continue;
                var ai = c.GetComponent<AICharacterController>();
                if (ai != null) controllers.Add(ai);
            }

            int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : boss.gameObject.scene.buildIndex;
            foreach (var ai in controllers)
            {
                if (ai == null) continue;
                if (ai.leader == boss)
                {
                    var ch = ai.GetComponent<CharacterMainControl>();
                    if (ch == null) continue;
                    var offset2D = Random.insideUnitCircle.normalized * UnityEngine.Random.Range(2.5f, 6f);
                    var pos = anchor + new Vector3(offset2D.x, 0f, offset2D.y);
                    ch.SetRelatedScene(scene);
                    // Force navmesh position to prevent invalid placement
                    if (UnityEngine.AI.NavMesh.SamplePosition(pos, out var hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        ch.SetPosition(hit.position);
                    }
                    else
                    {
                        ch.SetPosition(pos);
                    }
                }
            }
            AssignWingIndicesForLeader(boss);
        }

        private bool TryGetRandomMapPoint(Vector3 center, float minRange, float maxRange, out Vector3 pos)
        {
            return TryGetSeparatedMapPoint(center, minRange, maxRange, null, 0f, out pos);
        }

        private bool TryGetSeparatedMapPoint(Vector3 center, float minRange, float maxRange, List<Vector3>? avoidPoints, float avoidRadius, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (_pointsCache != null && _pointsCache.Count > 0)
            {
                // Filter points within range
                var candidates = new List<Points>();
                float sqrMin = minRange * minRange;
                float sqrMax = maxRange * maxRange;
                foreach (var p in _pointsCache)
                {
                    if (p == null) continue;
                    float d2 = Vector3.SqrMagnitude(p.transform.position - center);
                    if (d2 >= sqrMin && d2 <= sqrMax)
                    {
                        candidates.Add(p);
                    }
                }

                if (candidates.Count > 0)
                {
                    // Best Candidate Sampling for Uniform Distribution
                    int attempts = 15;
                    Vector3 bestCandidate = Vector3.zero;
                    float bestScore = -1f;

                    for (int i = 0; i < attempts; i++)
                    {
                        var p = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                        var candidatePos = p.GetRandomPoint();
                        if (Vector3.Distance(candidatePos, center) < minRange) continue;

                        // Calculate score based on separation
                        float score = 1000f;
                        bool valid = true;
                        
                        if (avoidPoints != null && avoidPoints.Count > 0)
                        {
                            float minSep = float.MaxValue;
                            foreach(var ap in avoidPoints)
                            {
                                float dist = Vector3.Distance(candidatePos, ap);
                                if (dist < avoidRadius) { valid = false; break; }
                                if (dist < minSep) minSep = dist;
                            }
                            if (!valid) continue;
                            score = minSep; // Prefer farthest from others
                        }

                        if (score > bestScore)
                        {
                            // Verify ground
                            var ray = new Ray(candidatePos + Vector3.up * 1.5f, Vector3.down);
                            if (Physics.Raycast(ray, out var hit, 5.0f, GameplayDataSettings.Layers.groundLayerMask))
                            {
                                bestScore = score;
                                bestCandidate = hit.point;
                            }
                        }
                    }

                    if (bestScore >= 0f)
                    {
                        pos = bestCandidate;
                        return true;
                    }
                }
            }
            return false;
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
            
            var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
            foreach (var s in waveSpawners) 
            { 
                if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                // Limit quantity to avoid overcrowding
                // s.spawnCountRange = new Vector2Int(1, 1);
            }

            // -----------------------------------

            // 2. Initial Map Metrics Calculation
            RecalculateMapMetrics();
            
            // 3. Initial Disable of any pre-existing vanilla AI
            _charactersCache.Clear();
            _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
            // DisableVanillaControllersCached();

            UniTask.Void(async () =>
            {
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
                        selected = allBossPresets.OrderBy(_ => rnd.Next()).Take(3).ToArray();
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

                            var avoid = _groupAnchors.Values.ToList();
                            
                            // Try to find a random point in target scene
                            Vector3 anchor = Vector3.zero;
                            bool found = false;
                            
                            // Filter points for target scene
                            var scenePoints = _pointsCache.Where(p => p != null && p.gameObject.scene.buildIndex == targetScene).ToList();
                            if (scenePoints.Count > 0)
                            {
                                // Try 10 times to find a valid spot
                                for(int k=0; k<10; k++)
                                {
                                    var pt = scenePoints[UnityEngine.Random.Range(0, scenePoints.Count)];
                                    var cand = pt.GetRandomPoint();
                                    // Basic distance check against other bosses
                                    bool farEnough = true;
                                    foreach(var av in avoid)
                                    {
                                        if(Vector3.Distance(cand, av) < 40f) { farEnough = false; break; }
                                    }
                                    if(farEnough)
                                    {
                                        if (Physics.Raycast(cand + Vector3.up * 2f, Vector3.down, out var hit, 5f, GameplayDataSettings.Layers.groundLayerMask))
                                        {
                                            anchor = hit.point;
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            
                            if (!found)
                {
                    anchor = GetSpawnPointOrFallback(player.transform.position, avoid);
                    targetScene = scene; // Fallback to main scene if custom placement failed
                }
                
                try
                {
                    var clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, targetScene, null, false);
                    if (clone != null)
                    {
                        // Wait longer to ensure full initialization (Animator, MagicBlend, etc.)
                        // KINEMATION MagicBlend requires Animator to be ready. 
                        // Wait until next frame or longer to avoid ArgumentNullException in PlayableHandle
                        await UniTask.Yield(PlayerLoopTiming.Update); 
                        await UniTask.Delay(1000); 
                        
                        if (clone == null) continue; // Check if destroyed during wait
                        
                        // Verify Animator before proceeding
                        var anim = clone.GetComponent<Animator>();
                        if (anim == null || !anim.isInitialized)
                        {
                             // Force simple wait if animator is lagging
                             await UniTask.Delay(500);
                        }

                        EnhanceBoss(clone).Forget();
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
