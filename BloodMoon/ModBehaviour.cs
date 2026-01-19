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
            // 初始化日志记录器和配置
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
            
            // 提前初始化武器管理器缓存（异步）
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
            if (condition.StartsWith("[BloodMoon]")) return; // 如果我们的日志记录器使用Debug.Log，避免无限递归

            // 处理Error/Exception类型和以"Error: "开头的Log消息
            bool isError = type == LogType.Exception || type == LogType.Error;
            bool isErrorLog = type == LogType.Log && condition.StartsWith("Error: ");
            
            if (isError || isErrorLog)
            {
                // 增强的错误分类和日志记录
                string errorCategory = "Unknown";
                string detailedMessage = condition;
                
                // 为更好的调试对错误进行分类
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
                
                // 使用类别和堆栈跟踪记录日志
                BloodMoon.Utils.Logger.Error($"[{errorCategory}] {detailedMessage}\nStack Trace:\n{stackTrace}");
                
                // 对关键错误的额外处理
                if (errorCategory == "IndexOutOfRange" || errorCategory == "NullReference")
                {
                    // 这些是需要立即关注的关键错误
                    BloodMoon.Utils.Logger.Error($"CRITICAL: {errorCategory} error detected. This may cause game instability.");
                    
                    // 尝试记录可用的额外上下文
                    try
                    {
                        // 记录当前游戏状态用于调试
                        if (LevelManager.Instance != null)
                        {
                            // 注意：此版本中LevelManager可能没有CurrentLevelName属性
                            // 我们将使用可用的属性
                            BloodMoon.Utils.Logger.Error($"Game State: IsBaseLevel={LevelManager.Instance.IsBaseLevel}, IsRaidMap={LevelManager.Instance.IsRaidMap}");
                        }
                        
                        // 记录BloodMoon状态
                        if (_event != null)
                        {
                            // 注意：此版本中BossManager可能没有GetBossCount方法
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
                // 记录警告用于监控
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
                if (this == null || _ui == null) return; // await 之后的安全检查
                
                // 重试附加UI几秒钟，因为TimeOfDayDisplay可能延迟
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

            // 在每个层级启动时重新加载配置，以支持热交换值
            // BloodMoon.Utils.ModConfig.Load();

            _ui.TryAttachToTimeOfDayDisplay();
            
            // 基础级别安全检查
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

            // 限制UI更新为2Hz（每0.5秒）以节省性能
            _uiRefreshTimer += Time.deltaTime;
            if (_uiRefreshTimer > 0.5f)
            {
                _uiRefreshTimer = 0f;
                _ui.Refresh(now);
                _dataStore.UpdateCache(); // 更新全局AI缓存
            }
            
            // 逻辑安全：首先检查LevelManager是否有效
            if (LevelManager.Instance == null) 
            {
                _overlay.Hide();
                return;
            }
            
            // 在基地关卡禁用Boss逻辑，但保持UI更新
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
            // 在游戏逻辑后处理大气视觉效果覆盖
            _overlay?.Tick(Time.deltaTime);
        }
    }
}
