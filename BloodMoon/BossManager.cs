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
using Pathfinding;

namespace BloodMoon
{
    public class BossManager
    {
        private readonly AIDataStore _store;
        private bool _initialized;
        private readonly HashSet<CharacterMainControl> _processed = new HashSet<CharacterMainControl>();
        private readonly HashSet<CharacterMainControl> _minionDoubled = new HashSet<CharacterMainControl>();
        
        private int _currentScene = -1;
        private bool _sceneSetupDone;
        private List<Points> _pointsCache = new List<Points>();
        private float _scanCooldown;
        private float _minionRescanCooldown;
        private readonly List<CharacterMainControl> _minionsCache = new List<CharacterMainControl>();
        private int _minionCursor;
        private bool _setupRunning;
        private int _minionsPerBossCap = 5;
        private int _globalMinionCap = 20;
        private int _globalMinionSpawned = 0;
        private bool _enableMinionDoubling = false;
        private readonly List<CharacterRandomPreset> _selectedBossPresets = new List<CharacterRandomPreset>();
        private int _selectedScene = -1;
        private bool _selectionInitialized;
        private readonly Dictionary<CharacterMainControl, Vector3> _groupAnchors = new Dictionary<CharacterMainControl, Vector3>();
        private float _bloodMoonStartTime = -1f;
        private float _bloodMoonDurationSec = 900f;
        private bool _bloodMoonActive;
        private float _strategyDecayTimer;
        private readonly List<CharacterMainControl> _charactersCache = new List<CharacterMainControl>();
        private float _charactersRescanCooldown;
        private readonly System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int> _crowdGridFine = new System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int>();
        private readonly System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int> _crowdGridCoarse = new System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int>();
        private float _crowdCellFine = 2f;
        private float _crowdCellCoarse = 6f;
        private float _lastCrowdUpdateTime;
        private System.Threading.Tasks.Task? _crowdTask;

        public BossManager(AIDataStore store)
        {
            _store = store;
        }

