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
        /// LevelManager.OnLevelInitialized 反射缓存
        /// </summary>
        private static FieldInfo? _onLevelInitField;

        /// <summary>
        /// 获取 OnLevelInitialized 字段信息（带缓存）
        /// </summary>
        private static FieldInfo? GetOnLevelInitField()
        {
            if (_onLevelInitField != null) return _onLevelInitField;
            _onLevelInitField = typeof(LevelManager).GetField("OnLevelInitialized",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return _onLevelInitField;
        }

        /// <summary>
        /// 通过反射订阅 LevelManager.OnLevelInitialized 事件
        /// </summary>
        private void SubscribeOnLevelInit(Action callback)
        {
            var fi = GetOnLevelInitField();
            if (fi == null)
            {
                ModLogger.Error("[ModBehaviour] Cannot find LevelManager.OnLevelInitialized field");
                return;
            }
            var del = (Action?)fi.GetValue(null);
            del = (Action?)Delegate.Combine(del, callback);
            fi.SetValue(null, del);
        }

        /// <summary>
        /// 通过反射取消订阅 LevelManager.OnLevelInitialized 事件
        /// </summary>
        private void UnsubscribeOnLevelInit(Action callback)
        {
            var fi = GetOnLevelInitField();
            if (fi == null) return;
            var del = (Action?)fi.GetValue(null);
            del = (Action?)Delegate.Remove(del, callback);
            fi.SetValue(null, del);
        }

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
            UnsubscribeOnLevelInit(OnLevelInitialized);
            _bossManager?.Dispose();
            _overlay?.Dispose();
            _ui?.Dispose();
            ModLogger.Shutdown();
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
            SubscribeOnLevelInit(OnLevelInitialized);
        }

        /// <summary>
        /// 模组禁用时调用，移除事件监听器
        /// </summary>
        private void OnDisable()
        {
            UnsubscribeOnLevelInit(OnLevelInitialized);
        }

        /// <summary>
        /// 关卡初始化时调用，设置场景
        /// </summary>
        private void OnLevelInitialized()
        {
            _ui.TryAttachToTimeOfDayDisplay();
            
            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel) return;

            var now = GameClock.Now;
            if (_event.IsActive(now) && LevelManager.Instance != null && LevelManager.Instance.IsRaidMap)
            {
                _bossManager.StartSceneSetupParallel();
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
            }
            
            if (LevelManager.Instance == null) 
            {
                _overlay.Hide();
                return;
            }
            
            if (LevelManager.Instance.IsBaseLevel)
            {
                _overlay.Hide();
            }
            else if (active && LevelManager.Instance.IsRaidMap)
            {
                _overlay.Show();
                _bossManager.Tick();
                _squadManager.Update();
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
