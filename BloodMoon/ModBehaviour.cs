using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Saves;
using Duckov;
using Duckov.Modding;
using Duckov.UI;
using Duckov.Utilities;
using Duckov.Weathers;
using ItemStatsSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using BloodMoon.Utils;
using ModLogger = BloodMoon.Utils.Logger;

namespace BloodMoon
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private BloodMoonEvent _event = null!;
        private BloodMoonUI _ui = null!;
        private RedOverlay _overlay = null!;
        private BossManager _bossManager = null!;
        private AIDataStore _dataStore = null!;
        private BloodMoon.AI.AdaptiveDifficulty _difficulty = null!;
        private BloodMoon.AI.SquadManager _squadManager = null!;

        /// <summary>
        /// 模组唤醒时调用，初始化所有系统
        /// </summary>
        private void Awake()
        {
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            ModLogger.Initialize(modDir);
            ModConfig.Initialize(modDir);

            ModLogger.Log("BloodMoon Mod Loaded");
            
            _event = new BloodMoonEvent();
            _dataStore = new AIDataStore();
            _overlay = new RedOverlay();
            _bossManager = new BossManager(_dataStore);
            _ui = new BloodMoonUI(_event);
            
            _difficulty = new BloodMoon.AI.AdaptiveDifficulty();
            _difficulty.Initialize();
            
            _squadManager = new BloodMoon.AI.SquadManager();
            _squadManager.Initialize();
            
            UniTask.Void(async ()=> {
                try
                {
                    await BloodMoon.AI.EnhancedWeaponManager.Instance.EnsureInitialized();
                    ModLogger.Log("[ModBehaviour] Weapon Manager initialized successfully");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"[ModBehaviour] Weapon Manager initialization failed: {ex}");
                }
            });

            SavesSystem.OnCollectSaveData += Save;

            // 玩家受击 → 自适应难度输入（旧版 ReportPlayerDamage 从未被调用，难度只会涨不会跌）
            Health.OnHurt += OnPlayerHurt;

            // 物品数据修复的**更早**时机：LevelManager.OnControllingCharacterChanged 在
            // CreateMainCharacterAsync 里（"Setting up pet / character items" 之前）就触发，
            // 而 OnLevelInitialized 要到关卡初始化末尾 —— 实机的 "Index was out of range"
            // 正是集中在 pet/character items 这两个阶段读物品时抛出的。
            LevelManager.OnControllingCharacterChanged += OnControllingCharacterChanged;

            // 预修复物品模板里结构错误的自定义数据（实机日志确认的 "Index was out of range" 源头：
            // 模板里的 BulletCount 声明为 Int 但字节为空，游戏每读一次弹匣状态就抛一次异常）。
            // 先修模板 → 之后 InstantiateAsync 出来的物品天然合法 → 关卡初始化阶段的报错大幅减少。
            // 用 UniTask 延后到物品表就绪之后执行，避免在 Awake 里抢初始化。
            UniTask.Void(async () =>
            {
                try
                {
                    await UniTask.WaitUntil(() => ItemAssetsCollection.Instance != null && ItemAssetsCollection.Instance.entries != null);
                    BloodMoon.Utils.ItemDataSanitizer.EnsurePrefabTemplatesRepaired();
                    // 导出英文资产名 ↔ 中文显示名对照表（preset 资产名与中文名无可推导规律，必须运行时读）
                    BloodMoon.Utils.NameTableDumper.DumpOnce();
                }
                catch (System.Exception ex)
                {
                    ModLogger.Warning($"[ModBehaviour] Prefab item-data repair failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 玩家受伤回调 → 难度系统记录受击量
        /// </summary>
        private void OnPlayerHurt(Health health, DamageInfo dmg)
        {
            if (_difficulty == null || health == null) return;
            var main = CharacterMainControl.Main;
            if (main == null) return;
            if (health.TryGetCharacter() != main) return; // 只统计玩家本人受击
            _difficulty.ReportPlayerDamage(dmg.finalDamage);
        }

        /// <summary>
        /// 模组启动时调用，加载保存的数据并初始化Boss管理器
        /// </summary>
        private void Start()
        {
            Load();
            _bossManager.Initialize();
        }

        /// <summary>
        /// 模组销毁时调用，清理资源和事件监听器
        /// </summary>
        private void OnDestroy()
        {
            Health.OnHurt -= OnPlayerHurt;
            SavesSystem.OnCollectSaveData -= Save;
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            LevelManager.OnControllingCharacterChanged -= OnControllingCharacterChanged;
            _bossManager?.Dispose();
            _overlay?.Dispose();
            _ui?.Dispose();
            ModLogger.Shutdown();
        }

        /// <summary>
        /// 主控角色就位（早于 pet/物品装配）时立刻修复其物品树的自定义数据，
        /// 尽量赶在游戏读取这些物品之前把结构错误补上。
        /// 同时把**仓库（PlayerStorage）与宠物代理容器**也修一遍 ——
        /// 实机统计显示每次关卡初始化都会固定报 8（基地）/ ~21（raid）条
        /// "Index was out of range"，说明来源是这批"存档载入的物品"，
        /// 而玩家的角色物品树本身已经是干净的。
        /// </summary>
        private void OnControllingCharacterChanged(CharacterMainControl character)
        {
            try
            {
                if (!BloodMoon.Utils.ModConfig.Instance.RepairItemCustomData) return;

                if (character != null && character.CharacterItem != null)
                {
                    BloodMoon.Utils.ItemDataSanitizer.SanitizeTree(character.CharacterItem, maxDepth: 2);
                }

                RepairStorageInventories();
            }
            catch (System.Exception e)
            {
                ModLogger.Warning($"[ModBehaviour] Early item-data repair failed: {e.Message}");
            }
        }

        /// <summary>仓库 / 宠物代理 / 战利品箱里的物品也做一次自定义数据修复</summary>
        private void RepairStorageInventories()
        {
            try
            {
                var storage = PlayerStorage.Inventory;
                if (storage != null)
                {
                    foreach (var item in storage)
                    {
                        if (item != null) BloodMoon.Utils.ItemDataSanitizer.Sanitize(item);
                    }
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Warning($"[ModBehaviour] PlayerStorage repair failed: {e.Message}");
            }

            try
            {
                var petInv = PetProxy.PetInventory;
                if (petInv != null)
                {
                    foreach (var item in petInv)
                    {
                        if (item != null) BloodMoon.Utils.ItemDataSanitizer.Sanitize(item);
                    }
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Warning($"[ModBehaviour] PetProxy repair failed: {e.Message}");
            }
        }

        /// <summary>
        /// 保存模组数据到存档系统
        /// </summary>
        private void Save()
        {
            _event.Save();
            _dataStore.Save();
        }

        /// <summary>
        /// 从存档系统加载模组数据
        /// </summary>
        private void Load()
        {
            _event.Load();
            _dataStore.Load();
        }

        /// <summary>
        /// 模组启用时调用，附加UI和事件监听器
        /// </summary>
        private void OnEnable()
        {
            UniTask.Void(async () =>
            {
                await UniTask.WaitUntil(() => LevelManager.LevelInited);
                if (this == null || _ui == null) return;
                
                float timeout = 5.0f;
                while (timeout > 0f)
                {
                    if (_ui.TryAttachToTimeOfDayDisplay()) break;
                    await UniTask.Delay(500);
                    timeout -= 0.5f;
                }
            });
            SubscribeOnLevelInit();
        }

        /// <summary>
        /// 模组禁用时，移除事件监听器
        /// </summary>
        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
        }

        /// <summary>
        /// 订阅关卡初始化事件。
        /// 修复：LevelManager.OnLevelInitialized 是 public static event（LevelManager.cs:268），
        /// 可以直接 += 订阅；原先用反射读私有委托字段，字段名一旦变化就会静默失效
        /// （失败时连 Boss/随从/武器系统都不会启动）。
        /// </summary>
        private void SubscribeOnLevelInit()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;   // 防重复订阅
            LevelManager.OnLevelInitialized += OnLevelInitialized;
        }

        /// <summary>
        /// 关卡初始化时调用，设置场景
        /// </summary>
        private void OnLevelInitialized()
        {
            _ui.TryAttachToTimeOfDayDisplay();

            // 修复 P0-7：地图记忆按场景分文件存储，换图后必须重新载入当图数据
            // （原来只在 Start() 里 Load 一次，切图后一直在用上一张图/初始图的记忆）
            _dataStore.ReloadForCurrentScene();

            // 新场景的角色要重新做受击体体检
            BloodMoon.Utils.HitboxFix.ResetScanCache();

            // 修复实机日志中的 "Index was out of range"（游戏 CustomData.GetInt 在字节缺失时抛异常，
            // 并把物品数量读成 0）：把关卡载入的物品树里结构错误的自定义数据补齐为默认值。
            // 只写默认值、不碰数值平衡，可用 ModConfig.RepairItemCustomData 关闭。
            TryRepairItemData();

            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel) return;

            var now = GameClock.Now;
            // 统一走 SceneUtils：LevelManager.IsRaidMap 在基地/加载场景上不可靠
            if (_event.IsActive(now) && BloodMoon.Utils.SceneUtils.IsRaidScene())
            {
                _bossManager.StartSceneSetupParallel();
            }
        }

        /// <summary>
        /// 修复玩家（以及宠物代理容器）物品树里的自定义数据结构错误。
        /// 放在关卡初始化之后执行：此时物品已经从存档载入完毕。
        /// </summary>
        private void TryRepairItemData()
        {
            try
            {
                if (!BloodMoon.Utils.ModConfig.Instance.RepairItemCustomData) return;

                // 兜底：若启动时的预修复还没跑（例如物品表当时未就绪），这里补一次
                BloodMoon.Utils.ItemDataSanitizer.EnsurePrefabTemplatesRepaired();

                var main = CharacterMainControl.Main;
                if (main != null && main.CharacterItem != null)
                {
                    BloodMoon.Utils.ItemDataSanitizer.SanitizeTree(main.CharacterItem);
                }

                var pet = LevelManager.Instance != null ? LevelManager.Instance.PetProxy : null;
                if (pet != null && pet.Inventory != null)
                {
                    foreach (var item in pet.Inventory)
                    {
                        BloodMoon.Utils.ItemDataSanitizer.Sanitize(item);
                    }
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Warning($"[ModBehaviour] TryRepairItemData failed: {e.Message}");
            }
        }

        private float _uiRefreshTimer;

        /// <summary>
        /// 每帧更新，处理血月逻辑和UI刷新
        /// </summary>
        private void Update()
        {
            var now = GameClock.Now;
            bool active = _event.IsActive(now);

            _uiRefreshTimer += Time.deltaTime;
            if (_uiRefreshTimer > 0.5f)
            {
                _uiRefreshTimer = 0f;
                _ui.Refresh(now);
                _dataStore.UpdateCache();
                // 修复 P1-13：AI 记忆改为脏标记 + 心跳落盘，避免一次 raid 结束后同帧多次全量写盘
                _dataStore.FlushIfDirty();
                // 受击体体检：给场上所有敌人（含游戏原生 Boss）做一次检查，
                // 修掉"模型很大但受击碰撞体很小 → 子弹穿过去"的情况
                BloodMoon.Utils.HitboxFix.ScanAllCharacters(_dataStore.AllCharacters);
            }
            
            if (LevelManager.Instance == null) 
            {
                _overlay.Hide();
                return;
            }

            // 统一走 SceneUtils：基地/加载场景不会被误判成 raid
            // （否则血月期间红色覆盖层 + Boss Tick 会在基地里也生效）
            if (BloodMoon.Utils.SceneUtils.IsRaidScene())
            {
                if (active)
                {
                    // P2 接线：EnableRedOverlay 此前是"从未被读取"的配置项（改了没反应）。
                    // 关闭时走 Hide() 而不是不调用 —— Hide 会驱动 Tick 里的淡出过渡，
                    // 把雾/环境光恢复到 CaptureOriginals 记录的原值。
                    if (BloodMoon.Utils.ModConfig.Instance.EnableRedOverlay) _overlay.Show();
                    else _overlay.Hide();
                    _bossManager.Tick();
                    _squadManager.Update();
                }
                else
                {
                    _overlay.Hide();
                }
            }
            else
            {
                _overlay.Hide();
            }
        }

        /// <summary>
        /// 每帧后期更新，处理视觉效果
        /// </summary>
        private void LateUpdate()
        {
            _overlay?.Tick(Time.deltaTime);
        }
    }
}