        public async void TickBloodMoon()
        {
            if (!_initialized)
            {
                await UniTask.WaitUntil(() => LevelManager.LevelInited && CharacterMainControl.Main != null);
                _store.Load();
                RaidUtilities.OnRaidEnd += OnRaidEnded;
                RaidUtilities.OnRaidDead += OnRaidEnded;
                MultiSceneCore.OnSubSceneWillBeUnloaded += OnSubSceneWillBeUnloaded;
                MultiSceneCore.OnInstanceDestroy += OnMultiSceneDestroyed;
                _initialized = true;
                _bloodMoonStartTime = Time.time;
                _bloodMoonActive = true;
                _strategyDecayTimer = 30f;
            }
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap)
            {
                return;
            }
            if (_bloodMoonActive && _bloodMoonStartTime > 0f && Time.time - _bloodMoonStartTime > _bloodMoonDurationSec)
            {
                EndBloodMoon();
                return;
            }
            _scanCooldown -= Time.deltaTime;
            _minionRescanCooldown -= Time.deltaTime;
            if (_scanCooldown > 0f)
            {
                return;
            }
            var player = CharacterMainControl.Main;
            if (!_sceneSetupDone && !_setupRunning)
            {
                // 等待场景初始化批处理完成后再开始轻量逻辑
                return;
            }
            if (_charactersRescanCooldown <= 0f)
            {
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                _charactersRescanCooldown = 8.0f;
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
                    EnhanceBoss(c);
                    TeleportToPlayerScene(c);
                    EnableRevenge(c, player);
                    _processed.Add(c);
                }
            }
            if (_minionRescanCooldown <= 0f)
            {
                _minionsCache.Clear();
                for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i];
                    if (c == null || c.IsMainCharacter) continue;
                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (isBoss) continue;
                    if (c.Team == Teams.player) continue;
                    _minionsCache.Add(c);
                }
                _minionRescanCooldown = 3.0f;
            }
            ProcessMinionsBudgeted(player, 6);
            DisableDefaultSpawner();
            _scanCooldown = 1.0f;
            TriggerCrowdUpdate(all);
            _strategyDecayTimer -= Time.deltaTime;
            if (_strategyDecayTimer <= 0f)
            {
                _store.DecayWeights(0.98f);
                _strategyDecayTimer = 30f;
            }
        }

        private void OnRaidEnded(RaidUtilities.RaidInfo info)
        {
            _store.Save();
        }

        private void OnSubSceneWillBeUnloaded(Duckov.Scenes.MultiSceneCore core, UnityEngine.SceneManagement.Scene scene)
        {
            _store.Save();
        }

        private void OnMultiSceneDestroyed(Duckov.Scenes.MultiSceneCore core)
        {
            _store.Save();
        }

        private void EndBloodMoon()
        {
            _bloodMoonActive = false;
            _store.Save();
        }

        private void DisableDefaultSpawner()
        {
            // Disable default spawner system to prevent new waves
            var spawners = UnityEngine.Object.FindObjectsOfType<CharacterSpawnerRoot>();
            if (spawners != null)
            {
                foreach (var s in spawners)
                {
                    if (s.gameObject.activeSelf) s.gameObject.SetActive(false);
                }
            }
        }

        private void EnhanceBoss(CharacterMainControl c)
        {
            var item = c.CharacterItem;
            if (item == null) return;
            var maxHealth = item.GetStat("MaxHealth".GetHashCode());
            if (maxHealth != null) maxHealth.BaseValue *= 4f;
            Multiply(item, "WalkSpeed", 1.35f);
            Multiply(item, "RunSpeed", 1.35f);
            Multiply(item, "TurnSpeed", 1.35f);
            BoostDefense(item, true);
            var mw = item.GetStat("MaxWeight".GetHashCode());
            if (mw != null) mw.BaseValue = 1000000000f;
            EquipTopArmor(c);
            EnsureTwoGuns(c);
            EquipMeleeWeaponLevel(c, 6);
            FillBestAmmo(c);
            EnsureGunLoaded(c);
            c.Health.SetHealth(c.Health.MaxHealth);
            c.UpdateWeightState();
            c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
            var ai = c.GetComponent<AICharacterController>();
            if (ai != null) ai.enabled = false;
            c.Health.RequestHealthBar();
            AddBossGlow(c);
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

        private async void EquipTopArmor(CharacterMainControl c)
        {
            var tags = new List<Tag> { GameplayDataSettings.Tags.Helmat };
            var helCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = 6, maxQuality = 6 });
            if (helCandidates.Length > 0)
            {
                var helm = await ItemAssetsCollection.InstantiateAsync(helCandidates.GetRandom());
                c.CharacterItem.TryPlug(helm, emptyOnly: true);
            }
            tags = new List<Tag> { GameplayDataSettings.Tags.Armor };
            var armCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = 6, maxQuality = 6 });
            if (armCandidates.Length > 0)
            {
                var armor = await ItemAssetsCollection.InstantiateAsync(armCandidates.GetRandom());
                c.CharacterItem.TryPlug(armor, emptyOnly: true);
            }
            tags = new List<Tag> { GameplayDataSettings.Tags.Backpack };
            var bpCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = 6, maxQuality = 6 });
            if (bpCandidates.Length > 0)
            {
                var backpack = await ItemAssetsCollection.InstantiateAsync(bpCandidates.GetRandom());
                c.CharacterItem.TryPlug(backpack, emptyOnly: true);
            }
        }

        private async void EquipMeleeWeaponLevel(CharacterMainControl c, int level)
        {
            var meleeTag = TagUtilities.TagFromString("MeleeWeapon");
            if (meleeTag == null) return;
            var filter = new ItemFilter { requireTags = new Tag[] { meleeTag }, minQuality = level, maxQuality = level };
            var ids = ItemAssetsCollection.Search(filter);
            if (ids.Length > 0)
            {
                var item = await ItemAssetsCollection.InstantiateAsync(ids.GetRandom());
                c.CharacterItem.TryPlug(item, emptyOnly: true);
            }
        }

        private async void EnsureTwoGuns(CharacterMainControl c)
        {
            var tags = new List<Tag> { GameplayDataSettings.Tags.Gun };
            var guns = ItemAssetsCollection.Search(new ItemFilter {
                requireTags = tags.ToArray(), minQuality = 5, maxQuality = 6,
                excludeTags = new Tag[] { TagUtilities.TagFromString("GunType_PST") }
            });
            Item? prim = c.PrimWeaponSlot()?.Content;
            Item? sec = c.SecWeaponSlot()?.Content;
            if (prim == null && guns.Length > 0)
            {
                prim = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(prim, emptyOnly: true);
            }
            if (sec == null && guns.Length > 1)
            {
                sec = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(sec, emptyOnly: true);
            }
            if (prim != null && prim.Tags.Contains("GunType_PST") && guns.Length > 0)
            {
                var rep = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(rep, emptyOnly: false);
            }
            if (sec != null && sec.Tags.Contains("GunType_PST") && guns.Length > 0)
            {
                var rep = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(rep, emptyOnly: false);
            }
        }

        private async void FillBestAmmo(CharacterMainControl c)
        {
            await FillAmmoForGunMultiple(c, c.PrimWeaponSlot()?.Content, 6, 8);
            await FillAmmoForGunMultiple(c, c.SecWeaponSlot()?.Content, 6, 8);
            PurgeNonMatchingAmmo(c);
        }

        private void TeleportToPlayerScene(CharacterMainControl c)
        {
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap) return;
            var player = CharacterMainControl.Main; if (player == null) return;
            if (MultiSceneCore.MainScene.HasValue)
            {
                c.SetRelatedScene(MultiSceneCore.MainScene.Value.buildIndex);
            }
            var anchor = GetOrCreateAnchor(c, player.transform.position);
            c.SetPosition(anchor);
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
            var custom = c.gameObject.AddComponent<BloodMoonAIController>();
            custom.Init(c, _store);
            custom.SetChaseDelay(0f);
            c.PopText("我要你死无葬身之地");
        }


        private async void ProcessMinionsBudgeted(CharacterMainControl player, int budget)
        {
            if (_minionsCache.Count == 0 || budget <= 0) return;
            int processed = 0;
            int n = _minionsCache.Count;
            for (int i = 0; i < n && processed < budget; i++)
            {
                var idx = (_minionCursor + i) % n;
                var m = _minionsCache[idx];
                if (m == null) continue;
                if (!_processed.Contains(m))
                {
                    Multiply(m.CharacterItem, "WalkSpeed", 1.2f);
                    Multiply(m.CharacterItem, "RunSpeed", 1.2f);
                    await EquipArmorLevel(m, 6);
                    var mh = m.CharacterItem.GetStat("MaxHealth".GetHashCode());
                    if (mh != null) mh.BaseValue *= 2f;
                    BoostDefense(m.CharacterItem, false);
                    await EnsureNoPistolWeapons(m);
                    EquipMeleeWeaponLevel(m, 5);
                    await FillHighAmmo(m, 5);
                    EnsureGunLoaded(m);
                    m.Health.SetHealth(m.Health.MaxHealth);
                    var mw2 = m.CharacterItem.GetStat("MaxWeight".GetHashCode());
                    if (mw2 != null) mw2.BaseValue = 1000000000f;
                    m.UpdateWeightState();
                    m.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
                    var mai = m.GetComponent<AICharacterController>();
                    if (mai != null) mai.enabled = false;
                    var custom = m.gameObject.GetComponent<BloodMoonAIController>();
                    if (custom == null)
                    {
                        custom = m.gameObject.AddComponent<BloodMoonAIController>();
                        custom.Init(m, _store);
                        custom.SetChaseDelay(0f);
                    }
                    _processed.Add(m);
                    var leader = m.GetComponent<AICharacterController>()?.leader;
                    if (leader != null)
                    {
                        GatherLeaderGroup(leader);
                    }
                    processed++;
                }
                if (_enableMinionDoubling && !_minionDoubled.Contains(m))
                {
                    var p = m.characterPreset;
                    if (p != null)
                    {
                        if (_globalMinionSpawned >= _globalMinionCap) break;
                        var scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : m.gameObject.scene.buildIndex;
                        Vector3 spawnPos = GetOpenAreaAnchorNearPlayer(player);
                        var clone = await p.CreateCharacterAsync(spawnPos, Vector3.forward, scene, m.GetComponent<AICharacterController>()?.group, false);
                        if (clone != null)
                        {
                            Multiply(clone.CharacterItem, "WalkSpeed", 1.2f);
                            Multiply(clone.CharacterItem, "RunSpeed", 1.2f);
                            await EquipArmorLevel(clone, 6);
                            await FillHighAmmo(clone, 5);
                            var mhc = clone.CharacterItem.GetStat("MaxHealth".GetHashCode());
                            if (mhc != null) mhc.BaseValue *= 2f;
                            EnsureGunLoaded(clone);
                            clone.Health.SetHealth(clone.Health.MaxHealth);
                            var mwc = clone.CharacterItem.GetStat("MaxWeight".GetHashCode());
                            if (mwc != null) mwc.BaseValue = 1000000000f;
                            clone.UpdateWeightState();
                            clone.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
                            var cai = clone.GetComponent<AICharacterController>();
                            if (cai != null) cai.enabled = false;
                            var customClone = clone.gameObject.AddComponent<BloodMoonAIController>();
                            customClone.Init(clone, _store);
                            customClone.SetChaseDelay(0f);
                            var clLeader = clone.GetComponent<AICharacterController>()?.leader;
                            if (clLeader != null)
                            {
                                GatherLeaderGroup(clLeader);
                            }
                            _minionDoubled.Add(m);
                            _globalMinionSpawned++;
                            processed++;
                        }
                    }
                }
            }
            _minionCursor = (_minionCursor + processed) % Mathf.Max(1, n);
        }

        private void BoostDefense(Item item, bool isBoss)
        {
            var body = item.GetStat("BodyArmor".GetHashCode());
            var head = item.GetStat("HeadArmor".GetHashCode());
            float bodyTarget = isBoss ? 10f : 8f;
            float headTarget = isBoss ? 10f : 8f;
            if (body != null) body.BaseValue = Mathf.Max(body.BaseValue, bodyTarget);
            if (head != null) head.BaseValue = Mathf.Max(head.BaseValue, headTarget);
        }

        private async System.Threading.Tasks.Task EquipArmorLevel(CharacterMainControl c, int level)
        {
            var tags = new List<Tag> { GameplayDataSettings.Tags.Helmat };
            var helCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = level, maxQuality = level });
            if (helCandidates.Length > 0)
            {
                var helm = await ItemAssetsCollection.InstantiateAsync(helCandidates.GetRandom());
                c.CharacterItem.TryPlug(helm, emptyOnly: true);
            }
            tags = new List<Tag> { GameplayDataSettings.Tags.Armor };
            var armCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = level, maxQuality = level });
            if (armCandidates.Length > 0)
            {
                var armor = await ItemAssetsCollection.InstantiateAsync(armCandidates.GetRandom());
                c.CharacterItem.TryPlug(armor, emptyOnly: true);
            }
            tags = new List<Tag> { GameplayDataSettings.Tags.Backpack };
            var bpCandidates = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = level, maxQuality = level });
            if (bpCandidates.Length > 0)
            {
                var backpack = await ItemAssetsCollection.InstantiateAsync(bpCandidates.GetRandom());
                c.CharacterItem.TryPlug(backpack, emptyOnly: true);
            }
        }

        private async System.Threading.Tasks.Task EnsureNoPistolWeapons(CharacterMainControl c)
        {
            var tags = new List<Tag> { GameplayDataSettings.Tags.Gun };
            var guns = ItemAssetsCollection.Search(new ItemFilter {
                requireTags = tags.ToArray(), minQuality = 5, maxQuality = 6,
                excludeTags = new Tag[] { TagUtilities.TagFromString("GunType_PST") }
            });
            if (guns.Length <= 0) return;
            var prim = c.PrimWeaponSlot()?.Content;
            var sec = c.SecWeaponSlot()?.Content;
            if (prim == null || prim.Tags.Contains("GunType_PST"))
            {
                var rep = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(rep, emptyOnly: false);
            }
            if (sec != null && sec.Tags.Contains("GunType_PST"))
            {
                var rep = await ItemAssetsCollection.InstantiateAsync(guns.GetRandom());
                c.CharacterItem.TryPlug(rep, emptyOnly: false);
            }
        }

        private async System.Threading.Tasks.Task FillHighAmmo(CharacterMainControl c, int level)
        {
            await FillAmmoForGunMultiple(c, c.PrimWeaponSlot()?.Content, level, 5);
            await FillAmmoForGunMultiple(c, c.SecWeaponSlot()?.Content, level, 5);
            PurgeNonMatchingAmmo(c);
        }

        private async System.Threading.Tasks.Task FillAmmoForGun(CharacterMainControl c, Item? gun, int level)
        {
            if (gun == null) return;
            var caliber = gun.Constants?.GetString("Caliber") ?? string.Empty;
            if (string.IsNullOrEmpty(caliber)) return;
            var filter = new ItemFilter { caliber = caliber, minQuality = level, maxQuality = level, requireTags = new Tag[] { GameplayDataSettings.Tags.Bullet } };
            var ids = ItemAssetsCollection.Search(filter);
            if (ids.Length > 0)
            {
                var ammo = await ItemAssetsCollection.InstantiateAsync(ids.GetRandom());
                c.CharacterItem.Inventory.AddItem(ammo);
            }
        }

        private async System.Threading.Tasks.Task FillAmmoForGunMultiple(CharacterMainControl c, Item? gun, int level, int count)
        {
            if (gun == null || count <= 0) return;
            var caliber = gun.Constants?.GetString("Caliber") ?? string.Empty;
            if (string.IsNullOrEmpty(caliber)) return;
            var filter = new ItemFilter { caliber = caliber, minQuality = level, maxQuality = level, requireTags = new Tag[] { GameplayDataSettings.Tags.Bullet } };
            var ids = ItemAssetsCollection.Search(filter);
            if (ids.Length <= 0) return;
            for (int i = 0; i < count; i++)
            {
                var ammo = await ItemAssetsCollection.InstantiateAsync(ids.GetRandom());
                c.CharacterItem.Inventory.AddItem(ammo);
            }
        }

        private void EnsureGunLoaded(CharacterMainControl c)
        {
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
                    gs.AutoSetTypeInInventory(inv);
                }
                gs.LoadBulletsFromInventory(inv).Forget();
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

        private async UniTask EnsureWanderersForLonelyBosses()
        {
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap) return;
            var bosses = new List<(CharacterMainControl boss, AICharacterController? ai)>();
            // Optimization: Use cached characters list instead of FindObjectsOfType
            foreach (var c in _charactersCache)
            {
                if (c == null || c.IsMainCharacter) continue;
                var preset = c.characterPreset;
                bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                if (isBoss)
                {
                    bosses.Add((c, c.GetComponent<AICharacterController>()));
                }
            }
            // Optimization: Use FindObjectsOfType only for controllers if strictly necessary, 
            // or iterate _charactersCache to get components.
            var controllers = new List<AICharacterController>();
            foreach(var c in _charactersCache) {
                if (c == null) continue;
                var ai = c.GetComponent<AICharacterController>();
                if (ai != null) controllers.Add(ai);
            }

            int referenceCount = 0;
            foreach (var b in bosses)
            {
                int count = 0;
                foreach (var ai in controllers)
                {
                    if (ai == null || ai == b.ai) continue;
                    if (ai.leader == b.boss)
                    {
                        count++;
                    }
                }
                referenceCount = Mathf.Max(referenceCount, count);
            }
            if (referenceCount <= 0)
            {
                referenceCount = 1;
            }
            referenceCount = Mathf.Min(referenceCount, _minionsPerBossCap);
            CharacterRandomPreset? wanderer = null;
            foreach (var p in GameplayDataSettings.CharacterRandomPresetData.presets)
            {
                if (p == null) continue;
                var icon = p.GetCharacterIcon();
                if (icon != GameplayDataSettings.UIStyle.BossCharacterIcon)
                {
                    wanderer = p;
                    break;
                }
            }
            if (wanderer == null) return;
            foreach (var b in bosses)
            {
                int currentCount = 0;
                foreach (var ai in controllers)
                {
                    if (ai == null || ai == b.ai) continue;
                    if (ai.leader == b.boss)
                    {
                        currentCount++;
                    }
                }
                if (currentCount >= referenceCount) continue;
                for (int i = 0; i < referenceCount - currentCount; i++)
                {
                    if (_globalMinionSpawned >= _globalMinionCap) break;
                    var boss = b.boss;
                    var anchor = GetOrCreateAnchor(boss, CharacterMainControl.Main.transform.position);
                    
                    // Try to find a valid spawn position around the anchor
                    Vector3 pos = anchor;
                    bool foundValid = false;
                    var ground = GameplayDataSettings.Layers.groundLayerMask;

                    for (int k = 0; k < 10; k++)
                    {
                        var offset2D = Random.insideUnitCircle.normalized * UnityEngine.Random.Range(2f, 6f);
                        var candidate = anchor + new Vector3(offset2D.x, 0f, offset2D.y);
                        
                        // 1. Snap to ground
                        if (Physics.Raycast(candidate + Vector3.up * 2.0f, Vector3.down, out var hit, 4.0f, ground))
                        {
                            candidate = hit.point;
                        }
                        else if (Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.down, out var hit2, 2.0f, ground))
                        {
                            candidate = hit2.point;
                        }
                        else
                        {
                             continue; // No ground found
                        }

                        // 2. Check if open area (NavMesh + Collision)
                        if (IsOpenArea(candidate, 1.0f))
                        {
                            pos = candidate;
                            foundValid = true;
                            break;
                        }
                    }
                    
                    if (!foundValid)
                    {
                        // Fallback: spawn exactly at anchor (boss position) if no space around
                         pos = anchor;
                    }

                    int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : boss.gameObject.scene.buildIndex;
                    var clone = await wanderer.CreateCharacterAsync(pos, Vector3.forward, scene, null, false);
                    if (clone != null)
                    {
                        var ai = clone.GetComponent<AICharacterController>();
                        if (ai != null) ai.enabled = false;
                        Multiply(clone.CharacterItem, "WalkSpeed", 1.2f);
                        Multiply(clone.CharacterItem, "RunSpeed", 1.2f);
                        await EquipArmorLevel(clone, 6);
                        BoostDefense(clone.CharacterItem, false);
                        await EnsureNoPistolWeapons(clone);
                        EquipMeleeWeaponLevel(clone, 5);
                        await FillHighAmmo(clone, 5);
                        var mhc2 = clone.CharacterItem.GetStat("MaxHealth".GetHashCode());
                        if (mhc2 != null) mhc2.BaseValue *= 3f;
                        EnsureGunLoaded(clone);
                        clone.Health.SetHealth(clone.Health.MaxHealth);
                        var mwc2 = clone.CharacterItem.GetStat("MaxWeight".GetHashCode());
                        if (mwc2 != null) mwc2.BaseValue = 1000000000f;
                        clone.UpdateWeightState();
                        clone.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
                        var cai2 = clone.GetComponent<AICharacterController>();
                        if (cai2 != null) cai2.enabled = false;
                        var custom = clone.gameObject.AddComponent<BloodMoonAIController>();
                        custom.Init(clone, _store);
                        custom.SetChaseDelay(0f);
                        custom.SetWingAssignment(b.boss, i);
                        GatherLeaderGroup(b.boss);
                        _globalMinionSpawned++;
                        _charactersCache.Add(clone);
                    }
                }
            }
        }

        private void AssignWingIndicesForLeader(CharacterMainControl boss)
        {
            var controllers = UnityEngine.Object.FindObjectsOfType<AICharacterController>();
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
            if (!TryGetRandomMapPoint(out pos))
            {
                pos = GetOpenAreaNear(fallbackCenter, 30f, 50f);
            }
            if (!IsOpenArea(pos, 3f)) pos = GetOpenAreaNear(fallbackCenter, 30f, 50f);
            _groupAnchors[boss] = pos;
            return pos;
        }

        private Vector3 GetOpenAreaAnchorNearPlayer(CharacterMainControl player)
        {
            return SelectOpenAreaParallel(player.transform.position, 20, 45f, 80f, player.transform.position);
        }

        private Vector3 GetOpenAreaNear(Vector3 center, float minDist, float maxDist)
        {
            return SelectOpenAreaParallel(center, 24, minDist, maxDist);
        }

        private Vector3 SelectOpenAreaParallel(Vector3 center, int count, float minDist, float maxDist, Vector3? avoidVisibilityFrom = null)
        {
            var candidates = new Vector3[count];
            var ground = GameplayDataSettings.Layers.groundLayerMask;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos;
                if (!TryGetRandomMapPoint(out pos))
                {
                    float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                    float r = UnityEngine.Random.Range(minDist, maxDist);
                    // Initial guess: assume same height as player
                    pos = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                    
                    // Attempt to snap to ground near this point
                    // Cast UP from low and DOWN from high to find floor
                    if (Physics.Raycast(pos + Vector3.up * 2.0f, Vector3.down, out var hit, 4.0f, ground))
                    {
                        pos = hit.point;
                    }
                    else if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var hit2, 2.0f, ground))
                    {
                        pos = hit2.point;
                    }
                    // If no ground found, IsOpenArea will reject it later anyway
                }
                candidates[i] = pos;
            }
            var heats = _store.ComputeHeatBatch(candidates, Time.time, 6f);
            var scores = new float[count];
            System.Threading.Tasks.Parallel.For(0, count, i =>
            {
                var pos = candidates[i];
                float baseOpen = 1f;
                float heatPenalty = heats[i];
                int crowd = GetCrowdDensityApprox(pos, 8f);
                float crowdPenalty = crowd * 0.15f;
                scores[i] = baseOpen - crowdPenalty - Mathf.Clamp01(heatPenalty);
            });
            var indices = new int[count]; for (int i = 0; i < count; i++) indices[i] = i;
            System.Array.Sort(indices, (a, b) => scores[b].CompareTo(scores[a]));
            int validate = Mathf.Min(5, count);
            var wallMask = GameplayDataSettings.Layers.fowBlockLayers;
            for (int i = 0; i < validate; i++)
            {
                var p = candidates[indices[i]];
                
                // Visibility Check (Avoid spawning in plain sight)
                if (avoidVisibilityFrom.HasValue)
                {
                    var from = avoidVisibilityFrom.Value + Vector3.up * 1.6f; // Eye level
                    var to = p + Vector3.up * 1.5f; // Enemy head level
                    var dir = to - from;
                    // If Raycast DOES NOT hit a wall, it means we HAVE line of sight (Bad)
                    // Use FOW layers to ensure we don't spawn behind a "see-through" wall
                    if (!Physics.Raycast(from, dir.normalized, dir.magnitude, wallMask))
                    {
                        continue; 
                    }
                }

                if (IsOpenArea(p, 3f)) return p;
            }
            return candidates[indices[0]];
        }

        private void TriggerCrowdUpdate(System.Collections.Generic.List<CharacterMainControl> all)
        {
            if (_crowdTask != null)
            {
                if (_crowdTask.IsCompleted)
                {
                    _lastCrowdUpdateTime = Time.time;
                    _crowdTask = null;
                }
                return;
            }
            if (Time.time - _lastCrowdUpdateTime < 0.5f) return;
            var snap = new System.Collections.Generic.List<Vector3>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c.IsMainCharacter) continue;
                snap.Add(c.transform.position);
            }
            _crowdTask = System.Threading.Tasks.Task.Run(() =>
            {
                var gridFine = new System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int>();
                var gridCoarse = new System.Collections.Generic.Dictionary<UnityEngine.Vector2Int, int>();
                float cf = _crowdCellFine; float cc = _crowdCellCoarse;
                for (int i = 0; i < snap.Count; i++)
                {
                    var p = snap[i];
                    var kf = new UnityEngine.Vector2Int(Mathf.FloorToInt(p.x / cf), Mathf.FloorToInt(p.z / cf));
                    var kc = new UnityEngine.Vector2Int(Mathf.FloorToInt(p.x / cc), Mathf.FloorToInt(p.z / cc));
                    gridFine.TryGetValue(kf, out var vf); gridFine[kf] = vf + 1;
                    gridCoarse.TryGetValue(kc, out var vc); gridCoarse[kc] = vc + 1;
                }
                lock (_crowdGridFine)
                {
                    _crowdGridFine.Clear();
                    foreach (var kv in gridFine) _crowdGridFine[kv.Key] = kv.Value;
                    _crowdGridCoarse.Clear();
                    foreach (var kv in gridCoarse) _crowdGridCoarse[kv.Key] = kv.Value;
                }
            });
        }

        private int GetCrowdDensityApprox(Vector3 pos, float radius)
        {
            int fine = 0, coarse = 0;
            float cellFine = _crowdCellFine;
            float cellCoarse = _crowdCellCoarse;
            bool indoor = !IsOpenArea(pos, 3f);
            int rxFine = Mathf.CeilToInt(radius / cellFine);
            int rxCoarse = Mathf.CeilToInt(radius / cellCoarse);
            var baseKeyFine = new UnityEngine.Vector2Int(Mathf.FloorToInt(pos.x / cellFine), Mathf.FloorToInt(pos.z / cellFine));
            var baseKeyCoarse = new UnityEngine.Vector2Int(Mathf.FloorToInt(pos.x / cellCoarse), Mathf.FloorToInt(pos.z / cellCoarse));
            lock (_crowdGridFine)
            {
                for (int dx = -rxFine; dx <= rxFine; dx++)
                {
                    for (int dz = -rxFine; dz <= rxFine; dz++)
                    {
                        var kf = new UnityEngine.Vector2Int(baseKeyFine.x + dx, baseKeyFine.y + dz);
                        if (_crowdGridFine.TryGetValue(kf, out var vf)) fine += vf;
                    }
                }
            }
            lock (_crowdGridCoarse)
            {
                for (int dx = -rxCoarse; dx <= rxCoarse; dx++)
                {
                    for (int dz = -rxCoarse; dz <= rxCoarse; dz++)
                    {
                        var kc = new UnityEngine.Vector2Int(baseKeyCoarse.x + dx, baseKeyCoarse.y + dz);
                        if (_crowdGridCoarse.TryGetValue(kc, out var vc)) coarse += vc;
                    }
                }
            }
            float heat = _store.GetHeatAt(pos, Time.time, 6f);
            float alphaBase = indoor ? 0.6f : 0.2f;
            float alpha = Mathf.Clamp01(alphaBase + Mathf.Clamp01(heat));
            int blended = Mathf.RoundToInt(alpha * fine + (1f - alpha) * coarse);
            return blended;
        }

        private bool IsOpenArea(Vector3 pos, float radius)
        {
            var wall = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            
            // 1. Immediate Collision Check (Anti-Stuck)
            // Ensure we are not spawning inside a box, wall, or prop
            if (Physics.CheckSphere(pos + Vector3.up * 0.8f, 0.4f, wall)) return false;

            // Check against known Stuck Spots
            if (_store.IsStuckSpot(pos, 2.5f)) return false;

            // 2. NavMesh / Walkability Check
            // Ensure the point is on a valid NavMesh node
            if (AstarPath.active != null)
            {
                var nnInfo = AstarPath.active.GetNearest(pos, NNConstraint.Walkable);
                if (nnInfo.node == null || !nnInfo.node.Walkable) return false;
                
                // If the nearest navmesh point is too far (e.g. through a wall), reject it
                // Vertical distance check is important for multi-floor
                if (Mathf.Abs(nnInfo.position.y - pos.y) > 1.5f) return false;
                if (Vector3.Distance(nnInfo.position, pos) > 2.0f) return false;
            }

            var ground = GameplayDataSettings.Layers.groundLayerMask;
            
            // 3. Ground Snapping (Low Raycast)
            // Instead of casting from 15m (which hits ceiling), cast from 1.5m
            if (!Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out var hit, 4.0f, ground)) return false;
            
            // 4. Horizontal Clearance Check
            int blocked = 0;
            for (int k = 0; k < 8; k++) // Reduced from 12 to 8 for perf
            {
                float ang = k * 45f * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                // Reduced check distance from 6.5f to 2.5f (AI doesn't need THAT much space)
                if (Physics.Raycast(pos + Vector3.up * 1.0f, dir, 2.5f, wall)) blocked++;
            }
            if (blocked > 2) return false;

            // 5. Slope Check
            float slope = Vector3.Angle(hit.normal, Vector3.up);
            if (slope > 25f) return false;

            // 6. Locked Room Check (NavMesh Connectivity)
            // If the path from player to this point is blocked (e.g. locked door), do not spawn here.
            // This prevents spawning inside locked key rooms.
            if (AstarPath.active != null && CharacterMainControl.Main != null)
            {
                var node1 = AstarPath.active.GetNearest(pos, NNConstraint.Walkable).node;
                var node2 = AstarPath.active.GetNearest(CharacterMainControl.Main.transform.position, NNConstraint.Walkable).node;
                
                if (node1 != null && node2 != null)
                {
                    // Check if area is accessible from player's current area
                    if (!PathUtilities.IsPathPossible(node1, node2)) return false;
                }
            }

            return true;
        }

        private int CountUnitsNear(Vector3 pos, float radius)
        {
            int count = 0;
            for (int i = 0; i < _charactersCache.Count; i++)
            {
                var c = _charactersCache[i];
                if (c == null || c.IsMainCharacter) continue;
                if (Vector3.Distance(pos, c.transform.position) <= radius) count++;
            }
            return count;
        }

        private void GatherLeaderGroup(CharacterMainControl boss)
        {
            if (!_groupAnchors.TryGetValue(boss, out var anchor)) anchor = boss.transform.position;
            if (!IsOpenArea(anchor, 3f))
            {
                anchor = GetOpenAreaNear(anchor, 10f, 25f);
                _groupAnchors[boss] = anchor;
            }
            var controllers = UnityEngine.Object.FindObjectsOfType<AICharacterController>();
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
                    ch.SetPosition(pos);
                }
            }
            AssignWingIndicesForLeader(boss);
        }

        private bool TryGetRandomMapPoint(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (_pointsCache != null && _pointsCache.Count > 0)
            {
                var p = _pointsCache[UnityEngine.Random.Range(0, _pointsCache.Count)];
                pos = p.GetRandomPoint();
                // Lowered raycast height from 10f to 1.5f for indoor compatibility
                var ray = new Ray(pos + Vector3.up * 1.5f, Vector3.down);
                if (Physics.Raycast(ray, out var hit, 5.0f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    pos = hit.point;
                }
                return true;
            }
            
            // Fallback: Use NavMesh Random Point if Points are missing
            if (AstarPath.active != null)
            {
                // Try 10 times to find a valid node
                for(int i=0; i<10; i++) 
                {
                    var rnd = UnityEngine.Random.insideUnitCircle * 100f;
                    var p = new Vector3(rnd.x, 0f, rnd.y);
                    var nn = AstarPath.active.GetNearest(p, NNConstraint.Walkable);
                    if (nn.node != null && nn.node.Walkable)
                    {
                        pos = (Vector3)nn.position;
                        return true;
                    }
                }
            }
            
            return false;
        }


        public void StartSceneSetupParallel()
        {
            if (!LevelManager.Instance || !LevelManager.Instance.IsRaidMap) return;
            if (_setupRunning) return;
            UniTask.Void(async () =>
            {
                _setupRunning = true;
                await UniTask.WaitUntil(() => LevelManager.LevelInited && CharacterMainControl.Main != null);
                var player = CharacterMainControl.Main;
                int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : SceneManager.GetActiveScene().buildIndex;
                _processed.Clear();
                _minionDoubled.Clear();
                _pointsCache.Clear();
                _pointsCache.AddRange(UnityEngine.Object.FindObjectsOfType<Points>());
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());

                _currentScene = scene;
                _sceneSetupDone = false;
                
                var allBossPresets = GameplayDataSettings.CharacterRandomPresetData.presets
                    .Where(p => p != null && p.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon)
                    .ToArray();

                CharacterRandomPreset[] selected = System.Array.Empty<CharacterRandomPreset>();

                await Task.Run(() =>
                {
                    var rnd = new System.Random();
                    selected = allBossPresets.OrderBy(_ => rnd.Next()).Take(3).ToArray();
                });

                _selectedBossPresets.Clear();
                _selectedBossPresets.AddRange(selected);
                _selectedScene = scene;
                _selectionInitialized = true;

                foreach (var preset in _selectedBossPresets)
                {
                    bool exists = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>().Any(c => c.characterPreset == preset);
                    if (!exists)
                    {
                        var anchor = GetOpenAreaAnchorNearPlayer(player);
                        var clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, scene, null, false);
                        if (clone != null)
                        {
                            EnhanceBoss(clone);
                            EnableRevenge(clone, player);
                            _processed.Add(clone);
                            _groupAnchors[clone] = anchor;
                            _charactersCache.Add(clone);
                        }
                    }
                }

                await EnsureWanderersForLonelyBosses();
                _sceneSetupDone = true;
                _setupRunning = false;
            });
        }

    }
}
