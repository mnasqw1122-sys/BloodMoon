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

namespace BloodMoon
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private BloodMoonEvent _event = null!;
        private BloodMoonUI _ui = null!;
        private RedOverlay _overlay = null!;
        private BossManager _bossManager = null!;
        private AIDataStore _dataStore = null!;

        private void Awake()
        {
            Debug.Log("BloodMoon Mod Loaded");
            _event = new BloodMoonEvent();
            _dataStore = new AIDataStore();
            _overlay = new RedOverlay();
            _bossManager = new BossManager(_dataStore);
            _ui = new BloodMoonUI(_event);
            SavesSystem.OnCollectSaveData += Save;
            
            Application.logMessageReceived += OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (condition.StartsWith("[BloodMoon Debug]")) return;

            if (type == LogType.Exception || type == LogType.Error)
            {
                // Catch all IndexOutOfRange exceptions to identify source
                if (condition.Contains("Index was out of range") || condition.Contains("ArgumentOutOfRangeException"))
                {
                    Debug.LogError($"[BloodMoon Debug] Caught Index Error: {condition}\nStack: {stackTrace}");
                }
            }
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
            Application.logMessageReceived -= OnLogMessage;
            Application.logMessageReceivedThreaded -= OnLogMessage;
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
                if (this == null || _ui == null) return; // Safety check after await
                
                // Retry attaching UI for a few seconds as TimeOfDayDisplay might be late
                float timeout = 5.0f;
                while (timeout > 0f)
                {
                    if (_ui.TryAttachToTimeOfDayDisplay()) break;
                    await UniTask.Delay(500);
                    timeout -= 0.5f;
                }
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
            // BloodMoon.Utils.ModConfig.Load();

            _ui.TryAttachToTimeOfDayDisplay();
            
            // Safety check for base level
            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel) return;

            var now = GameClock.Now;
            if (_event.IsActive(now) && LevelManager.Instance != null && LevelManager.Instance.IsRaidMap)
            {
                _bossManager.StartSceneSetupParallel();
            }
        }

        private float _uiRefreshTimer;

        private void Update()
        {
            var now = GameClock.Now;
            bool active = _event.IsActive(now);

            // Throttle UI updates to 2Hz (every 0.5s) to save performance
            _uiRefreshTimer += Time.deltaTime;
            if (_uiRefreshTimer > 0.5f)
            {
                _uiRefreshTimer = 0f;
                _ui.Refresh(now);
            }
            
            // Logic Safety: Check if LevelManager is valid first
            if (LevelManager.Instance == null) 
            {
                _overlay.Hide();
                return;
            }
            
            // Disable Boss logic in Base Level, but keep UI updating
            if (LevelManager.Instance.IsBaseLevel)
            {
                _overlay.Hide();
            }
            else if (active && LevelManager.Instance.IsRaidMap)
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
