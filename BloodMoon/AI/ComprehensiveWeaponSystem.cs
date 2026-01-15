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
            // Simple logic to populate slots
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
                else if (w.GetComponent<ItemSetting_Skill>()) // Assuming throwable logic
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
                
                // Strategy 1: Try to find by name using enhanced search
                // Note: ItemFilter doesn't have nameContains property in this version
                // We'll use tag-based search instead
                BloodMoon.Utils.Logger.Log($"[WeaponCache] Searching for weapons with name pattern: {weaponName}");
                
                // We'll rely on tag-based search since name search is not available
                // The actual search will be done in the generic gun/melee searches below
                
                // Strategy 2: Generic gun search
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
                
                // Strategy 3: Melee weapon search
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
                
                // Strategy 4: Fallback to enhanced weapon manager cache
                if (_cachedWeaponIds.Count == 0)
                {
                    BloodMoon.Utils.Logger.Log($"[WeaponCache] No weapons found via search, trying to use EnhancedWeaponManager cache...");
                    // We'll rely on EnhancedWeaponManager's cache which we've already improved
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
            // Return a new instance of a cached weapon ID
            if (_cachedWeaponIds.Count > 0)
            {
                int id = _cachedWeaponIds[Random.Range(0, _cachedWeaponIds.Count)];
                // Note: This needs to be async in real usage, but here we might block or return null if not ready.
                // For simplicity in this synchronous context, we might need a different approach or return null.
                // However, the guide implies we can get it. 
                // In Duckov, instantiation is async. We might need to change the signature or fire-and-forget.
                
                // Since we can't easily wait here synchronously, we will rely on the Async methods in ComprehensiveWeaponSystem
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
            // Preload known weapon resources
            string[] knownWeapons = {
                "AK-47", "M4A1", "MP5", "UZI", "Glock", "DesertEagle",
                "Knife", "Axe", "Bat", "Crowbar", "Machete",
                "Grenade", "Molotov", "SmokeGrenade"
            };
            
            foreach (var weapon in knownWeapons)
            {
                _weaponCache.Preload(weapon);
            }
        }
        
        public async UniTask<WeaponSet> FindWeaponsForAI(CharacterMainControl character)
        {
            var weaponSet = new WeaponSet();
            
            try
            {
                // 0. Ensure weapon manager is initialized
                await EnhancedWeaponManager.Instance.EnsureInitialized();
                
                // 1. Search Inventory First
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
                
                // 2. If missing essentials, try to spawn/find fallback
                if (weaponSet.PrimaryWeapon == null && weaponSet.SecondaryWeapon == null)
                {
                    BloodMoon.Utils.Logger.Log($"[WeaponSystem] No guns found in inventory for {character.name}, trying to spawn...");
                    
                    // Try to spawn a gun (multiple attempts)
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
                        // Try cache
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
                    
                    // Try to spawn a melee weapon (multiple attempts)
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
            // In Duckov, secondary is also a gun, usually pistol. 
            // We can check size or specific tags if available, but for now any gun fits if primary slot logic handles it.
            // But strict definition:
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
            // Check for grenades/skills
            var ss = item.GetComponent<ItemSetting_Skill>();
            return ss != null && ss.Skill != null && !item.GetComponent<Drug>();
        }
    }
}
