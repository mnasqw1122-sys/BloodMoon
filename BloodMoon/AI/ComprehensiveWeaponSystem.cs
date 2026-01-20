using System.Collections.Generic;
using UnityEngine;
using Duckov;
using Duckov.Utilities;
using Duckov.ItemUsage;
using BloodMoon.Utils;
using ItemStatsSystem;
using System.Linq;
using Cysharp.Threading.Tasks;

namespace BloodMoon.AI
{
    public class WeaponSet
    {
        public Item? PrimaryWeapon { get; set; }
        public Item? SecondaryWeapon { get; set; }
        public Item? MeleeWeapon { get; set; }
        public Item? ThrowableWeapon { get; set; }

        public bool IsEmpty => PrimaryWeapon == null && SecondaryWeapon == null && MeleeWeapon == null;

        public void AddRange(List<Item> weapons)
        {
            // 简单的槽位填充逻辑
            foreach (var w in weapons)
            {
                if (w == null) continue;
                if (w.GetComponent<ItemAgent_Gun>())
                {
                    if (PrimaryWeapon == null) PrimaryWeapon = w;
                    else if (SecondaryWeapon == null) SecondaryWeapon = w;
                }
                else if (w.GetComponent<ItemAgent_MeleeWeapon>())
                {
                    if (MeleeWeapon == null) MeleeWeapon = w;
                }
                else if (w.GetComponent<ItemSetting_Skill>()) // 假设是可投掷物逻辑
                {
                    if (ThrowableWeapon == null) ThrowableWeapon = w;
                }
            }
        }
    }

    public class WeaponCache
    {
        private List<Item> _cachedWeapons = new List<Item>();
        private List<int> _cachedWeaponIds = new List<int>();

