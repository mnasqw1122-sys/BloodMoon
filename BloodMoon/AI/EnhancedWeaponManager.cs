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

        // 硬编码回退武器ID（基于实际日志中的成功生成记录）
        // 从新日志中提取的实际成功生成的武器ID
        private static readonly int[] FALLBACK_MELEE_IDS = { 
            1172, 240, 1096, 784, 1208, 683, 735, 658, 655, 254, 682, 1286, 1287, 786, 1173,
            1174, 1248, 680, 258, 305, 238, 1074, 1095, 652, 653, 657, 659, 250, 252, 256,
            260, 327, 357, 681, 734, 737, 780, 782, 787, 788
        }; // 从实际日志中提取的成功生成的近战武器ID
        
        private static readonly int[] FALLBACK_GUN_IDS = { 
            788, 659, 238, 656, 258, 655, 250, 246, 252, 781, 786, 254, 782, 652, 781,
            653, 327, 657, 787, 357, 681, 734, 256, 260, 305, 680, 682, 737, 780
        }; // 从实际日志中提取的成功生成的枪支ID
        
        private List<int> _cachedMeleeIds = new List<int>();
        private List<int> _cachedGunIds = new List<int>();
        private bool _initialized = false;
        private bool _isInitializing = false;
        private UniTask _initializationTask;
        
        // 弹药生成失败统计
        private Dictionary<string, int> _ammoGenerationFailures = new Dictionary<string, int>();

        public async UniTask EnsureInitialized()
        {
            if (_initialized) return;
            if (_isInitializing) 
            {
                await _initializationTask;
                return;
            }
            
            _isInitializing = true;
            _initializationTask = InitializeInternal();
            await _initializationTask;
        }

        private async UniTask InitializeInternal()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Starting initialization...");
                
                // 策略1：尝试从ItemAssetsCollection扫描
                await TryInitializeFromCollection();
                
                // 策略2：如果扫描失败，尝试使用标签搜索
                if (!_initialized)
                {
                    TryInitializeFromTags();
                }
                
                // 策略3：如果标签搜索失败，使用硬编码ID
                if (!_initialized)
                {
                    TryInitializeFromHardcoded();
                }
                
                if (_initialized)
                {
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Initialized successfully. Found {_cachedMeleeIds.Count} Melee Weapons and {_cachedGunIds.Count} Guns.");
                }
                else
                {
                    BloodMoon.Utils.Logger.Error("[EnhancedWeaponManager] All initialization strategies failed!");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Initialization Failed: {ex}");
                _isInitializing = false;
            }
        }

        private async UniTask TryInitializeFromCollection()
        {
            try
            {
                BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying to initialize from ItemAssetsCollection...");
                
                // 等待游戏资源加载完成
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
                    
                    // 检查近战武器组件
                    if (entry.prefab.GetComponent<ItemAgent_MeleeWeapon>() != null)
                    {
                        if (!_cachedMeleeIds.Contains(entry.typeID)) 
                        {
                            _cachedMeleeIds.Add(entry.typeID);
                            meleeFound++;
                        }
                    }
                    // 检查枪支组件
                    else if (entry.prefab.GetComponent<ItemAgent_Gun>() != null)
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
                
                // 搜索近战武器
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
                
                // 搜索枪支
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
                
                // 添加硬编码ID到缓存
                _cachedMeleeIds.AddRange(FALLBACK_MELEE_IDS);
                _cachedGunIds.AddRange(FALLBACK_GUN_IDS);
                
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Added {FALLBACK_MELEE_IDS.Length} hardcoded melee IDs and {FALLBACK_GUN_IDS.Length} hardcoded gun IDs");
                
                // 标记为已初始化（即使硬编码ID可能不正确，至少不会崩溃）
                _initialized = true;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Hardcoded initialization failed: {ex}");
            }
        }

        public void Initialize()
        {
            if (_initialized) return;
            
            // 同步初始化（向后兼容）
            try
            {
                var collection = ItemAssetsCollection.Instance;
                if (collection != null && collection.entries != null)
                {
                    foreach (var entry in collection.entries)
                    {
                        if (entry == null || entry.prefab == null) continue;
                        
                        if (entry.prefab.GetComponent<ItemAgent_MeleeWeapon>() != null)
                        {
                            if (!_cachedMeleeIds.Contains(entry.typeID)) _cachedMeleeIds.Add(entry.typeID);
                        }
                        else if (entry.prefab.GetComponent<ItemAgent_Gun>() != null)
                        {
                            if (!_cachedGunIds.Contains(entry.typeID)) _cachedGunIds.Add(entry.typeID);
                        }
                    }
                    _initialized = true;
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Initialized. Found {_cachedMeleeIds.Count} Melee Weapons and {_cachedGunIds.Count} Guns.");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Initialization Failed: {ex}");
            }
        }

        public async UniTask<Item?> SpawnRandomMeleeWeapon()
        {
            // 确保已初始化
            await EnsureInitialized();

            // 策略1：尝试从缓存获取
            if (_cachedMeleeIds.Count > 0)
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying to spawn melee from cache ({_cachedMeleeIds.Count} IDs available)");
                
                // 尝试从缓存中获取有效武器
                int attempts = Mathf.Min(15, _cachedMeleeIds.Count * 2);
                for (int i = 0; i < attempts; i++) 
                {
                    // 确保缓存不为空再随机访问
                    if (_cachedMeleeIds.Count == 0) break;
                    
                    int id = _cachedMeleeIds[Random.Range(0, _cachedMeleeIds.Count)];
                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null) 
                        {
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned melee weapon ID: {id}");
                            return item;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate melee ID {id}: {ex.Message}");
                        // 移除无效ID
                        if (_cachedMeleeIds.Contains(id))
                        {
                            _cachedMeleeIds.Remove(id);
                        }
                    }
                }
            }

            // 策略2：尝试硬编码回退
            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying hardcoded fallback melee weapons");
            foreach (int id in FALLBACK_MELEE_IDS)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(id);
                    if (item != null) 
                    {
                        BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned fallback melee weapon ID: {id}");
                        if (!_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Add(id);
                        return item;
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate fallback melee ID {id}: {ex.Message}");
                }
            }

            // 策略3：标签搜索（原逻辑）
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
            
            // 后备搜索
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
            
            // 绝望搜索：搜索所有物品
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
                    
                    // 跳过已知的枪支ID
                    if (_cachedGunIds.Contains(id)) continue;
                    
                    // 如果是已知的近战武器ID，直接使用
                    if (_cachedMeleeIds.Contains(id))
                    {
                        try
                        {
                            var cachedItem = await ItemAssetsCollection.InstantiateAsync(id);
                            if (cachedItem != null) return cachedItem;
                        }
                        catch {}
                        continue;
                    }

                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            // 验证是否为近战武器
                            if (item.GetComponent<ItemAgent_MeleeWeapon>() != null)
                            {
                                if (!_cachedMeleeIds.Contains(id)) _cachedMeleeIds.Add(id);
                                return item;
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
            // 确保已初始化
            await EnsureInitialized();

            // 策略1：尝试从缓存获取
            if (_cachedGunIds.Count > 0)
            {
                BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Trying to spawn gun from cache ({_cachedGunIds.Count} IDs available)");
                
                int attempts = Mathf.Min(15, _cachedGunIds.Count * 2);
                for (int i = 0; i < attempts; i++)
                {
                    // 确保缓存不为空再随机访问
                    if (_cachedGunIds.Count == 0) break;
                    
                    int id = _cachedGunIds[Random.Range(0, _cachedGunIds.Count)];
                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null) 
                        {
                            BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned gun ID: {id}");
                            return item;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate gun ID {id}: {ex.Message}");
                        // 移除无效ID
                        if (_cachedGunIds.Contains(id))
                        {
                            _cachedGunIds.Remove(id);
                        }
                    }
                }
            }

            // 策略2：尝试硬编码回退
            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying hardcoded fallback guns");
            foreach (int id in FALLBACK_GUN_IDS)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(id);
                    if (item != null) 
                    {
                        BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully spawned fallback gun ID: {id}");
                        if (!_cachedGunIds.Contains(id)) _cachedGunIds.Add(id);
                        return item;
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Failed to instantiate fallback gun ID {id}: {ex.Message}");
                }
            }

            // 策略3：标签搜索
            BloodMoon.Utils.Logger.Log("[EnhancedWeaponManager] Trying tag search for guns");
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
                if (ids != null && ids.Length > 0)
                {
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Found {ids.Length} potential guns via tag search");
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Gun tag search failed: {ex.Message}");
            }
            
            // 后备搜索：搜索所有物品
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
                    
                    // 跳过已知的近战武器ID
                    if (_cachedMeleeIds.Contains(id)) continue;
                    
                    // 如果是已知的枪支ID，直接使用
                    if (_cachedGunIds.Contains(id))
                    {
                        try
                        {
                            var cachedItem = await ItemAssetsCollection.InstantiateAsync(id);
                            if (cachedItem != null) return cachedItem;
                        }
                        catch {}
                        continue;
                    }

                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (item.GetComponent<ItemAgent_Gun>() != null)
                            {
                                if (!_cachedGunIds.Contains(id)) _cachedGunIds.Add(id);
                                return item;
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
             
             // 特殊武器处理
             string gunName = gun.name.ToLower();
             
             // 检查沙漠之鹰（从日志中看到子弹生成失败）
             if (gunName.Contains("desert") || gunName.Contains("沙漠之鹰"))
             {
                 BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] Desert Eagle detected, may have ammo issues");
                 // 尝试使用备用弹药生成方法
                 return await TryAlternativeAmmoGeneration(character, gun, "DesertEagle");
             }
             
             // 检查RPG/Rocket
             if (gunName.Contains("rpg") || gunName.Contains("rocket"))
             {
                 BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] RPG/Rocket detected, may have ammo issues");
                 // 尝试使用备用弹药生成方法
                 return await TryAlternativeAmmoGeneration(character, gun, "RPG");
             }

             try
             {
                var bullet = await ItemUtilities.GenerateBullet(gun);
                if (bullet != null)
                {
                    bullet.StackCount = bullet.MaxStackCount;
                    character.CharacterItem.Inventory.AddAndMerge(bullet);
                    
                    // 添加额外堆叠
                    for(int i=0; i<3; i++)
                    {
                        var extra = await ItemUtilities.GenerateBullet(gun);
                        if (extra != null)
                        {
                             extra.StackCount = extra.MaxStackCount;
                             character.CharacterItem.Inventory.AddAndMerge(extra);
                        }
                    }
                    BloodMoon.Utils.Logger.Log($"[EnhancedWeaponManager] Successfully ensured ammo for {gun.name}");
                    return true;
                }
                else
                {
                    BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] GenerateBullet returned null for {gun.name}");
                    
                    // 记录弹药生成失败统计
                    string weaponName = gun.name;
                    if (!_ammoGenerationFailures.ContainsKey(weaponName))
                        _ammoGenerationFailures[weaponName] = 0;
                    _ammoGenerationFailures[weaponName]++;
                    
                    // 定期输出统计信息
                    if (_ammoGenerationFailures[weaponName] % 5 == 0) // 每5次失败输出一次
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
                
                // 尝试直接搜索弹药ID
                // 基于实际日志，弹药ID 50已验证有效
                // 添加更多可能的弹药ID范围
                Dictionary<string, int[]> ammoIdsByWeaponType = new Dictionary<string, int[]>
                {
                    { "DesertEagle", new int[] { 100, 50, 51, 52, 53, 54 } }, // 从日志看ID 100有效，ID 50已验证有效
                    { "RPG", new int[] { 326 } }, // RPG弹药ID（需要实际验证）
                    { "Generic", new int[] { 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65 } } // 通用弹药ID范围
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
                                ammo.StackCount = ammo.MaxStackCount;
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
                
                // 如果所有方法都失败，至少记录警告
                BloodMoon.Utils.Logger.Warning($"[EnhancedWeaponManager] All ammo generation methods failed for {gun.name}");
                return false;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[EnhancedWeaponManager] Alternative ammo generation failed for {gun.name}: {ex.Message}");
                return false;
            }
        }
        
        public Item? FindMeleeWeapon(CharacterMainControl character)
        {
            var inventory = character.CharacterItem?.Inventory;
            if (inventory == null) return null;
            
            foreach (var item in inventory)
            {
                if (item == null) continue;
                if (item.GetComponent<ItemAgent_MeleeWeapon>() != null) return item;
            }

            string[] meleeTypes = { "Knife", "Axe", "Bat", "Crowbar", "Machete", "Sword", "Dagger", "Hammer" };
            foreach (var item in inventory)
            {
                if (item == null) continue;
                string itemName = item.name;
                foreach (var meleeType in meleeTypes)
                {
                    if (itemName.IndexOf(meleeType, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return item;
                    }
                }
            }
            return null;
        }
    }
}
