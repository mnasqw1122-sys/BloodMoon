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

using System.IO;
using BloodMoon.Utils;

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

        private void Awake()
        {
            // Initialize Logger and Config
            string modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            BloodMoon.Utils.Logger.Initialize(modDir);
            ModConfig.Initialize(modDir);

            BloodMoon.Utils.Logger.Log("BloodMoon Mod Loaded");
            
            _event = new BloodMoonEvent();
            _dataStore = new AIDataStore();
            _overlay = new RedOverlay();
            _bossManager = new BossManager(_dataStore);
            _ui = new BloodMoonUI(_event);
            
            _difficulty = new BloodMoon.AI.AdaptiveDifficulty();
            _difficulty.Initialize();
            
            _squadManager = new BloodMoon.AI.SquadManager();
            _squadManager.Initialize();
            
            // Initialize Weapon Manager Cache Early (async)
            UniTask.Void(async () =>
            {
                try
                {
                    await BloodMoon.AI.EnhancedWeaponManager.Instance.EnsureInitialized();
                    BloodMoon.Utils.Logger.Log("[ModBehaviour] Weapon Manager initialized successfully");
                }
                catch (System.Exception ex)
                {
                    BloodMoon.Utils.Logger.Error($"[ModBehaviour] Weapon Manager initialization failed: {ex}");
                }
            });

            SavesSystem.OnCollectSaveData += Save;
            
            Application.logMessageReceived += OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (condition.StartsWith("[BloodMoon]")) return; // Avoid infinite recursion if our logger uses Debug.Log

            // Handle both Error/Exception types and Log messages that start with "Error: "
            bool isError = type == LogType.Exception || type == LogType.Error;
            bool isErrorLog = type == LogType.Log && condition.StartsWith("Error: ");
            
            if (isError || isErrorLog)
            {
                // Enhanced error categorization and logging
                string errorCategory = "Unknown";
                string detailedMessage = condition;
                
                // Categorize errors for better debugging
                if (condition.Contains("Index was out of range") || condition.Contains("ArgumentOutOfRangeException"))
                {
                    errorCategory = "IndexOutOfRange";
                }
                else if (condition.Contains("NullReferenceException") || condition.Contains("Object reference not set"))
                {
                    errorCategory = "NullReference";
                }
                else if (condition.Contains("MissingReferenceException") || condition.Contains("The object of type"))
                {
                    errorCategory = "MissingReference";
                }
                else if (condition.Contains("InvalidOperationException"))
                {
                    errorCategory = "InvalidOperation";
                }
                else if (condition.Contains("ArgumentException"))
                {
                    errorCategory = "Argument";
                }
                else if (condition.Contains("Timeout") || condition.Contains("timed out"))
                {
                    errorCategory = "Timeout";
                }
                else if (condition.Contains("OutOfMemory") || condition.Contains("Memory"))
                {
                    errorCategory = "Memory";
                }
                
                // Log with category and stack trace
                BloodMoon.Utils.Logger.Error($"[{errorCategory}] {detailedMessage}\nStack Trace:\n{stackTrace}");
                
                // Additional handling for critical errors
                if (errorCategory == "IndexOutOfRange" || errorCategory == "NullReference")
                {
                    // These are critical errors that need immediate attention
                    BloodMoon.Utils.Logger.Error($"CRITICAL: {errorCategory} error detected. This may cause game instability.");
                    
                    // Try to log additional context if available
                    try
                    {
                        // Log current game state for debugging
                        if (LevelManager.Instance != null)
                        {
                            // Note: LevelManager may not have CurrentLevelName property in this version
                            // We'll use available properties
                            BloodMoon.Utils.Logger.Error($"Game State: IsBaseLevel={LevelManager.Instance.IsBaseLevel}, IsRaidMap={LevelManager.Instance.IsRaidMap}");
                        }
                        
                        // Log BloodMoon state
                        if (_event != null)
                        {
                            // Note: BossManager may not have GetBossCount method in this version
                            BloodMoon.Utils.Logger.Error($"BloodMoon State: Active={_event.IsActive(GameClock.Now)}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        BloodMoon.Utils.Logger.Error($"Failed to log additional context: {ex.Message}");
                    }
                }
            }
            else if (type == LogType.Warning)
            {
                // Log warnings for monitoring
                if (condition.Contains("BloodMoon") || condition.Contains("AI") || condition.Contains("Weapon"))
                {
                    BloodMoon.Utils.Logger.Warning($"Game Warning: {condition}");
                }
            }
        }

        private void Start()
        {
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
                _dataStore.UpdateCache(); // Update global AI cache
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
                _squadManager.Update();
                BloodMoon.AI.RuntimeMonitor.Instance.Update();
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
