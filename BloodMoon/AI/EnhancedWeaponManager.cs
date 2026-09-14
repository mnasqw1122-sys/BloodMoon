using System.Collections.Generic;
using Duckov;
using Duckov.ItemUsage;
using ItemStatsSystem;
using Duckov.Utilities;
using Cysharp.Threading.Tasks;
using UnityEngine;
using System.Linq;

namespace BloodMoon.AI
{
    public class EnhancedWeaponManager
    {
        private static EnhancedWeaponManager? _instance;
        public static EnhancedWeaponManager Instance => _instance ??= new EnhancedWeaponManager();

        private static readonly int[] FALLBACK_MELEE_IDS = { 
            1172, 240, 1096, 784, 1208, 683, 735, 658, 655, 254, 682, 1286, 1287, 786, 1173,
            1174, 1248, 680, 258, 305, 238, 1074, 1095, 652, 653, 657, 659, 250, 252, 256,
            260, 327, 357, 681, 734, 737, 780, 782, 787, 788
        };
        
        private static readonly int[] FALLBACK_GUN_IDS = { 
            246, 781, 656, 327, 357, 681, 734, 256, 260, 305, 680, 682, 737, 780
        };
        
        private List<int> _cachedMeleeIds = new List<int>();
        private List<int> _cachedGunIds = new List<int>();

        /// <summary>已构建好的枪械武器池（只读，供 WeaponCache 复用，避免重复全表搜索）</summary>
        public IReadOnlyList<int> CachedGunIds => _cachedGunIds;

        /// <summary>已构建好的近战武器池（只读）</summary>
        public IReadOnlyList<int> CachedMeleeIds => _cachedMeleeIds;
        private bool _initialized = false;
        private bool _isInitializing = false;
        
        private Dictionary<string, int> _ammoGenerationFailures = new Dictionary<string, int>();

        /// <summary>补弹幂等记录（P1-8）：角色 InstanceID → 上次补弹时间</summary>
        private readonly Dictionary<int, float> _lastAmmoGrant = new Dictionary<int, float>();

        /// <summary>
        /// 配装锁（修复 P1-2）：BossManager.EnsureMinionHasWeapons 与
        /// BloodMoonAIController.FindWeaponsAsync 会在同一帧先后 fire-and-forget 执行，
        /// 两条管线都会"查槽位 → 生成 → 入包 → 插槽 → 补弹"，导致同一角色拿到双份武器/弹药。
        /// 用它做互斥，只允许一条管线配装。
        /// </summary>
        private static readonly HashSet<int> _loadoutInProgress = new HashSet<int>();

        public static bool TryBeginLoadout(CharacterMainControl? character, string requester)
        {
            if (character == null) return false;
            int id = character.GetInstanceID();
            if (_loadoutInProgress.Contains(id))
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Loadout already in progress, {requester} skips ({character.name})");
                return false;
            }
            _loadoutInProgress.Add(id);
            return true;
        }

        public static void EndLoadout(CharacterMainControl? character)
        {
            if (character == null) return;
            _loadoutInProgress.Remove(character.GetInstanceID());
        }

        /// <summary>
        /// 资产 prefab 是否是真正的武器（修复 P0-8 根因）。
        /// ItemAgent_* 是运行时生成的，资产上只有 ItemSetting_*；两者都查一遍最稳。
        /// 另外套用武器池过滤（动物/植物/魔法/任务/玩家变体等，见 WeaponPoolFilter）。
        /// </summary>
        private static bool IsWeaponPrefab(Item? prefab, bool requireGun)
        {
            if (prefab == null) return false;
            if (!WeaponPoolFilter.IsAllowedPrefab(prefab)) return false;
            if (requireGun)
            {
                return prefab.GetComponentInChildren<ItemSetting_Gun>(true) != null
                    || prefab.GetComponentInChildren<ItemAgent_Gun>(true) != null;
            }
            return prefab.GetComponentInChildren<ItemSetting_MeleeWeapon>(true) != null
                || prefab.GetComponentInChildren<ItemAgent_MeleeWeapon>(true) != null;
        }

