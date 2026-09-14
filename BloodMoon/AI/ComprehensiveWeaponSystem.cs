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
            // 简单的槽位填充逻辑（同一件武器不会重复填两个槽）
            foreach (var w in weapons)
            {
                if (w == null) continue;
                if (w == PrimaryWeapon || w == SecondaryWeapon || w == MeleeWeapon || w == ThrowableWeapon) continue;
                // 用结构判定（ItemSetting_*/ItemAgent_*）而不是 GetComponent<ItemAgent_Gun>()：
                // 刚实例化的物品上还没有 ItemAgent_*（它由 ItemAgent_Gun.BuildAgent() 在运行时挂），
                // 原来那样写会把背包里的枪识别不出来。
                if (EnhancedWeaponManager.LooksLikeGun(w))
                {
                    if (PrimaryWeapon == null) PrimaryWeapon = w;
                    else if (SecondaryWeapon == null) SecondaryWeapon = w;
                }
                else if (EnhancedWeaponManager.LooksLikeMelee(w))
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
        private List<int> _cachedWeaponIds = new List<int>();

        /// <summary>本轮是否已经回填过武器池（避免每次 Preload 都全表搜索）</summary>
        private bool _poolPulled;

        /// <summary>
        /// 预加载。
        ///
        /// 2026-09-10 实机日志观察项：`PreloadWeaponResources()` 会对 6 个武器名各调一次本方法，
        /// 而原实现每次都要跑两遍 `ItemAssetsCollection.Search`（全表搜索）→ 启动时 12 次全表扫描，
        /// 日志里能看到重复的 `Added 39 gun IDs (total: 39)` / `Added 17 melee IDs (total: 56)`。
        ///
        /// 现在只保留"按名字找 ID"（廉价），武器池统一从 EnhancedWeaponManager 已经构建好的
        /// （且已过武器池过滤的）缓存里取一次。
        /// </summary>
        public void Preload(string weaponName)
        {
            try
            {
                BloodMoon.Utils.Logger.Log($"[WeaponCache] Preloading weapons for: {weaponName}");

                // 策略1：按名字精确搜索（TryGetIDByName 支持忽略大小写）
                try
                {
                    int nameId = ItemAssetsCollection.TryGetIDByName(weaponName, true);
                    if (nameId >= 0 && !_cachedWeaponIds.Contains(nameId))
                    {
                        _cachedWeaponIds.Add(nameId);
                        BloodMoon.Utils.Logger.Log($"[WeaponCache] Found '{weaponName}' by name (id={nameId})");
                    }
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Warning($"[WeaponCache] Name search failed for '{weaponName}': {ex.Message}");
                }

                // 策略2：复用 EnhancedWeaponManager 已经建好的武器池（只做一次）
                if (!_poolPulled)
                {
                    _poolPulled = true;
                    int added = 0;
                    var gunIds = EnhancedWeaponManager.Instance.CachedGunIds;
                    for (int i = 0; i < gunIds.Count; i++)
                    {
                        if (!_cachedWeaponIds.Contains(gunIds[i]))
                        {
                            _cachedWeaponIds.Add(gunIds[i]);
                            added++;
                        }
                    }
                    var meleeIds = EnhancedWeaponManager.Instance.CachedMeleeIds;
                    for (int i = 0; i < meleeIds.Count; i++)
                    {
                        if (!_cachedWeaponIds.Contains(meleeIds[i]))
                        {
                            _cachedWeaponIds.Add(meleeIds[i]);
                            added++;
                        }
                    }
                    if (added > 0)
                    {
                        BloodMoon.Utils.Logger.Log($"[WeaponCache] Pulled {added} IDs from weapon manager cache (total: {_cachedWeaponIds.Count})");
                    }
                }

                BloodMoon.Utils.Logger.Log($"[WeaponCache] Preload completed. Total cached weapon IDs: {_cachedWeaponIds.Count}");
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[WeaponCache] Preload failed: {ex}");
            }
        }

        public async UniTask<Item?> GetAvailableWeaponAsync()
        {
            // 注意：调用方通常是在 EnhancedWeaponManager.SpawnRandomGun() 返回 null 之后才走到这里，
            // 所以**不要**再回调 SpawnRandomGun（纯属重复劳动）。这里只用本地 ID 表（已从
            // EnhancedWeaponManager 的过滤后武器池回填），并逐一把空壳/被过滤的物品剔除。
            if (_cachedWeaponIds.Count > 0)
            {
                int attempts = Mathf.Min(8, _cachedWeaponIds.Count);
                for (int i = 0; i < attempts; i++)
                {
                    int idx = Random.Range(0, _cachedWeaponIds.Count);
                    int id = _cachedWeaponIds[idx];
                    try
                    {
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item == null) continue;
                        if (EnhancedWeaponManager.IsUsableWeapon(item, requireGun: true)) return item;
                        Object.Destroy(item.gameObject);
                        _cachedWeaponIds.RemoveAt(idx);   // 无效 ID 直接出池，避免反复实例化
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Warning($"[WeaponCache] Instantiate failed for ID {id}: {ex.Message}");
                        _cachedWeaponIds.RemoveAt(idx);
                    }
                }
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

            if (character == null || character.CharacterItem == null) return weaponSet;

            // 修复 P1-2：与 BossManager.EnsureMinionHasWeapons 互斥，避免同一角色被配两套武器/弹药
            if (!EnhancedWeaponManager.TryBeginLoadout(character, "ComprehensiveWeaponSystem"))
            {
                return weaponSet;
            }

            try
            {
                // 0.确保武器管理器已初始化
                await EnhancedWeaponManager.Instance.EnsureInitialized();

                if (character == null || character.CharacterItem == null) return weaponSet;

                // 1. 先搜索库存。
                //    修复 P1-3：不再用 strategy.Type 覆盖 Primary/Secondary ——
                //    PrimaryWeaponStrategy 与 SecondaryWeaponStrategy 的判定条件逐字相同，
                //    两轮 SearchInventory 会返回同一把枪，覆盖后 Primary 与 Secondary 会指向同一对象。
                //    WeaponSet.AddRange 本身已按"第一个→Primary，第二个→Secondary"分配（本文件 :28-33）。
                foreach (var strategy in _searchStrategies.Values)
                {
                    var weapons = strategy.SearchInventory(character);
                    if (weapons != null && weapons.Count > 0)
                    {
                        weaponSet.AddRange(weapons);
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

                    // 修复 P0-8：SpawnRandomGun 已做"真实武器"校验，这里再兜一道，
                    // 避免把空壳（FallbackItem_*）塞进背包并当成武器上报
                    if (gun != null && !EnhancedWeaponManager.IsUsableWeapon(gun, requireGun: true))
                    {
                        BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Spawner returned a non-gun item ({gun.name}), discarded");
                        UnityEngine.Object.Destroy(gun.gameObject);
                        gun = null;
                    }

                    if (gun != null)
                    {
                        // 修复 P0-9：AddAndMerge 在背包满时返回 false 且物品留在场景根不销毁
                        // （ItemUtilities.cs:149-153），必须检查返回值。
                        if (!character.CharacterItem.Inventory.AddAndMerge(gun))
                        {
                            BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Inventory full, gun discarded for {character.name}");
                            UnityEngine.Object.Destroy(gun.gameObject);
                            gun = null;
                        }
                        else
                        {
                            await EnhancedWeaponManager.Instance.EnsureAmmo(character, gun);
                            // 插进主武器槽：旧版只塞背包不插槽 → 敌人有枪不掏
                            var pSlot = character.PrimWeaponSlot();
                            if (pSlot != null && pSlot.Content == null && pSlot.CanPlug(gun))
                            {
                                pSlot.Plug(gun, out var _);
                            }
                            weaponSet.PrimaryWeapon = gun;
                            BloodMoon.Utils.Logger.Log($"[WeaponSystem] Successfully spawned gun for {character.name}: {gun.name}");
                        }
                    }

                    if (gun == null)
                    {
                        // 尝试缓存
                        BloodMoon.Utils.Logger.Log($"[WeaponSystem] Trying weapon cache for {character.name}...");
                        var cached = await _weaponCache.GetAvailableWeaponAsync();
                        if (cached != null && EnhancedWeaponManager.IsUsableWeapon(cached, requireGun: true))
                        {
                            if (!character.CharacterItem.Inventory.AddAndMerge(cached))
                            {
                                BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Inventory full, cached gun discarded for {character.name}");
                                UnityEngine.Object.Destroy(cached.gameObject);
                            }
                            else
                            {
                                await EnhancedWeaponManager.Instance.EnsureAmmo(character, cached);
                                var pSlot2 = character.PrimWeaponSlot();
                                if (pSlot2 != null && pSlot2.Content == null && pSlot2.CanPlug(cached))
                                {
                                    pSlot2.Plug(cached, out var _);
                                }
                                weaponSet.PrimaryWeapon = cached;
                                BloodMoon.Utils.Logger.Log($"[WeaponSystem] Found gun in cache for {character.name}: {cached.name}");
                            }
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
                        // 修复 P0-9：检查 AddAndMerge 返回值（背包满时物品不会被收纳）
                        if (!character.CharacterItem.Inventory.AddAndMerge(melee))
                        {
                            BloodMoon.Utils.Logger.Warning($"[WeaponSystem] Inventory full, melee discarded for {character.name}");
                            UnityEngine.Object.Destroy(melee.gameObject);
                            melee = null;
                        }
                        else
                        {
                            // 插进近战槽（旧版同样只塞背包）
                            var mSlot = character.MeleeWeaponSlot();
                            if (mSlot != null && mSlot.Content == null && mSlot.CanPlug(melee))
                            {
                                mSlot.Plug(melee, out var _);
                            }
                            weaponSet.MeleeWeapon = melee;
                            BloodMoon.Utils.Logger.Log($"[WeaponSystem] Successfully spawned melee weapon for {character.name}: {melee.name}");
                        }
                    }

                    if (melee == null)
                    {
                        BloodMoon.Utils.Logger.Error($"[WeaponSystem] Failed to find melee weapon for {character.name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"[WeaponSystem] Error finding weapons for {character.name}: {ex}");
            }
            finally
            {
                EnhancedWeaponManager.EndLoadout(character);
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
