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
        private float _minionRescanCooldown;
        private readonly List<CharacterMainControl> _minionsCache = new List<CharacterMainControl>();
        private int _minionCursor;
        private bool _setupRunning;
        private int _minionsPerBossCap = 5;
        private int _globalMinionCap = 20;
        private int _globalMinionSpawned = 0;
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

        private bool _wanderersSetupDone;
        private void DisableVanillaControllersCached()
        {
            for (int i = 0; i < _charactersCache.Count; i++)
            {
                var c = _charactersCache[i];
                if (c == null || c.IsMainCharacter) continue;
                var ai = c.GetComponent<AICharacterController>();
                if (ai != null) ai.enabled = false;
            }
        }

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
                // Simple optimization: only scan if we are not overloaded
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                _charactersRescanCooldown = 10.0f;
                DisableVanillaControllersCached();
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

        private async void EnhanceBoss(CharacterMainControl c)
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
            if (mw != null) mw.BaseValue = 1000f;
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
            await EnsureMedicalSupplies(c, 3);
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
            await EquipBackpackById(c, 40);
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


        private async System.Threading.Tasks.Task EnsureMedicalSupplies(CharacterMainControl c, int count)
        {
            var tags = new List<Tag> { TagUtilities.TagFromString("Drug") };
            var meds = ItemAssetsCollection.Search(new ItemFilter { requireTags = tags.ToArray(), minQuality = 4, maxQuality = 6 });
            if (meds.Length > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(meds.GetRandom());
                    if (item != null)
                    {
                        c.CharacterItem.Inventory.AddItem(item);
                    }
                }
            }
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
                    await EnsureMedicalSupplies(m, 2);
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
            await EquipBackpackById(c, 40);
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

        private async System.Threading.Tasks.Task EquipBackpackById(CharacterMainControl c, int id)
        {
            var item = await ItemAssetsCollection.InstantiateAsync(id);
            if (item != null)
            {
                c.CharacterItem.TryPlug(item, emptyOnly: false);
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
            if (_wanderersSetupDone) return;
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
                    Vector3 pos = GetSpawnPointOrFallback(anchor);

                    int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : boss.gameObject.scene.buildIndex;
                    var clone = await wanderer.CreateCharacterAsync(pos, Vector3.forward, scene, null, false);
                    if (clone != null)
                    {
                        var ai = clone.GetComponent<AICharacterController>();
                        if (ai != null) { ai.enabled = false; ai.leader = b.boss; }
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
                        if (mwc2 != null) mwc2.BaseValue = 1000f;
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
            _wanderersSetupDone = true;
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
                pos = GetSpawnPointOrFallback(fallbackCenter);
            }
            _groupAnchors[boss] = pos;
            return pos;
        }

        private Vector3 GetSpawnPointOrFallback(Vector3 fallback)
        {
            if (TryGetRandomMapPoint(out var p)) return p;
            var result = fallback;
            var player = CharacterMainControl.Main;
            if (player != null)
            {
                var playerPos = player.transform.position;
                float minR = 20f;
                float maxR = 35f;
                float dist = Vector3.Distance(fallback, playerPos);
                if (dist < minR + 2f)
                {
                    var back = -player.transform.forward;
                    if (back.sqrMagnitude < 0.01f)
                    {
                        back = (fallback - playerPos).sqrMagnitude > 0.01f ? (fallback - playerPos).normalized : Vector3.back;
                    }
                    float r = UnityEngine.Random.Range(minR, maxR);
                    var candidate = playerPos + back.normalized * r;
                    var ground = GameplayDataSettings.Layers.groundLayerMask;
                    if (Physics.Raycast(candidate + Vector3.up * 2f, Vector3.down, out var hit, 4f, ground))
                    {
                        candidate = hit.point;
                    }
                    result = candidate;
                    var mask = GameplayDataSettings.Layers.fowBlockLayers;
                    var from = playerPos + Vector3.up * 1.6f;
                    var to = result + Vector3.up * 1.2f;
                    var dir = to - from;
                    if (!Physics.Raycast(from, dir.normalized, dir.magnitude, mask))
                    {
                        var perp = Vector3.Cross(back, Vector3.up).normalized;
                        candidate = playerPos + (back + perp * (UnityEngine.Random.value < 0.5f ? 1f : -1f)).normalized * r;
                        if (Physics.Raycast(candidate + Vector3.up * 2f, Vector3.down, out var hit2, 4f, ground))
                        {
                            candidate = hit2.point;
                        }
                        result = candidate;
                    }
                }
            }
            return result;
        }

        

        

        

        

        

        private void GatherLeaderGroup(CharacterMainControl boss)
        {
            if (!_groupAnchors.TryGetValue(boss, out var anchor)) anchor = boss.transform.position;
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
                _pointsCache.Clear();
                _pointsCache.AddRange(UnityEngine.Object.FindObjectsOfType<Points>());
                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                _wanderersSetupDone = false;
                DisableVanillaControllersCached();

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
                        var anchor = GetSpawnPointOrFallback(player.transform.position);
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
                DisableVanillaControllersCached();
                _sceneSetupDone = true;
                _setupRunning = false;
            });
        }

    }
}