        /// <summary>
        /// 实例化出来的物品是否是**可用**的武器（修复 P0-8）。
        /// 关键背景：ItemAssetsCollection.InstantiateAsync 在 ID 找不到时**不返回 null**，
        /// 而是返回名为 FallbackItem_{id} 的裸壳 Item（ItemAssetsCollection.cs:238-243），
        /// 原先把这种空壳当武器交给 AI → 手持打不出子弹的假枪，日志还打印"成功"。
        /// 这里同时套用武器池过滤：tag 搜索与硬编码 ID 只能拿到 ID，必须看实例名字兜底。
        /// </summary>
        public static bool IsUsableWeapon(Item? item, bool requireGun)
        {
            if (item == null) return false;
            if (item.name != null && item.name.StartsWith("FallbackItem_")) return false;
            if (!WeaponPoolFilter.IsAllowedItem(item)) return false;
            return IsWeaponPrefabStructural(item, requireGun);
        }

        /// <summary>只做结构判定（不看武器池策略），供过滤与校验分离使用</summary>
        private static bool IsWeaponPrefabStructural(Item? item, bool requireGun)
        {
            if (item == null) return false;
            if (requireGun)
            {
                return item.GetComponentInChildren<ItemSetting_Gun>(true) != null
                    || item.GetComponentInChildren<ItemAgent_Gun>(true) != null;
            }
            return item.GetComponentInChildren<ItemSetting_MeleeWeapon>(true) != null
                || item.GetComponentInChildren<ItemAgent_MeleeWeapon>(true) != null;
        }

        /// <summary>物品结构上是不是枪（不看武器池策略）。修正"刚实例化的物品还没有 ItemAgent_*"的问题。</summary>
        public static bool LooksLikeGun(Item? item) => IsWeaponPrefabStructural(item, requireGun: true);

        /// <summary>物品结构上是不是近战武器（不看武器池策略）</summary>
        public static bool LooksLikeMelee(Item? item) => IsWeaponPrefabStructural(item, requireGun: false);

        /// <summary>
        /// 把武器交给调用方之前的统一出口：顺手修复该物品的自定义数据结构错误
        /// （Int/Float/Bool 声明但字节缺失，会让游戏打印 "Index was out of range" 并把数量读成 0）
        /// </summary>
        private static Item Accept(Item item)
        {
            if (item != null) BloodMoon.Utils.ItemDataSanitizer.Sanitize(item);
            return item!;
        }