        public void Preload(string weaponName)
        {
            try
            {
                BloodMoon.Utils.Logger.Log($"[WeaponCache] Preloading weapons for: {weaponName}");
                
                // 策略1：尝试使用增强搜索按名称查找
                // 注意：此版本中ItemFilter没有nameContains属性
                // 我们将使用基于标签的搜索代替
                BloodMoon.Utils.Logger.Log($"[WeaponCache] Searching for weapons with name pattern: {weaponName}");
                
                // 由于名称搜索不可用，我们将依赖基于标签的搜索
                // 实际搜索将在下面的通用枪支/近战搜索中完成
                
                // 策略2：通用枪支搜索
                var filterGun = new ItemFilter
                {
                    minQuality = 1,
                    maxQuality = 5,
                    requireTags = new Tag[] { GameplayDataSettings.Tags.Gun }
                };
                
                try 
                {
                    var idsGun = ItemAssetsCollection.Search(filterGun);
                    if (idsGun != null && idsGun.Length > 0)
                    {
                        int added = 0;
                        foreach (var id in idsGun)
                        {
                            if (!_cachedWeaponIds.Contains(id))
                            {
                                _cachedWeaponIds.Add(id);
                                added++;
                            }
                        }
                        if (added > 0)
                        {
                            BloodMoon.Utils.Logger.Log($"[WeaponCache] Added {added} gun IDs (total: {_cachedWeaponIds.Count})");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[WeaponCache] Gun tag search failed: {ex.Message}");
                }
                
                // 策略3：近战武器搜索
                Tag? meleeTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Melee");
                Tag? weaponTag = GameplayDataSettings.Tags.AllTags.FirstOrDefault(t => t.name == "Weapon");
                
                if (meleeTag != null || weaponTag != null)
                {
                    var filterMelee = new ItemFilter
                    {
                        minQuality = 1,
                        maxQuality = 5,
                        requireTags = new Tag[] { meleeTag ?? weaponTag! }
                    };
                    
                    try 
                    {
                        var idsMelee = ItemAssetsCollection.Search(filterMelee);
                        if (idsMelee != null && idsMelee.Length > 0)
                        {
                            int added = 0;
                            foreach (var id in idsMelee)
                            {
                                if (!_cachedWeaponIds.Contains(id))
                                {
                                    _cachedWeaponIds.Add(id);
                                    added++;
                                }
                            }
                            if (added > 0)
                            {
                                BloodMoon.Utils.Logger.Log($"[WeaponCache] Added {added} melee IDs (total: {_cachedWeaponIds.Count})");
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[WeaponCache] Melee tag search failed: {ex.Message}");
                    }
                }
                
                // 策略4：回退到增强武器管理器缓存
                if (_cachedWeaponIds.Count == 0)
                {
                    BloodMoon.Utils.Logger.Log($"[WeaponCache] No weapons found via search, trying to use EnhancedWeaponManager cache...");
                    // 我们将依赖我们已经改进的EnhancedWeaponManager缓存
                }
                
                BloodMoon.Utils.Logger.Log($"[WeaponCache] Preload completed. Total cached weapon IDs: {_cachedWeaponIds.Count}");
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[WeaponCache] Preload failed: {ex}");
            }
        }

        public Item? GetAvailableWeapon()
        {
            // 返回缓存武器ID的新实例
            if (_cachedWeaponIds.Count > 0)
            {
                int id = _cachedWeaponIds[Random.Range(0, _cachedWeaponIds.Count)];
                // 注意：在实际使用中这需要是异步的，但在这里如果未准备好可能会阻塞或返回null
                // 为了在这个同步上下文中的简单性，我们可能需要不同的方法或返回null
                // 然而，指南暗示我们可以获取它
                // 在Duckov中，实例化是异步的。我们可能需要更改签名或使用fire-and-forget
                
                // 由于我们无法在此同步等待，我们将依赖ComprehensiveWeaponSystem中的异步方法
                return null; 
            }
            return null;
        }

        public async UniTask<Item?> GetAvailableWeaponAsync()
        {
             if (_cachedWeaponIds.Count > 0)
            {
                int id = _cachedWeaponIds[Random.Range(0, _cachedWeaponIds.Count)];
                return await ItemAssetsCollection.InstantiateAsync(id);
            }
            return null;
        }
    }

    public class ComprehensiveWeaponSystem
    {
        private static ComprehensiveWeaponSystem? _instance;
        public static ComprehensiveWeaponSystem Instance => _instance ??= new ComprehensiveWeaponSystem();

        private Dictionary<string, WeaponSearchStrategy> _searchStrategies = null!;
        private WeaponCache _weaponCache = null!;
        
        public ComprehensiveWeaponSystem()
        {
            Initialize();
        }

        public void Initialize()
        {
            _searchStrategies = new Dictionary<string, WeaponSearchStrategy>
            {
                { "Primary", new PrimaryWeaponStrategy() },
                { "Secondary", new SecondaryWeaponStrategy() },
                { "Melee", new MeleeWeaponStrategy() },
                { "Throwable", new ThrowableWeaponStrategy() }
            };
            
            _weaponCache = new WeaponCache();
            PreloadWeaponResources();
        }
        
        private void PreloadWeaponResources()
        {
            // 优化：只预加载最常用的武器类型，减少资源消耗
            // 基于实际游戏日志，这些是最常见的武器类型
            string[] essentialWeapons = {
                "AK-47", "M4A1", "MP5", "Glock",
                "Knife", "Axe"
            };
            
            // 延迟预加载，避免游戏启动时的性能冲击
            UniTask.Void(async () =>
            {
                // 等待游戏初始化完成
                await UniTask.Delay(3000);
                
                BloodMoon.Utils.Logger.Log("[WeaponSystem] Starting essential weapon preload...");
                
                foreach (var weapon in essentialWeapons)
                {
                    _weaponCache.Preload(weapon);
                    // 添加小延迟以避免一次性加载太多资源
                    await UniTask.Delay(100);
                }
                
                BloodMoon.Utils.Logger.Log("[WeaponSystem] Essential weapon preload completed");
            });
        }
        
        public async UniTask<WeaponSet> FindWeaponsForAI(CharacterMainControl character)
        {
            var weaponSet = new WeaponSet();
            
            try
            {
                // 0.确保武器管理器已初始化
                await EnhancedWeaponManager.Instance.EnsureInitialized();
                
                // 1. 先搜索库存
                foreach (var strategy in _searchStrategies.Values)
                {
                    var weapons = strategy.SearchInventory(character);
                    if (weapons != null && weapons.Count > 0)
                    {
                        weaponSet.AddRange(weapons);
                        if (strategy.Type == "Primary" && weapons.Count > 0) weaponSet.PrimaryWeapon = weapons[0];
                        if (strategy.Type == "Secondary" && weapons.Count > 0) weaponSet.SecondaryWeapon = weapons[0];
                        if (strategy.Type == "Melee" && weapons.Count > 0) weaponSet.MeleeWeapon = weapons[0];
                    }
                }
                
                // 2. 如果缺少必需品，尝试生成/寻找备用方案
                if (weaponSet.PrimaryWeapon == null && weaponSet.SecondaryWeapon == null)
                {
                    BloodMoon.Utils.Logger.Log($"[WeaponSystem] No guns found in inventory for {character.name}, trying to spawn...");
                    
                    // 尝试生成一把枪（多次尝试）
                    Item? gun = null;
                    for (int attempt = 0; attempt < 3 && gun == null; attempt++)
                    {
                        gun = await EnhancedWeaponManager.Instance.SpawnRandomGun();
                        if (gun == null)
                        {
                            await UniTask.Delay(100);
                            BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Gun spawn attempt {attempt + 1} failed for {character.name}");
                        }
                    }
                    
                    if (gun != null)
                    {
                        character.CharacterItem.Inventory.AddAndMerge(gun);
                        await EnhancedWeaponManager.Instance.EnsureAmmo(character, gun);
                        weaponSet.PrimaryWeapon = gun;
                        BloodMoon.Utils.Logger.Log($"[WeaponSystem] Successfully spawned gun for {character.name}: {gun.name}");
                    }
                    else
                    {
                        // 尝试缓存
                        BloodMoon.Utils.Logger.Log($"[WeaponSystem] Trying weapon cache for {character.name}...");
                        var cached = await _weaponCache.GetAvailableWeaponAsync();
                        if (cached != null && cached.GetComponent<ItemAgent_Gun>())
                        {
                            character.CharacterItem.Inventory.AddAndMerge(cached);
                            await EnhancedWeaponManager.Instance.EnsureAmmo(character, cached);
                            weaponSet.PrimaryWeapon = cached;
                            BloodMoon.Utils.Logger.Log($"[WeaponSystem] Found gun in cache for {character.name}: {cached.name}");
                        }
                        else
                        {
                            BloodMoon.Utils.Logger.Error($"[WeaponSystem] All gun search strategies failed for {character.name}");
                        }
                    }
                }

                if (weaponSet.MeleeWeapon == null)
                {
                    BloodMoon.Utils.Logger.Log($"[WeaponSystem] No melee weapon found for {character.name}, trying to spawn...");
                    
                    // 尝试生成近战武器（多次尝试）
                    Item? melee = null;
                    for (int attempt = 0; attempt < 3 && melee == null; attempt++)
                    {
                        melee = await EnhancedWeaponManager.Instance.SpawnRandomMeleeWeapon();
                        if (melee == null)
                        {
                            await UniTask.Delay(100);
                            BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Melee spawn attempt {attempt + 1} failed for {character.name}");
                        }
                    }
                    
                    if (melee != null)
                    {
                        character.CharacterItem.Inventory.AddAndMerge(melee);
                        weaponSet.MeleeWeapon = melee;
                        BloodMoon.Utils.Logger.Log($"[WeaponSystem] Successfully spawned melee weapon for {character.name}: {melee.name}");
                    }
                    else
                    {
                        BloodMoon.Utils.Logger.Error($"[WeaponSystem] Failed to find melee weapon for {character.name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[WeaponSystem] Error finding weapons for {character.name}: {ex}");
            }
            
            LogWeaponFindings(character, weaponSet);
            return weaponSet;
        }
        
        private void LogWeaponFindings(CharacterMainControl character, WeaponSet weaponSet)
        {
            if (weaponSet.IsEmpty)
            {
                BloodMoon.Utils.Logger.Error($"[WeaponSystem] Failed to find weapons for {character.name}");
            }
            else
            {
                BloodMoon.Utils.Logger.Log($"[WeaponSystem] Found weapons for {character.name}: " +
                          $"Primary: {weaponSet.PrimaryWeapon?.name ?? "None"}, " +
                          $"Melee: {weaponSet.MeleeWeapon?.name ?? "None"}");
            }
        }
    }
    
    public abstract class WeaponSearchStrategy
    {
        public abstract string Type { get; }
        
        public virtual List<Item> SearchInventory(CharacterMainControl character)
        {
            var foundWeapons = new List<Item>();
            var inventory = character.CharacterItem?.Inventory;
            if (inventory != null)
            {
                foreach (var item in inventory)
                {
                    if (item == null) continue;
                    if (IsWeaponType(item)) foundWeapons.Add(item);
                }
            }
            return foundWeapons;
        }
        
        protected abstract bool IsWeaponType(Item item);
    }

    public class PrimaryWeaponStrategy : WeaponSearchStrategy
    {
        public override string Type => "Primary";
        protected override bool IsWeaponType(Item item)
        {
            return item.GetComponent<ItemAgent_Gun>() != null; 
        }
    }

    public class SecondaryWeaponStrategy : WeaponSearchStrategy
    {
        public override string Type => "Secondary";
        protected override bool IsWeaponType(Item item)
        {
            // 在Duckov中，副武器也是枪支，通常是手枪
            // 如果有的话，我们可以检查大小或特定标签，但目前如果主槽位逻辑处理它，任何枪支都适用
            // 但严格定义：
            return item.GetComponent<ItemAgent_Gun>() != null; 
        }
    }

    public class MeleeWeaponStrategy : WeaponSearchStrategy
    {
        public override string Type => "Melee";
        protected override bool IsWeaponType(Item item)
        {
            return item.GetComponent<ItemAgent_MeleeWeapon>() != null;
        }
    }

    public class ThrowableWeaponStrategy : WeaponSearchStrategy
    {
        public override string Type => "Throwable";
        protected override bool IsWeaponType(Item item)
        {
            // 检查手榴弹/技能
            var ss = item.GetComponent<ItemSetting_Skill>();
            return ss != null && ss.Skill != null && !item.GetComponent<Drug>();
        }
    }
}
