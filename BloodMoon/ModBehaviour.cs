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
            Load();
        }

        private void OnDestroy()
        {
            SavesSystem.OnCollectSaveData -= Save;
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
            _ui.AttachToTimeOfDayDisplay();
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
            if (active && LevelManager.Instance != null && LevelManager.Instance.IsRaidMap)
            {
                _overlay.Show();
                _overlay.Tick(Time.deltaTime);
                _bossManager.TickBloodMoon();
            }
            else
            {
                _overlay.Hide();
            }
        }
    }
}