        public async UniTask EnsureInitialized()
        {
            if (_initialized) return;
            if (_isInitializing) 
            {
                await UniTask.WaitUntil(() => _initialized || !_isInitializing);
                return;
            }
            
            _isInitializing = true;
            try
            {
                await InitializeInternal();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private async UniTask InitializeInternal()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Starting initialization...");
                
                await TryInitializeFromCollection();
                
                if (!_initialized)
                {
                    TryInitializeFromTags();
                }
                
                if (!_initialized)
                {
                    TryInitializeFromHardcoded();
                }
                
                if (_initialized)
                {
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Initialized successfully. Found {_cachedMeleeIds.Count} Melee Weapons and {_cachedGunIds.Count} Guns.");
                    // 打一次武器池过滤统计：确认动物/植物/魔法/任务/玩家变体确实被排除
                    WeaponPoolFilter.DumpBlockedSummaryOnce();
                }
                else
                {
                    BloodMoon.Utils.Logger.Error("[EnhancedWeaponManager] All initialization strategies failed!");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Initialization Failed: {ex}");
            }
        }

        private async UniTask TryInitializeFromCollection()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying to initialize from ItemAssetsCollection...");
                
                await UniTask.Delay(1000);
                
                var collection = ItemAssetsCollection.Instance;
                if (collection == null)
                {
                    BloodMoon.Utils.Logger.Warning("[EnhancedWeaponManager] ItemAssetsCollection.Instance is null");
                    return;
                }
                
                if (collection.entries == null)
                {
                    BloodMoon.Utils.Logger.Warning("[EnhancedWeaponManager] ItemAssetsCollection.entries is null");
                    return;
                }
                
                int scannedCount = 0;
                int meleeFound = 0;
                int gunFound = 0;
                
                foreach (var entry in collection.entries)
                {
                    scannedCount++;
                    if (entry == null || entry.prefab == null) continue;

                    // 修复 P0-8 的根因：ItemAgent_Gun/ItemAgent_MeleeWeapon 是**运行时**由
                    // ItemAgent_Gun.BuildAgent()（ItemAgent_Gun.cs:1476）挂上去的，
                    // 资产 prefab 上并不存在 → 原来用 GetComponent<ItemAgent_*>() 判定，
                    // 1569 条里一条都认不出来（实机日志："Scanned 1569 entries but found no weapons"）。
                    // 正确信号是资产上的 ItemSetting_* 组件（ItemSettingBase : MonoBehaviour，与 Item 同物体）。
                    if (IsWeaponPrefab(entry.prefab, requireGun: false))
                    {
                        if (!_cachedMeleeIds.Contains(entry.typeID))
                        {
                            _cachedMeleeIds.Add(entry.typeID);
                            meleeFound++;
                        }
                    }
                    else if (IsWeaponPrefab(entry.prefab, requireGun: true))
                    {
                        if (!_cachedGunIds.Contains(entry.typeID))
                        {
                            _cachedGunIds.Add(entry.typeID);
                            gunFound++;
                        }
                    }
                }
                
                if (meleeFound > 0 || gunFound > 0)
                {
                    _initialized = true;
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Scanned {scannedCount} entries, found {meleeFound} melee and {gunFound} gun IDs");
                }
                else
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Scanned {scannedCount} entries but found no weapons");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Collection scan failed: {ex}");
            }
        }

        private void TryInitializeFromTags()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying to initialize from tags...");
                
                Tag? meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Melee");
                if (meleeTag == null) meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Weapon");
                
                if (meleeTag != null)
                {
                    var filter = new ItemFilter
                    {
                        minQuality = 1,
                        maxQuality = 5,
                        requireTags = new Tag[] { meleeTag }
                    };
                    
                    int[]? ids = null;
                    try 
                    {
                        ids = ItemAssetsCollection.Search(filter);
                        if (ids != null && ids.Length > 0)
                        {
                            _cachedMeleeIds.AddRange(ids);
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Found {ids.Length} melee weapons via tag search");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Melee tag search failed: {ex}");
                    }
                }
                
                Tag? gunTag = GameplayDataSettings.Tags.Gun;
                if (gunTag != null)
                {
                    var filter = new ItemFilter
                    {
                        minQuality = 1,
                        maxQuality = 6,
                        requireTags = new Tag[] { gunTag }
                    };
                    
                    int[]? ids = null;
                    try
                    {
                        ids = ItemAssetsCollection.Search(filter);
                        if (ids != null && ids.Length > 0)
                        {
                            _cachedGunIds.AddRange(ids);
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Found {ids.Length} guns via tag search");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Gun tag search failed: {ex}");
                    }
                }
                
                if (_cachedMeleeIds.Count > 0 || _cachedGunIds.Count > 0)
                {
                    _initialized = true;
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Tag initialization failed: {ex}");
            }
        }

        private void TryInitializeFromHardcoded()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying to initialize from hardcoded IDs...");
                
                _cachedMeleeIds.AddRange(FALLBACK_MELEE_IDS);
                _cachedGunIds.AddRange(FALLBACK_GUN_IDS);
                
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Added {FALLBACK_MELEE_IDS.Length} hardcoded melee IDs and {FALLBACK_GUN_IDS.Length} hardcoded gun IDs");
                
                _initialized = true;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Hardcoded initialization failed: {ex}");
            }
        }


        public async UniTask<Item?> SpawnRandomMeleeWeapon()
        {
            await EnsureInitialized();

            if (_cachedMeleeIds.Count > 0)
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying to spawn melee from cache ({_cachedMeleeIds.Count} IDs available)");
                
                int attempts = Mathf.Min(15, _cachedMeleeIds.Count * 2);
                for (int i = 0; i < attempts; i++) 
                {
                    if (_cachedMeleeIds.Count == 0) break;
                    
                    int id = _cachedMeleeIds[Random.Range(0, _cachedMeleeIds.Count)];
                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (IsUsableWeapon(item, requireGun: false))
                        {
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned melee weapon ID: {id}");
                            return Accept(item);
                        }
                        else if (item != null)
                        {
                            BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] ID {id} is not a usable melee weapon (shell={item.name}), discarded");
                            Object.Destroy(item.gameObject);
                            if (_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Remove(id);
                            if (!_cachedGunIds.Contains(id)) _cachedGunIds.Add(id);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate melee ID {id}: {ex.Message}");
                        if (_cachedMeleeIds.Contains(id))
                        {
                            _cachedMeleeIds.Remove(id);
                        }
                    }
                }
            }

            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying hardcoded fallback melee weapons");
            foreach (int id in FALLBACK_MELEE_IDS)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(id);
                    if (IsUsableWeapon(item, requireGun: false))
                    {
                        BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned fallback melee weapon ID: {id}");
                        if (!_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Add(id);
                        return Accept(item);
                    }
                    if (item != null)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Fallback melee ID {id} is not usable (shell={item.name}), discarded");
                        Object.Destroy(item.gameObject);
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate fallback melee ID {id}: {ex.Message}");
                }
            }

            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying tag search for melee weapons");
            Tag? meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Melee");
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
                if (ids != null && ids.Length > 0)
                {
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Found {ids.Length} potential melee weapons via tag search");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Melee tag search failed: {ex.Message}");
            }
            
            if (ids == null || ids.Length == 0)
            {
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
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying {ids.Length} IDs from search");
                int attempts = Mathf.Min(30, ids.Length * 3);
                
                for(int i = 0; i < attempts; i++)
                {
                    int id = ids[Random.Range(0, ids.Length)];
                    
                    if (_cachedGunIds.Contains(id)) continue;
                    
                    if (_cachedMeleeIds.Contains(id))
                    {
                        try
                        {
                            var cachedItem = await ItemAssetsCollection.InstantiateAsync(id);
                            if (IsUsableWeapon(cachedItem, requireGun: false)) return Accept(cachedItem);
                            if (cachedItem != null) Object.Destroy(cachedItem.gameObject);
                            _cachedMeleeIds.Remove(id);
                        }
                        catch {}
                        continue;
                    }

                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (IsUsableWeapon(item, requireGun: false))
                            {
                                if (!_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Add(id);
                                return Accept(item);
                            }
                            Object.Destroy(item.gameObject);
                        }
                    }
                    catch {}
                }
            }
            
            BloodMoon.Utils.Logger.Error("[EnhancedWeaponManager] Failed to spawn any melee weapon");
            return null;
        }

        public async UniTask<Item?> SpawnRandomGun()
        {
            await EnsureInitialized();

            if (_cachedGunIds.Count > 0)
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying to spawn gun from cache ({_cachedGunIds.Count} IDs available)");
                
                int attempts = Mathf.Min(15, _cachedGunIds.Count * 2);
                for (int i = 0; i < attempts; i++)
                {
                    if (_cachedGunIds.Count == 0) break;
                    
                    int id = _cachedGunIds[Random.Range(0, _cachedGunIds.Count)];
                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (IsUsableWeapon(item, requireGun: true))
                        {
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned gun ID: {id}");
                            return Accept(item);
                        }
                        else if (item != null)
                        {
                            // 空壳/非枪物品：销毁并降级到近战候选表，绝不交回调用方
                            BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] ID {id} is not a usable gun (shell={item.name}), discarded");
                            Object.Destroy(item.gameObject);
                            if (_cachedGunIds.Contains(id)) _cachedGunIds.Remove(id);
                            if (!_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Add(id);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate gun ID {id}: {ex.Message}");
                        if (_cachedGunIds.Contains(id))
                        {
                            _cachedGunIds.Remove(id);
                        }
                    }
                }
            }

            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying hardcoded fallback guns");
            foreach (int id in FALLBACK_GUN_IDS)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(id);
                    if (IsUsableWeapon(item, requireGun: true))
                    {
                        BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned fallback gun ID: {id}");
                        if (!_cachedGunIds.Contains(id)) _cachedGunIds.Add(id);
                        return Accept(item);
                    }
                    // 修复 P0-8：硬编码表里的失效 ID 会返回裸壳，必须销毁并继续尝试下一个
                    if (item != null)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Fallback gun ID {id} is not usable (shell={item.name}), discarded");
                        Object.Destroy(item.gameObject);
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate fallback gun ID {id}: {ex.Message}");
                }
            }

            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying tag search for guns");
            // 修复 P1-16：GameplayDataSettings.Tags.Gun 可能为 null（资源缺失），
            // 原实现直接放进 requireTags 数组，一旦为 null 会让搜索结果退化成"全部物品"
            Tag? gunTag = GameplayDataSettings.Tags.Gun;
            if (gunTag == null)
            {
                BloodMoon.Utils.Logger.Warning("[EnhancedWeaponManager] GameplayDataSettings.Tags.Gun is null, search without tag filter");
            }
            var filter = new ItemFilter
            {
                minQuality = 1,
                maxQuality = 6,
                requireTags = gunTag != null ? new Tag[] { gunTag } : null
            };
            
            int[]? ids = null;
            try
            {
                ids = ItemAssetsCollection.Search(filter);
                if (ids != null && ids.Length > 0)
                {
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Found {ids.Length} potential guns via tag search");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Gun tag search failed: {ex.Message}");
            }
            
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
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying {ids.Length} IDs from search");
                int attempts = Mathf.Min(40, ids.Length * 3);
                
                for(int i = 0; i < attempts; i++)
                {
                    int id = ids[Random.Range(0, ids.Length)];
                    
                    if (_cachedMeleeIds.Contains(id)) continue;
                    
                    if (_cachedGunIds.Contains(id))
                    {
                        try
                        {
                            var cachedItem = await ItemAssetsCollection.InstantiateAsync(id);
                            if (IsUsableWeapon(cachedItem, requireGun: true)) return Accept(cachedItem);
                            if (cachedItem != null) Object.Destroy(cachedItem.gameObject);
                            _cachedGunIds.Remove(id);
                        }
                        catch {}
                        continue;
                    }

                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (IsUsableWeapon(item, requireGun: true))
                            {
                                if (!_cachedGunIds.Contains(id)) _cachedGunIds.Add(id);
                                return Accept(item);
                            }
                            Object.Destroy(item.gameObject);
                        }
                    }
                    catch {}
                }
            }
            
            BloodMoon.Utils.Logger.Error("[EnhancedWeaponManager] Failed to spawn any gun");
            return null;
        }

        public async UniTask<bool> EnsureAmmo(CharacterMainControl character, Item gun)
        {
            if (gun == null) return false;
            if (character == null || character.CharacterItem == null) return false;

            // 修复 P1-8：补弹幂等。BossManager.AddAmmoForGun 与 ComprehensiveWeaponSystem 都会调用，
            // 原先每次调用固定塞 3 组（每组最多 60 发），而角色背包容量只有 64 格 → 极易挤爆背包，
            // 多余的弹药 AddAndMerge 失败后还会留在场景根。这里 60s 内同一角色只补一次。
            int ammoKey = character.GetInstanceID();
            if (_lastAmmoGrant.TryGetValue(ammoKey, out float lastGrant) &&
                Time.realtimeSinceStartup - lastGrant < 60f)
            {
                return true;
            }
            _lastAmmoGrant[ammoKey] = Time.realtimeSinceStartup;

            string gunName = gun.name.ToLower();
            
            if (gunName.Contains("desert") || gunName.Contains("沙漠之鹰"))
            {
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Desert Eagle detected, may have ammo issues");
                return await TryAlternativeAmmoGeneration(character, gun, "DesertEagle");
            }
            
            if (gunName.Contains("rpg") || gunName.Contains("rocket"))
            {
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] RPG/Rocket detected, may have ammo issues");
                return await TryAlternativeAmmoGeneration(character, gun, "RPG");
            }

            try
            {
                var bullet = await ItemUtilities.GenerateBullet(gun);
                if (bullet != null)
                {
                    int desiredCount = Mathf.Min(bullet.MaxStackCount, 60);
                    bullet.StackCount = desiredCount;
                    if (!character.CharacterItem.Inventory.AddAndMerge(bullet))
                    {
                        // 背包满：销毁而不是丢在场景根（ItemUtilities.cs:149-153 只在成功时接管物品）
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Inventory full, ammo discarded for {gun.name}");
                        Object.Destroy(bullet.gameObject);
                        return false;
                    }

                    // 修复 P1-8：最多再补 1 组（原先固定再补 2 组）
                    for(int i=0; i<1; i++)
                    {
                        var extra = await ItemUtilities.GenerateBullet(gun);
                        if (extra != null)
                        {
                            extra.StackCount = Mathf.Min(extra.MaxStackCount, 60);
                            if (!character.CharacterItem.Inventory.AddAndMerge(extra))
                            {
                                Object.Destroy(extra.gameObject);
                                break;
                            }
                        }
                    }
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully ensured ammo for {gun.name}");
                    return true;
                }
                else
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] GenerateBullet returned null for {gun.name}");
                    
                    string weaponName = gun.name;
                    if (!_ammoGenerationFailures.ContainsKey(weaponName))
                        _ammoGenerationFailures[weaponName] = 0;
                    _ammoGenerationFailures[weaponName]++;
                    
                    if (_ammoGenerationFailures[weaponName] % 5 == 0)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Ammo generation failed {_ammoGenerationFailures[weaponName]} times for {weaponName}");
                    }
                    
                    return await TryAlternativeAmmoGeneration(character, gun, "Generic");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Error ensuring ammo for {gun.name}: {ex.Message}");
                return await TryAlternativeAmmoGeneration(character, gun, "Fallback");
            }
        }
        
        private async UniTask<bool> TryAlternativeAmmoGeneration(CharacterMainControl character, Item gun, string weaponType)
        {
            try
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying alternative ammo generation for {gun.name} (type: {weaponType})");
                
                Dictionary<string, int[]> ammoIdsByWeaponType = new Dictionary<string, int[]>
                {
                    { "DesertEagle", new int[] { 100, 50, 51, 52, 53, 54 } },
                    { "RPG", new int[] { 326 } },
                    { "Generic", new int[] { 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65 } }
                };
                
                if (ammoIdsByWeaponType.TryGetValue(weaponType, out var ammoIds))
                {
                    foreach (int ammoId in ammoIds)
                    {
                        try
                        {
                            var ammo = await ItemAssetsCollection.InstantiateAsync(ammoId);
                            if (ammo != null)
                            {
                                ammo.StackCount = Mathf.Min(ammo.MaxStackCount, 60);
                                character.CharacterItem.Inventory.AddAndMerge(ammo);
                                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully added ammo ID {ammoId} for {gun.name}");
                                return true;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            BloodMoon.Utils.Logger.Debug($"[EnhancedWeaponManager] Failed to instantiate ammo ID {ammoId}: {ex.Message}");
                        }
                    }
                }
                
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] All ammo generation methods failed for {gun.name}");
                return false;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Alternative ammo generation failed for {gun.name}: {ex.Message}");
                return false;
            }
        }
        
        // P2 清理（2026-09-10）：删除了 public FindMeleeWeapon(CharacterMainControl)。
        // 全仓无调用点 —— 随从近战武器的发放实际走 BossManager 的武器池
        // （WeaponPoolFilter + BossManager 里的近战分配分支），这里是一份重复实现。
    }
}