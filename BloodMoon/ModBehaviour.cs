using System;
using System.Collections.Generic;
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

using HarmonyLib;

namespace BloodMoon
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private BloodMoonEvent _event = null!;
        private BloodMoonUI _ui = null!;
        private RedOverlay _overlay = null!;
        private BossManager _bossManager = null!;
        private AIDataStore _dataStore = null!;
        private Harmony _harmony = null!;

        private void Awake()
        {
            Debug.Log("BloodMoon Mod Loaded");
            _event = new BloodMoonEvent();
            _dataStore = new AIDataStore();
            _overlay = new RedOverlay();
            _bossManager = new BossManager(_dataStore);
            _ui = new BloodMoonUI(_event);
            SavesSystem.OnCollectSaveData += Save;
            
            _harmony = new Harmony("com.bloodmoon.patch");
            _harmony.PatchAll();
            Patches.ManualPatch(_harmony);
        }

        private void Start()
        {
            // Force load config on main thread to avoid threading issues with Application.dataPath
            var config = BloodMoon.Utils.ModConfig.Instance;
            Load();
            _bossManager.Initialize();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll("com.bloodmoon.patch");
            SavesSystem.OnCollectSaveData -= Save;
            _bossManager?.Dispose();
            _overlay?.Dispose();
            _ui?.Dispose();
        }

        private void Save()
        {
            _event.Save();
            _dataStore.Save();
        }

        private void Load()
        {
            _event.Load();
            _dataStore.Load();
        }

        private void OnEnable()
        {
            UniTask.Void(async () =>
            {
                await UniTask.WaitUntil(() => LevelManager.LevelInited);
                _ui.AttachToTimeOfDayDisplay();
            });
            LevelManager.OnLevelInitialized += OnLevelInitialized;
        }

        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
        }

        private void OnLevelInitialized()
        {
            // Reload config on every level start to support hot-swapping values
            BloodMoon.Utils.ModConfig.Load();

            _ui.AttachToTimeOfDayDisplay();
            
            // Safety check for base level
            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel) return;

            var now = GameClock.Now;
            if (_event.IsActive(now) && LevelManager.Instance != null && LevelManager.Instance.IsRaidMap)
            {
                _bossManager.StartSceneSetupParallel();
            }
        }

        private void Update()
        {
            var now = GameClock.Now;
            bool active = _event.IsActive(now);
            _ui.Refresh(now);
            
            // Disable Boss logic in Base Level, but keep UI updating
            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel)
            {
                _overlay.Hide();
            }
            else if (active && LevelManager.Instance != null && LevelManager.Instance.IsRaidMap)
            {
                _overlay.Show();
                _bossManager.Tick();
            }
            else
            {
                _overlay.Hide();
            }
        }

        private void LateUpdate()
        {
            // Process atmosphere visual overrides after game logic
            _overlay?.Tick(Time.deltaTime);
        }
    }
}
