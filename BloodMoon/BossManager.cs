using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Duckov;
using Duckov.Utilities;
using Duckov.Scenes;
using ItemStatsSystem;
using Duckov.ItemUsage;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Threading.Tasks;
using BloodMoon.Utils;
using Logger = BloodMoon.Utils.Logger;   // 消歧：UnityEngine 也有一个 Logger 类型

namespace BloodMoon
{
    /// <summary>
    /// Boss管理器，负责Boss的生成、增强和管理
    /// </summary>
    public class BossManager
    {
        private readonly AIDataStore _store;
        private bool _initialized;
        private readonly HashSet<CharacterMainControl> _processed = new HashSet<CharacterMainControl>();
        private readonly HashSet<CharacterMainControl> _minionSpawned = new HashSet<CharacterMainControl>(); // 已刷过随从的 Boss（防无限刷）
        private float _recheckSpawnerTimer = 15f;
        
        private int _currentScene = -1;
        private bool _sceneSetupDone;
        private List<Points> _pointsCache = new List<Points>();
        private float _scanCooldown;
        private bool _setupRunning;
        private int _minionsPerBossCap => ModConfig.Instance.BossMinionCount;
        private readonly List<CharacterRandomPreset> _selectedBossPresets = new List<CharacterRandomPreset>();
        private int _selectedScene = -1;
        private bool _selectionInitialized;
        private readonly Dictionary<CharacterMainControl, Vector3> _groupAnchors = new Dictionary<CharacterMainControl, Vector3>();
        private float _bloodMoonStartTime = -1f;
        private float _bloodMoonDurationSec => ModConfig.Instance.ActiveHours * 3600f;
        private System.TimeSpan _bloodMoonStartGameTime;
        
        private bool _bloodMoonActive;
        private readonly List<CharacterMainControl> _charactersCache = new List<CharacterMainControl>();
        private List<MonoBehaviour> _disabledSpawners = new List<MonoBehaviour>();
        /// <summary>场景初始化任务的取消源（修复 P0-4）</summary>
        private System.Threading.CancellationTokenSource? _setupCts;
        private float _weightCheckTimer;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly string EmissionKeyword = "_EMISSION";
        /// <summary>模组自己创建的临时刷怪组名，禁用原版刷怪器时要跳过它（P1-6）</summary>
        private const string ModSpawnerGroupName = "BloodMoon_SpawnerGroup";

        /// <summary>
        /// 构造函数，初始化Boss管理器
        /// </summary>
        /// <param name="store">AI数据存储</param>
        public BossManager(AIDataStore store)
        {
            _store = store;
        }

        /// <summary>
        /// 初始化Boss管理器
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            RaidUtilities.OnRaidEnd += OnRaidEnded;
            RaidUtilities.OnRaidDead += OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded += OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy += OnMultiSceneDestroyed;
            // 供名称对照表导出查询"该 preset 是否被 BloodMoon 过滤"
            BloodMoon.Utils.BossPresetFilterBridge.IsBlockedImpl = IsPresetBlocked;
            _initialized = true;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            RaidUtilities.OnRaidEnd -= OnRaidEnded;
            RaidUtilities.OnRaidDead -= OnRaidEnded;
            MultiSceneCore.OnSubSceneWillBeUnloaded -= OnSubSceneWillBeUnloaded;
            MultiSceneCore.OnInstanceDestroy -= OnMultiSceneDestroyed;
            CancelPendingSetup();
            // 修复 P0-3：停用模组时必须把被禁用的原版刷怪器恢复，否则它们会一直保持 SetActive(false)
            EnableDefaultSpawner();
            _initialized = false;
        }

        /// <summary>
        /// 重置会话状态
        /// </summary>
        private void ResetSession()
        {
            CancelPendingSetup();
            _setupRunning = false;
            _sceneSetupDone = false;
            _bloodMoonActive = false;
            _bloodMoonStartTime = -1f;
            _bloodMoonStartGameTime = default;
            _processed.Clear();
            _minionSpawned.Clear();
            _groupAnchors.Clear();
            _pointsCache.Clear();
            _charactersCache.Clear();
            _selectedBossPresets.Clear();
            _selectionInitialized = false;
            // 修复 P0-3：原版刷怪器被 SetActive(false) 后 FindObjectsOfType 再也找不到它们，
            // 所以不能只清空引用列表——必须先恢复，再清空。
            // （正常 raid 结束路径 OnRaidEnded → EndBloodMoon 已经恢复过，这里是幂等的）
            EnableDefaultSpawner();
        }

        /// <summary>
        /// 取消进行中的场景初始化任务（修复 P0-4：原先没有任何取消机制，
        /// 超时/切图后旧任务仍会继续生成 Boss/随从并写共享集合）
        /// </summary>
        private void CancelPendingSetup()
        {
            try
            {
                _setupCts?.Cancel();
                _setupCts?.Dispose();
            }
            catch (System.Exception e)
            {
                Logger.Warning($"CancelPendingSetup error: {e.Message}");
            }
            _setupCts = null;
        }

        /// <summary>
        /// 主更新循环
        /// </summary>
        public void Tick()
        {
            try 
            {
                if (!_initialized) return;

                // 统一走 SceneUtils：LevelManager.IsRaidMap 在基地/加载场景上不可靠
                if (!LevelManager.Instance || !BloodMoon.Utils.SceneUtils.IsRaidScene())
                {
                    return;
                }

                // 血月期间每 15 秒重新禁用一次默认生成器：
                // 多场景结构的 raid 会在玩家进入新区域时加载新子场景，新子场景的原版生成器
                // 不在首次禁用范围内 → 会持续在玩家身边刷怪（无限刷的根因之一）
                _recheckSpawnerTimer -= Time.deltaTime;
                if (_recheckSpawnerTimer <= 0f)
                {
                    _recheckSpawnerTimer = 15f;
                    if (ModConfig.Instance.DisableDefaultSpawners) DisableDefaultSpawner();
                }

                _weightCheckTimer -= Time.deltaTime;
                if (_weightCheckTimer <= 0f)
                {
                    _weightCheckTimer = 10.0f;
                    // 安全的集合遍历
                    var list = _processed.ToList();
                    foreach(var c in list)
                    {
                        if (c != null && c.gameObject.activeInHierarchy && c.Health.CurrentHealth > 0)
                        {
                            FixWeight(c, c.CharacterItem);
                        }
                        else
                        {
                            if (c != null) _processed.Remove(c);
                        }
                    }
                }
                
                _scanCooldown -= Time.deltaTime;
                if (_scanCooldown > 0f)
                {
                if (_scanCooldown > 0.9f)
                {
                     if (_store != null)
                     {
                         _store.DecayAndPrune(Time.time, 120f);
                     }
                }
                    return;
                }

                var player = CharacterMainControl.Main;
                if (!_sceneSetupDone || _setupRunning)
                {
                    return;
                }

                if (_store == null) return;
                // 修复 P0-5：扫描冷却必须在扫描**之前**设置。原先放在循环之后，
                // 循环内任一异常（如 c.Health 为空）都会跳过它 → 每帧重跑扫描并每帧 LogError。
                _scanCooldown = 1.0f;
                // 优化：仅处理有效的角色
                var all = _store.AllCharacters;
                int count = all.Count;
                var playerPos = player != null ? player.transform.position : Vector3.zero;
                bool hasPlayer = player != null;
                
                for (int i = 0; i < count; i++)
                {
                    var c = all[i];
                    if (c == null || c.IsMainCharacter) continue;
                    
                    if (_processed.Contains(c)) continue;
                    // 已刷过随从的 Boss 不再刷（配合 _processed 双保险，防"每秒新 Boss 每秒刷随从"的无限循环）
                    if (_minionSpawned.Contains(c)) continue;

                    // 距离检查优化：距离玩家太远的角色不处理
                    if (hasPlayer && Vector3.SqrMagnitude(c.transform.position - playerPos) > 40000f) // 200m^2
                    {
                        continue;
                    }

                    var preset = c.characterPreset;
                    bool isBoss = preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon;
                    if (!isBoss) continue;
                    
                    if (_selectionInitialized && preset != null && !_selectedBossPresets.Contains(preset))
                    {
                        continue;
                    }
                    
                    EnhanceBoss(c);
                    EnableRevenge(c, player);
                    SpawnMinionsForBoss(c, c.transform.position, c.gameObject.scene.buildIndex).Forget();
                    _processed.Add(c);
                    _minionSpawned.Add(c);
                    BloodMoon.Utils.Logger.Log($"Enhanced existing boss: {c.name} preset={preset?.name}");
                }

                // P2 清理：这里原本每 30 秒调用 _store.DecayWeights()，维护的是"策略权重"表 ——
                // 该表没有任何读取者（GetWeight 无调用点），已整条删除，连同 _strategyDecayTimer。
            }
            catch (System.Exception e)
            {
                Logger.Error($"Tick Error: {e}");
            }
        }

        /// <summary>
        /// 内置的 preset 排除表（名字关键字，忽略大小写）。
        ///
        /// ★ 2026-09-10 用运行时导出的中英对照表（`name_table_presets.csv`）核对后修正：
        ///   · **风暴机器人 = `EnemyPreset_Vehicle_Storm`（`Cname_StormRobot`），team = player** ——
        ///     它是**玩家自己的载具**（有交互按钮=可以上车），压根不是敌人，也从来不在血月池里。
        ///     所以上一版按 `boss_storm` 关键字封杀是**封错了对象**（封掉的是真 Boss），已撤销。
        ///     真正要挡的是"玩家阵营的角色"，改由 <see cref="IsPresetBlocked(CharacterRandomPreset)"/>
        ///     里的 `team == Teams.player` 语义判断统一处理（载具/队友/宠物/任务 NPC 共 14 个）。
        ///   · 保留：任务发布 NPC（艾力克斯/佛哥/小明）、开发测试 preset（马/载具测试/测试拾荒者/维达测试）。
        /// </summary>
        private static readonly string[] BuiltInPresetBlocklist =
        {
            "questgiver", "quest_giver",
            "vehicletest", "_test", "test_", "scav_test"
        };

        private static readonly HashSet<string> _blockedPresetSeen = new HashSet<string>();

        /// <summary>
        /// 该 preset 是否被排除（内置表 + 配置里的黑名单，按名字关键字，逗号分隔，忽略大小写）。
        /// 匹配三个名字：资产名（`EnemyPreset_XXX`）、本地化 key（`Cname_XXX`）、**中文显示名**。
        /// 后者是玩家在游戏里看到的名字，所以配置里可以直接写中文（例如 "风暴机器人"）。
        /// </summary>
        private static bool IsPresetBlocked(CharacterRandomPreset? preset)
        {
            if (preset == null) return true;

            // 语义过滤①：**玩家阵营的角色一律不碰**。
            // 依据运行时导出的 `name_table_presets.csv`：team=player 的有 14 个 ——
            // 载具（风暴机器人/摩托车/马）、任务 NPC（艾力克斯/佛哥/小明）、队友（MatePreset_PMC）、
            // 宠物（PetPreset_NormalPet）、炸弹小车、煤球 等。
            // 这些被 `SetTeam(Teams.wolf)` 后会把玩家自己的载具/队友变成敌人，必须排除。
            if (preset.team == Teams.player)
            {
                _skippedPlayerTeam++;
                if (ModConfig.Instance.EnableDebugLogging && _blockedPresetSeen.Add(preset.name))
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Preset skipped (player team): {preset.name}");
                }
                return true;
            }

            // 语义过滤②：**没有本地化名字的 preset 一律不碰**。
            // 导出表里这类是无名开发条目：`DummyEnemyCharacterRandomPresetLv 0..5`、
            // `EnemyPreset_LittleBoss`、`EnemyPreset_Basement`。它们没有中文名，
            // 刷出来就是个"无名敌人"，属于开发残留。
            if (string.IsNullOrEmpty(preset.nameKey))
            {
                _skippedNoNameKey++;
                if (ModConfig.Instance.EnableDebugLogging && _blockedPresetSeen.Add(preset.name))
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Preset skipped (no nameKey / dev entry): {preset.name}");
                }
                return true;
            }

            if (IsPresetBlocked(preset.name)) return true;
            if (IsPresetBlocked(preset.nameKey)) return true;

            // 语义过滤③：商人/任务 NPC（本地化 key 前缀判定，因为它们的图标标记不可靠）
            string key = preset.nameKey ?? string.Empty;
            string lowerKey = key.ToLowerInvariant();
            for (int i = 0; i < NonCombatKeyPrefixes.Length; i++)
            {
                if (lowerKey.StartsWith(NonCombatKeyPrefixes[i]) || lowerKey.Contains(NonCombatKeyPrefixes[i]))
                {
                    _skippedNonCombatNpc++;
                    if (ModConfig.Instance.EnableDebugLogging && _blockedPresetSeen.Add(preset.name))
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Preset skipped (non-combat NPC, key={key}): {preset.name}");
                    }
                    return true;
                }
            }

            string? displayName = null;
            try { displayName = preset.DisplayName; } catch { }
            if (!string.IsNullOrEmpty(displayName) && IsPresetBlocked(displayName)) return true;

            return false;
        }

        /// <summary>
        /// preset 是否挂着"非战斗"图标（宠物 / 商人）。
        /// 这是**游戏自己的标记**（`CharacterRandomPreset.GetCharacterIcon()` 由私有枚举
        /// `characterIconType` 决定），比按名字猜关键字可靠得多。
        /// 注意必须显式判 null —— `CharacterIconTypes.none` 会返回 null，而 `UIStyle` 里的图标
        /// 在资源未就绪时也可能为 null，直接 `==` 比较会出现 `null == null` 的假阳性
        /// （第 9 轮导出对照表时就踩过这个坑，见 `NameTableDumper`）。
        /// </summary>
        private static bool HasNonCombatIcon(CharacterRandomPreset preset)
        {
            try
            {
                var icon = preset.GetCharacterIcon();
                if (icon == null) return false;
                return icon == GameplayDataSettings.UIStyle.PetCharacterIcon
                    || icon == GameplayDataSettings.UIStyle.MerchantCharacterIcon;
            }
            catch
            {
                return false;   // 图标枚举越界等异常一律不参与过滤，交给名字规则兜底
            }
        }

        /// <summary>本会话被各类规则跳过的 preset 计数（只在调试模式逐条打印，汇总打一次 INFO）</summary>
        private static int _skippedPlayerTeam;
        private static int _skippedNoNameKey;
        private static int _skippedNonCombatNpc;
        private static int _skippedByKeyword;
        private static bool _presetFilterSummaryLogged;

        /// <summary>打印一次 preset 过滤汇总（每个会话一次）</summary>
        private static void LogPresetFilterSummaryOnce()
        {
            if (_presetFilterSummaryLogged) return;
            int total = _skippedPlayerTeam + _skippedNoNameKey + _skippedNonCombatNpc + _skippedByKeyword;
            if (total == 0) return;
            _presetFilterSummaryLogged = true;
            BloodMoon.Utils.Logger.Log(
                $"[BossManager] Preset filter: skipped {total} " +
                $"(playerTeam={_skippedPlayerTeam}, noNameKey={_skippedNoNameKey}, " +
                $"nonCombatNpc={_skippedNonCombatNpc}, keyword={_skippedByKeyword})");
        }

        /// <summary>
        /// 该 preset 能否作为血月的**随从**。
        /// 额外要求：不是 Boss 级单位（`isBoss`）、不是"对所有人敌对"的野怪（`team == all`）。
        /// 依据：实测把 `Boss_LionHead`(狮子头)、`Boss_Fly_Alone`(落单的蝇蝇队员)、
        /// `Melee_UltraMan`(光之男) 当随从刷出来过 —— 它们是 Boss 级/全场敌对单位，不该做小弟。
        /// </summary>
        private static bool CanBeMinion(CharacterRandomPreset? preset)
        {
            if (preset == null) return false;
            if (IsPresetBlocked(preset)) return false;
            if (preset.isBoss) return false;
            if (preset.team == Teams.all) return false;
            return true;
        }

        private static bool IsPresetBlocked(string? presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            string lower = presetName!.ToLowerInvariant();

            for (int i = 0; i < BuiltInPresetBlocklist.Length; i++)
            {
                if (lower.Contains(BuiltInPresetBlocklist[i]))
                {
                    _skippedByKeyword++;
                    if (ModConfig.Instance.EnableDebugLogging && _blockedPresetSeen.Add(presetName))
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Preset blocked (built-in): {presetName}");
                    }
                    return true;
                }
            }

            var blocklist = ModConfig.Instance.BossPresetBlocklist;
            if (string.IsNullOrWhiteSpace(blocklist)) return false;

            var parts = blocklist.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string key = parts[i].Trim().ToLowerInvariant();
                if (key.Length == 0) continue;
                if (lower.Contains(key))
                {
                    if (ModConfig.Instance.EnableDebugLogging && _blockedPresetSeen.Add(presetName))
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Preset blocked (config): {presetName}");
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 商人 / 任务 NPC 的本地化 key 前缀（导出表里 `MerchantName_Myst`=神秘商人、
        /// `Character_Alex`=艾力克斯 等）。这些不是战斗单位，绝不能进血月池。
        /// 注意：**图标检查对商人不可靠** —— `EnemyPreset_Merchant_Myst` 的 merchant 图标是 False，
        /// 实测它被当成随从刷出来过，所以这里按 key 前缀再兜一层。
        /// </summary>
        private static readonly string[] NonCombatKeyPrefixes = { "merchantname_", "merchant_", "trader" };

        /// <summary>
        /// 禁用原生AI
        /// </summary>
        /// <param name="c">角色控制器</param>
        private void DisableVanillaAI(CharacterMainControl c)
        {
            if (c == null || c.IsMainCharacter) return;

            // 1) 关掉原生 AI 控制器组件
            var ai = c.GetComponent<AICharacterController>();
            if (ai != null)
            {
                if (ai.enabled) ai.enabled = false;
            }

            // 2) 关掉行为树 owner（NodeCanvas BehaviourTree : GraphOwner）。
            //    注意：AICharacterController.enabled=false 并不能阻止行为树继续调用它的方法
            //    （树通过 TransformToType(GetComponent) 直接拿组件实例），所以必须停 owner 本身。
            StopGraphOwners(c, "GraphOwner");
            // VisualScripting 的 ScriptMachine（旧版写法 FlowScriptController 在本版本源码树中不存在，
            // 这里保留字符串尝试，取不到就自然跳过）
            StopGraphOwners(c, "FlowScriptController");
        }

        /// <summary>
        /// 停掉角色身上所有指定名字的图 owner 组件。
        /// 修复 P1-15：原实现用 GetMethod("StopBehaviour") 取到的是
        /// StopBehaviour(bool success = true)（GraphOwner.cs:336），
        /// 而 Invoke(owner, null) 会抛 "Parameter count mismatch" 并被空 catch 吞掉 —— 等于没停。
        /// 现在显式按签名取方法并传入参数；即使反射失败，enabled=false 仍然生效。
        /// </summary>
        private static void StopGraphOwners(CharacterMainControl c, string typeName)
        {
            var components = c.GetComponents<MonoBehaviour>();
            if (components == null) return;
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var t = comp.GetType();
                if (t.Name != typeName && !IsSubclassOfNamed(t, typeName)) continue;

                TryInvokeStopBehaviour(comp);
                if (comp.enabled) comp.enabled = false;
            }
        }

        private static bool IsSubclassOfNamed(System.Type t, string typeName)
        {
            var baseType = t.BaseType;
            while (baseType != null)
            {
                if (baseType.Name == typeName) return true;
                baseType = baseType.BaseType;
            }
            return false;
        }

        private static void TryInvokeStopBehaviour(MonoBehaviour comp)
        {
            try
            {
                var t = comp.GetType();
                // StopBehaviour(bool success = true) ——必须显式传参，不能用 Invoke(obj, null)
                var method = t.GetMethod("StopBehaviour", new System.Type[] { typeof(bool) });
                if (method != null)
                {
                    method.Invoke(comp, new object[] { true });
                    return;
                }
                // 兜底：无参重载
                var noArg = t.GetMethod("StopBehaviour", System.Type.EmptyTypes);
                if (noArg != null) noArg.Invoke(comp, null);
            }
            catch (System.Exception e)
            {
                BloodMoon.Utils.Logger.Warning($"StopBehaviour failed on {comp.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// 为Boss生成随从
        /// </summary>
        /// <param name="boss">Boss角色</param>
        /// <param name="anchor">生成锚点</param>
        /// <param name="scene">场景</param>
        private async UniTask SpawnMinionsForBoss(CharacterMainControl boss, Vector3 anchor, int scene)
        {
            if (boss == null) return;

            var presets = GameplayDataSettings.CharacterRandomPresetData.presets;
            if (presets == null) return;

            var minionPresets = presets
                .Where(p => p != null && 
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.BossCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.MerchantCharacterIcon &&
                       p.GetCharacterIcon() != GameplayDataSettings.UIStyle.PetCharacterIcon &&
                       !p.name.ToLower().Contains("pet") &&
                       !p.name.ToLower().Contains("dog") &&
                       !p.name.ToLower().Contains("cat") &&
                       !p.name.ToLower().Contains("animal") &&
                       !p.name.ToLower().Contains("companion") &&
                       CanBeMinion(p))
                .ToList();

            if (minionPresets.Count == 0) return;

            int count = ModConfig.Instance.BossMinionCount;
            count = Mathf.Clamp(count, 1, 10); // 下限降到 1：尊重配置，避免至少 3 个随从的固定开销

            // 批量生成以减少每帧开销
            for (int i = 0; i < count; i++)
            { 
                 // 修复 P0-4：随从生成是 fire-and-forget 的异步链，会话被取消（切图/超时）后必须停止
                 if (_setupCts == null || _setupCts.IsCancellationRequested) return;
                 var preset = minionPresets[UnityEngine.Random.Range(0, minionPresets.Count)];
                 
                 Vector3 bossPos = boss.transform.position;
                 var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(3f, 8f);
                 Vector3 spawnPos = bossPos + new Vector3(offset.x, 0, offset.y);
                 
                 if (AstarPath.active != null)
                 {
                     var nn = Pathfinding.NNConstraint.Walkable;
                     var node = AstarPath.active.GetNearest(spawnPos, nn).node;
                     if (node != null && node.Walkable)
                     {
                         spawnPos = (Vector3)node.position;
                     }
                 }
                 else if (Physics.Raycast(spawnPos + Vector3.up * 5f, Vector3.down, out var groundHit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                 {
                     spawnPos = groundHit.point;
                 }

                 try
                 {
                     var groupObj = new GameObject("BloodMoon_SpawnerGroup");
                     var group = groupObj.AddComponent<CharacterSpawnerGroup>();
                     CharacterMainControl clone = null!;
                     try
                     {
                         clone = await preset.CreateCharacterAsync(spawnPos, Vector3.forward, scene, group, false);
                     }
                     finally
                     {
                         if (groupObj != null) UnityEngine.Object.Destroy(groupObj);
                     }
                     if (clone != null)
                     {
                         // 减少等待时间
                         await UniTask.Yield(PlayerLoopTiming.Update);
                         
                         if (clone == null) continue;
                         
                         if (IsPetOrNPC(clone))
                         {
                             BloodMoon.Utils.Logger.Warning($"Skipping pet/NPC minion: {clone.name}");
                             if (clone != null && clone.gameObject != null)
                             {
                                 UnityEngine.Object.Destroy(clone.gameObject);
                             }
                             continue;
                         }
                         
                         // 快速初始化
                         var anim = clone.GetComponent<Animator>();
                         if (anim != null && !anim.isInitialized) 
                         {
                             // 不再长时间等待，而是让它在后台初始化
                             // await UniTask.Delay(200); 
                         }

                         var charItem = clone.CharacterItem;
                         if (charItem == null) continue;

                         DisableVanillaAI(clone);
                         
                         Multiply(charItem, "WalkSpeed", ModConfig.Instance.MinionSpeedMultiplier);
                         Multiply(charItem, "RunSpeed", ModConfig.Instance.MinionSpeedMultiplier);
                         
                         BoostDefense(charItem, false);
                         
                         var mh = charItem.GetStat("MaxHealth".GetHashCode());
                         if (mh != null) mh.BaseValue *= ModConfig.Instance.MinionHealthMultiplier;
                         clone.Health.SetHealth(clone.Health.MaxHealth);
                         
                         FixWeight(clone, charItem);

                         clone.SetTeam(Teams.wolf);
                         
                        // 并行武器检查
                        EnsureMinionHasWeapons(clone).Forget();
                        
                        var custom = clone.gameObject.AddComponent<BloodMoonAIController>();
                        custom.Init(clone, _store);
                        custom.SetChaseDelay(0f);
                        
                        var ai = clone.GetComponent<AICharacterController>();
                        if (ai != null) ai.leader = boss;

                        _processed.Add(clone);
                        _charactersCache.Add(clone);
                        BloodMoon.Utils.Logger.Log($"Minion spawned: {clone.name} preset={preset?.name}");
                     }
                 }
                 catch (System.Exception ex)
                 {
                     Logger.Error($"Minion Spawn Error: {ex}");
                 }
            }
            
            AttachControllersToFollowers(boss);
        }

        /// <summary>
        /// 突袭结束事件处理
        /// </summary>
        /// <param name="info">突袭信息</param>
        private void OnRaidEnded(RaidUtilities.RaidInfo info)
        {
            EndBloodMoon();
            _store.RequestSave();
            ResetSession();
        }

        /// <summary>
        /// 子场景将卸载事件处理
        /// </summary>
        /// <param name="core">多场景核心</param>
        /// <param name="scene">场景</param>
        private void OnSubSceneWillBeUnloaded(Duckov.Scenes.MultiSceneCore core, UnityEngine.SceneManagement.Scene scene)
        {
            _store.RequestSave();
        }

        /// <summary>
        /// 多场景销毁事件处理
        /// </summary>
        /// <param name="core">多场景核心</param>
        private void OnMultiSceneDestroyed(Duckov.Scenes.MultiSceneCore core)
        {
            _store.RequestSave();
            ResetSession();
        }

        /// <summary>
        /// 结束血月
        /// </summary>
        private void EndBloodMoon()
        {
            _bloodMoonActive = false;
            EnableDefaultSpawner();
            _store.RequestSave();
        }

        /// <summary>
        /// 启用默认生成器
        /// </summary>
        private void EnableDefaultSpawner()
        {
            int restored = 0;
            foreach (var s in _disabledSpawners)
            {
                if (s != null && !s.gameObject.activeSelf)
                {
                    s.gameObject.SetActive(true);
                    restored++;
                }
            }
            _disabledSpawners.Clear();
            // 诊断日志：确认被禁用的原版刷怪器真的恢复了（P0-3）
            if (restored > 0)
                BloodMoon.Utils.Logger.Log($"Restored {restored} default spawners.");
        }

        /// <summary>
        /// 禁用默认生成器（覆盖全部生成器类型：根组件 + 独立 Spawner + 组生成器）
        /// </summary>
        private void DisableDefaultSpawner()
        {
            int count = 0;
            count += DisableSpawnerType<CharacterSpawnerRoot>();
            count += DisableSpawnerType<RandomCharacterSpawner>();
            count += DisableSpawnerType<WaveCharacterSpawner>();
            count += DisableSpawnerType<CharacterSpawnerGroup>();
            count += DisableSpawnerType<CharacterSpawnerGroupSelector>();
            if (count > 0)
                BloodMoon.Utils.Logger.Log($"Disabled {count} default spawners.");
        }

        private int DisableSpawnerType<T>() where T : MonoBehaviour
        {
            int count = 0;
            var spawners = UnityEngine.Object.FindObjectsOfType<T>();
            if (spawners == null) return 0;
            foreach (var s in spawners)
            {
                if (s != null && s.gameObject.activeSelf)
                {
                    // 修复 P1-6：模组自己为了调用 CreateCharacterAsync(group) 临时创建的
                    // BloodMoon_SpawnerGroup 也是 CharacterSpawnerGroup，会被这里连带禁用。
                    if (s.gameObject.name == ModSpawnerGroupName) continue;
                    s.gameObject.SetActive(false);
                    if (!_disabledSpawners.Contains(s)) _disabledSpawners.Add(s);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 在玩家周围 [minDist, maxDist] 环带找导航网格可达点（Boss 生成无刷怪点时的兜底）
        /// </summary>
        private static Vector3? FindSpawnPosNearPlayer(CharacterMainControl player, float minDist, float maxDist)
        {
            if (player == null) return null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(minDist, maxDist);
                Vector3 candidate = player.transform.position + new Vector3(offset.x, 0f, offset.y);

                if (AstarPath.active != null)
                {
                    var nn = Pathfinding.NNConstraint.Walkable;
                    var node = AstarPath.active.GetNearest(candidate, nn).node;
                    if (node != null && node.Walkable)
                    {
                        candidate = (Vector3)node.position;
                        float dist = Vector3.Distance(candidate, player.transform.position);
                        if (dist >= minDist) return candidate;
                    }
                }
                else if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    return hit.point;
                }
            }
            return null;
        }

        /// <summary>
        /// 修复角色重量问题
        /// </summary>
        /// <param name="c">角色控制器</param>
        /// <param name="item">角色物品</param>
        private void FixWeight(CharacterMainControl c, Item item)
        {
            if (c == null || item == null) return;

            var mw = item.GetStat("MaxWeight".GetHashCode());
            if (mw != null) mw.BaseValue = 10000f;

            var cap = item.GetStat("InventoryCapacity".GetHashCode());
            if (cap != null) cap.BaseValue = 200f;

            c.RemoveBuffsByTag(Duckov.Buffs.Buff.BuffExclusiveTags.Weight, removeOneLayer: false);
            
            c.UpdateWeightState();
        }

        /// <summary>
        /// 增强Boss
        /// </summary>
        /// <param name="c">Boss角色</param>
        private void EnhanceBoss(CharacterMainControl c)
        {
            DisableVanillaAI(c);
            
            try
            {
                var item = c.CharacterItem;
                if (item == null) return;
                var maxHealth = item.GetStat("MaxHealth".GetHashCode());
                if (maxHealth != null) maxHealth.BaseValue *= ModConfig.Instance.BossHealthMultiplier;
                
                Multiply(item, "WalkSpeed", ModConfig.Instance.BossSpeedMultiplier);
                Multiply(item, "RunSpeed", ModConfig.Instance.BossSpeedMultiplier);
                Multiply(item, "TurnSpeed", ModConfig.Instance.BossSpeedMultiplier);
                BoostDefense(item, true);
                
                FixWeight(c, item);
                
                c.Health.SetHealth(c.Health.MaxHealth);
                
                var custom = c.GetComponent<BloodMoonAIController>();
                if (custom == null)
                {
                    custom = c.gameObject.AddComponent<BloodMoonAIController>();
                    custom.Init(c, _store);
                    custom.SetChaseDelay(0f);
                }
                if (c.Team != Teams.wolf) c.SetTeam(Teams.wolf);
                if (ModConfig.Instance.EnableBossGlow) AddBossGlow(c);

                AddRandomLoot(c).Forget();
            }
            catch (System.Exception e)
            {
                Logger.Error($"EnhanceBoss Error: {e}");
            }
        }

        /// <summary>
        /// 为Boss添加随机战利品
        /// </summary>
        /// <param name="c">Boss角色</param>
        private async UniTaskVoid AddRandomLoot(CharacterMainControl c)
        {
            if (c == null || c.CharacterItem == null || c.CharacterItem.Inventory == null) return;

            try
            {
                var filter = new ItemFilter
                {
                    minQuality = ModConfig.Instance.BossLootMinQuality,
                    maxQuality = ModConfig.Instance.BossLootMaxQuality
                };

                int[]? ids = null;
                try
                {
                    ids = ItemAssetsCollection.Search(filter);
                }
                catch {}
                
                if (ids != null && ids.Length > 0)
                {
                    int count = UnityEngine.Random.Range(ModConfig.Instance.BossLootMinCount, ModConfig.Instance.BossLootMaxCount + 1);
                    for (int i = 0; i < count; i++)
                    {
                        int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                        var item = await ItemAssetsCollection.InstantiateAsync(id);
                        if (item != null)
                        {
                            if (!c.CharacterItem.Inventory.AddAndMerge(item))
                            {
                                item.Drop(c.transform.position, true, Vector3.up, 360f);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Logger.Error($"Add Loot Error: {e}");
            }
        }

        /// <summary>
        /// 为Boss添加发光效果
        /// </summary>
        /// <param name="c">Boss角色</param>
        private void AddBossGlow(CharacterMainControl c)
        {
            var lightObj = new GameObject("BossGlowLight");
            lightObj.transform.SetParent(c.transform);
            lightObj.transform.localPosition = Vector3.up * 1.5f;
            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.1f, 0.1f);
            light.range = ModConfig.Instance.BossGlowRange;
            light.intensity = ModConfig.Instance.BossGlowIntensity;
            light.shadows = LightShadows.Soft;

            var renderers = c.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                
                var mats = r.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat.HasProperty(EmissionColorId))
                    {
                        mat.EnableKeyword(EmissionKeyword);
                        mat.SetColor(EmissionColorId, new Color(0.8f, 0.1f, 0.1f) * 2f);
                    }
                }
            }
        }

        /// <summary>
        /// 乘以属性值
        /// </summary>
        /// <param name="item">角色物品</param>
        /// <param name="stat">属性名</param>
        /// <param name="m">乘数</param>
        private void Multiply(Item item, string stat, float m)
        {
            var s = item.GetStat(stat.GetHashCode());
            if (s != null) s.BaseValue *= m;
        }

        /// <summary>
        /// 启用复仇机制
        /// </summary>
        /// <param name="c">Boss角色</param>
        /// <param name="player">玩家</param>
        private void EnableRevenge(CharacterMainControl c, CharacterMainControl? player)
        {
            if (player == null || c == null) return;
            
            if (player.mainDamageReceiver == null) return;

            var ai = c.GetComponent<AICharacterController>();
            if (ai != null && player.mainDamageReceiver.transform != null)
            {
                ai.SetTarget(player.mainDamageReceiver.transform);
                ai.forceTracePlayerDistance = 100f;
            }
            
            var custom = c.GetComponent<BloodMoonAIController>();
            if (custom != null && custom.CanChase == false)
            {
                custom.SetChaseDelay(0f);
            }
            
            string[] taunts = { "Boss_Revenge", "Boss_Taunt_1", "Boss_Taunt_2", "Boss_Taunt_3" };
            string key = taunts[UnityEngine.Random.Range(0, taunts.Length)];
            
            try
            {
                c.PopText(Localization.Get(key));
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Warning($"Failed to show taunt text: {ex.Message}");
            }
        }

        /// <summary>
        /// 增强防御
        /// </summary>
        /// <param name="item">角色物品</param>
        /// <param name="isBoss">是否是Boss</param>
        private void BoostDefense(Item item, bool isBoss)
        {
            var body = item.GetStat("BodyArmor".GetHashCode());
            var head = item.GetStat("HeadArmor".GetHashCode());
            float bodyTarget = isBoss ? ModConfig.Instance.BossBodyArmor : ModConfig.Instance.MinionBodyArmor;
            float headTarget = isBoss ? ModConfig.Instance.BossHeadArmor : ModConfig.Instance.MinionHeadArmor;
            if (body != null) body.BaseValue = Mathf.Max(body.BaseValue, bodyTarget);
            if (head != null) head.BaseValue = Mathf.Max(head.BaseValue, headTarget);
        }
        
        /// <summary>
        /// 确保随从有武器
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask EnsureMinionHasWeapons(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;

            // 修复 P1-2：本方法与 BloodMoonAIController.FindWeaponsAsync() 会在同一帧先后
            // fire-and-forget，两条管线都做"查槽位 → 生成 → 入包 → 插槽 → 补弹"，
            // 可给同一角色发两把枪/两把近战和双份弹药。用配装锁互斥。
            if (!BloodMoon.AI.EnhancedWeaponManager.TryBeginLoadout(character, "BossManager"))
            {
                return;
            }

            try
            {
                bool hasMelee = false;
                bool hasGun = false;

                if (character.MeleeWeaponSlot()?.Content != null) hasMelee = true;
                if (character.PrimWeaponSlot()?.Content != null) hasGun = true;
                if (character.SecWeaponSlot()?.Content != null) hasGun = true;

                if (hasMelee && hasGun) return;

                if (!hasMelee)
                {
                    await TryAddMeleeWeapon(character);
                }

                if (!hasGun)
                {
                    await TryAddGun(character);
                }
            }
            finally
            {
                BloodMoon.AI.EnhancedWeaponManager.EndLoadout(character);
            }
        }
        
        /// <summary>
        /// 尝试添加近战武器
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask TryAddMeleeWeapon(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            try
            {
                if (character.MeleeWeaponSlot()?.Content == null)
                {
                    var item = await BloodMoon.AI.EnhancedWeaponManager.Instance.SpawnRandomMeleeWeapon();
                    if (item == null)
                    {
                        Logger.Warning($"Minion missing melee weapon: {character.name} (All searches failed)");
                        return;
                    }

                    var slot = character.MeleeWeaponSlot();
                    // 修复 P1-7：统一顺序为"先入包，再插槽"。入包失败（背包满）才销毁；
                    // 入包成功但插槽不可用时保留在背包里并告警，绝不销毁仍在背包中的物品。
                    bool added = character.CharacterItem.Inventory.AddAndMerge(item);
                    if (!added)
                    {
                        UnityEngine.Object.Destroy(item.gameObject);
                        Logger.Warning($"Minion inventory full, melee weapon discarded: {character.name}");
                        return;
                    }
                    if (slot != null && slot.CanPlug(item))
                    {
                        slot.Plug(item, out var _);
                        return;
                    }
                    Logger.Warning($"Melee weapon added to inventory but slot rejected it: {character.name}");
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Failed to add melee weapon: {ex}");
            }
        }
        
        /// <summary>
        /// 尝试添加枪械
        /// </summary>
        /// <param name="character">角色控制器</param>
        private async UniTask TryAddGun(CharacterMainControl character)
        {
            if (character == null || character.CharacterItem == null) return;
            
            try
            {
                if (character.PrimWeaponSlot()?.Content == null && character.SecWeaponSlot()?.Content == null)
                {
                    var item = await BloodMoon.AI.EnhancedWeaponManager.Instance.SpawnRandomGun();
                    if (item == null)
                    {
                        Logger.Warning($"Minion missing guns: {character.name} (All searches failed)");
                        return;
                    }

                    var pSlot = character.PrimWeaponSlot();
                    var sSlot = character.SecWeaponSlot();

                    bool added = character.CharacterItem.Inventory.AddAndMerge(item);
                    if (!added)
                    {
                        UnityEngine.Object.Destroy(item.gameObject);
                        Logger.Warning($"Minion inventory full, gun discarded: {character.name}");
                        return;
                    }

                    if (pSlot != null && pSlot.CanPlug(item))
                    {
                        pSlot.Plug(item, out var _);
                        await AddAmmoForGun(character, item);
                        return;
                    }
                    if (sSlot != null && sSlot.CanPlug(item))
                    {
                        sSlot.Plug(item, out var _);
                        await AddAmmoForGun(character, item);
                        return;
                    }
                    Logger.Warning($"Gun added to inventory but no slot accepted it: {character.name}");
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Failed to add gun: {ex}");
            }
        }

        /// <summary>
        /// 为枪械添加弹药
        /// </summary>
        /// <param name="c">角色控制器</param>
        /// <param name="gun">枪械</param>
        private async UniTask AddAmmoForGun(CharacterMainControl c, Item gun)
        {
            await BloodMoon.AI.EnhancedWeaponManager.Instance.EnsureAmmo(c, gun);
        }

        /// <summary>
        /// 为领袖分配侧翼索引
        /// </summary>
        /// <param name="boss">Boss角色</param>
        /// <summary>
        /// 把 Boss 的跟随者（`AICharacterController.leader == boss`）纳入自研 AI 控制。
        /// P2 清理：原方法名 `AssignWingIndicesForLeader` 与其"翼位"逻辑已随死代码一并移除，
        /// 这里只保留真正生效的两件事：确保跟随者挂上 BloodMoonAIController（P1-15 修复），
        /// 以及（曾用于 LeaderPrefs 的）编队基线计算 —— 后者已删除，因为该数据全仓无人读取。
        /// </summary>
        private void AttachControllersToFollowers(CharacterMainControl boss)
        {
            var controllers = new List<AICharacterController>();
            var all = _store.AllCharacters;
            int count = all.Count;
            for(int i=0; i<count; i++) {
                var c = all[i];
                if (c == null) continue;
                var ai = c.GetComponent<AICharacterController>();
                if (ai != null) controllers.Add(ai);
            }
            
            var followers = new List<CharacterMainControl>();
            foreach (var ai in controllers)
            {
                if (ai == null) continue;
                if (ai.leader == boss)
                {
                    var ch = ai.GetComponent<CharacterMainControl>();
                    if (ch != null) followers.Add(ch);
                }
            }

            followers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            for (int i = 0; i < followers.Count; i++)
            {
                var f = followers[i];
                if (f == null) continue;
                
                var ctrl = f.gameObject.GetComponent<BloodMoonAIController>();
                if (ctrl == null)
                {
                    // 修复 P1-15：这处生成站点原先没有停原版 AI，会和自研 AI 抢移动/开火
                    DisableVanillaAI(f);
                    ctrl = f.gameObject.AddComponent<BloodMoonAIController>();
                    ctrl.Init(f, _store);
                    ctrl.SetChaseDelay(0f);
                }
            }
        }

        /// <summary>
        /// 开始场景设置
        /// </summary>
        public void StartSceneSetupParallel()
        {
            if (!_initialized) return;
            // 统一走 SceneUtils：LevelManager.IsRaidMap 在基地/加载场景上不可靠
            if (!LevelManager.Instance || !BloodMoon.Utils.SceneUtils.IsRaidScene()) return;
            
            if (!_setupRunning && !_sceneSetupDone) 
            {
                 // 用游戏时钟判断血月是否已超时（之前误用 Time.time 现实秒，几乎永不触发）
                 if (_bloodMoonActive && (GameClock.Now - _bloodMoonStartGameTime).TotalHours > ModConfig.Instance.ActiveHours)
                 {
                     ResetSession();
                 }
            }

            if (_setupRunning) return;
            _setupRunning = true;

            _bloodMoonStartTime = Time.time;
            _bloodMoonStartGameTime = GameClock.Now;
            _bloodMoonActive = true;

            // 修复 P0-4：每次场景初始化都用一个全新的取消源，ResetSession/Dispose 会 Cancel 它，
            // 避免超时或切图后旧任务继续生成敌人并写共享集合。
            CancelPendingSetup();
            _setupCts = new System.Threading.CancellationTokenSource();
            var ct = _setupCts.Token;

            UniTask.Void(async () =>
            {
                try
                {
                    await UniTask.Delay(3000, cancellationToken: ct);
                    if (ct.IsCancellationRequested) return;

                    await UniTask.Yield();

                    _pointsCache.Clear();
                    // 修复 P0-3：上一轮被禁用的刷怪器要先恢复，否则这里的 FindObjectsOfType
                    // 找不到它们（SetActive(false) 的对象默认不参与查找），旧引用一丢就永久失效。
                    EnableDefaultSpawner();
                    if (ct.IsCancellationRequested) return;

                    // 注意顺序：必须先收集刷怪点，再禁用生成器！
                    // （生成器被 SetActive(false) 后 FindObjectsOfType 找不到 → _pointsCache 为空
                    //  → Boss 锚点 fallback 到玩家脚下 → 敌人全刷在玩家身边）
                    var spawners = UnityEngine.Object.FindObjectsOfType<RandomCharacterSpawner>();
                    foreach (var s in spawners)
                    {
                        if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    }

                    await UniTask.Yield();
                    if (ct.IsCancellationRequested) return;

                    var waveSpawners = UnityEngine.Object.FindObjectsOfType<WaveCharacterSpawner>();
                    foreach (var s in waveSpawners)
                    {
                        if (s.spawnPoints != null) _pointsCache.Add(s.spawnPoints);
                    }

                    // 收集完成后才禁用生成器（血月期间不再刷怪）
                    if (ModConfig.Instance.DisableDefaultSpawners) DisableDefaultSpawner();
                }
                catch (System.OperationCanceledException)
                {
                    // 正常取消（切图/超时/停用模组）：直接放弃整条初始化链
                    _setupRunning = false;
                    return;
                }
                catch (System.Exception e)
                {
                    Logger.Error($"Scene setup error: {e}");
                }

                if (ct.IsCancellationRequested) { _setupRunning = false; return; }

                _charactersCache.Clear();
                _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());
                try 
                {
                    if (CharacterMainControl.Main == null)
                    {
                        // 带超时等待玩家角色：避免永久挂起导致血月内容永不生成
                        float waitTimeout = 10f;
                        while (CharacterMainControl.Main == null && waitTimeout > 0f)
                        {
                            await UniTask.Delay(500);
                            waitTimeout -= 0.5f;
                        }
                    }
                    
                    var player = CharacterMainControl.Main;
                    if (player == null)
                    {
                         _setupRunning = false;
                         return;
                    }

                    int scene = MultiSceneCore.MainScene.HasValue ? MultiSceneCore.MainScene.Value.buildIndex : SceneManager.GetActiveScene().buildIndex;
                    _processed.Clear();
                    
                    _charactersCache.Clear();
                    _charactersCache.AddRange(UnityEngine.Object.FindObjectsOfType<CharacterMainControl>());

                    _currentScene = scene;
                    _sceneSetupDone = false;
                    
                    var availableScenes = new List<int>();
                    foreach(var p in _pointsCache)
                    {
                        if (p != null)
                        {
                            int sIdx = p.gameObject.scene.buildIndex;
                            if (!availableScenes.Contains(sIdx)) availableScenes.Add(sIdx);
                        }
                    }
                    if (availableScenes.Count == 0) availableScenes.Add(scene);
                    
                    // 宠物排除改为**图标语义判定**（原先是 name.ToLower().Contains("pet"/"dog"/"cat"/…)）：
                    // 名字里带 "pet" 才排除是纯猜测，而游戏本来就有明确标记
                    // —— `CharacterRandomPreset.GetCharacterIcon()` 返回 `UIStyle.PetCharacterIcon`。
                    // 实测 `PetPreset_NormalPet` 的 team 是 player，本来就被语义过滤①挡住了，
                    // 所以旧写法既多余又可能误伤（例如名字里含 "cat" 的普通敌人）。
                    var allBossPresets = GameplayDataSettings.CharacterRandomPresetData.presets
                        .Where(p => p != null &&
                               (p.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon || p.isBoss) &&
                               !HasNonCombatIcon(p) &&
                               !IsPresetBlocked(p))
                        .ToArray();

                    // 过滤汇总：逐条 skip 日志只在调试模式输出，这里每个会话打一次 INFO 汇总
                    LogPresetFilterSummaryOnce();

                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Log($"Boss candidate presets ({allBossPresets.Length}): " +
                                                   string.Join(", ", allBossPresets.Select(p => p.name)));
                    }

                    CharacterRandomPreset[] selected = System.Array.Empty<CharacterRandomPreset>();

                    var rnd = new System.Random();
                    int bossCount = ModConfig.Instance.BossCount;
                    bossCount = Mathf.Clamp(bossCount, 1, 5);
                    selected = allBossPresets.OrderBy(_ => rnd.Next()).Take(bossCount).ToArray();

                    // 选中的 Boss preset 名单（排查用，默认只在调试模式输出）
                    if (ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Log($"Selected boss presets: " +
                                                   (selected.Length > 0 ? string.Join(", ", selected.Select(p => p.name)) : "(none)"));
                    }

                    _selectedBossPresets.Clear();
                    _selectedBossPresets.AddRange(selected);
                    _selectedScene = scene;
                    _selectionInitialized = true;

                    int sceneCursor = 0;
                    foreach (var preset in _selectedBossPresets)
                    {
                        // 修复 P0-4：每次迭代都检查取消，避免切图后继续生成 Boss
                        if (ct.IsCancellationRequested) { _setupRunning = false; return; }
                        bool exists = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>().Any(c => c.characterPreset == preset);
                        if (!exists)
                        {
                            int targetScene = availableScenes[sceneCursor % availableScenes.Count];
                            sceneCursor++;
                            
                            Vector3 anchor = Vector3.zero;
                            bool found = false;
                            
                            if (_pointsCache != null && _pointsCache.Count > 0)
                            {
                                try 
                                {
                                    var pt = _pointsCache[UnityEngine.Random.Range(0, _pointsCache.Count)];
                                    if (pt != null)
                                    {
                                        anchor = pt.GetRandomPoint();
                                        targetScene = pt.gameObject.scene.buildIndex;
                                        found = true;
                                    }
                                }
                                catch {}
                            }
                            
                            if (!found)
                            {
                                // 没有刷怪点可用时：在玩家周围 40-90m 找导航网格可达点。
                                // 旧版直接 fallback 到玩家位置 → Boss 刷在玩家脚下（玩家身边刷怪的根因）
                                var nearPos = FindSpawnPosNearPlayer(player, 40f, 90f);
                                if (nearPos == null)
                                {
                                    BloodMoon.Utils.Logger.Warning("No valid spawn position for boss, skipping.");
                                    continue;
                                }
                                anchor = nearPos.Value;
                                targetScene = scene;
                            }
                            
                            try
                            {
                                var groupObj = new GameObject("BloodMoon_SpawnerGroup");
                                var group = groupObj.AddComponent<CharacterSpawnerGroup>();
                                CharacterMainControl clone = null!;
                                try
                                {
                                    clone = await preset.CreateCharacterAsync(anchor, Vector3.forward, targetScene, group, false);
                                }
                                finally
                                {
                                    if (groupObj != null) UnityEngine.Object.Destroy(groupObj);
                                }
                                if (clone != null)
                                {
                                    await UniTask.Yield(PlayerLoopTiming.Update); 
                                    await UniTask.Delay(1000); 

                                    if (clone == null) continue;
                                    if (ct.IsCancellationRequested) { _setupRunning = false; return; }
                                    
                                    if (IsPetOrNPC(clone))
                                    {
                                        BloodMoon.Utils.Logger.Warning($"Skipping pet/NPC character: {clone.name}");
                                        if (clone != null && clone.gameObject != null)
                                        {
                                            UnityEngine.Object.Destroy(clone.gameObject);
                                        }
                                        continue;
                                    }
                                    
                                    var anim = clone.GetComponent<Animator>();
                                    if (anim == null || !anim.isInitialized)
                                    {
                                         await UniTask.Delay(500);
                                    }

                                    EnhanceBoss(clone);
                                    EnableRevenge(clone, player);
                                    SpawnMinionsForBoss(clone, anchor, targetScene).Forget();
                                    _processed.Add(clone);
                                    _minionSpawned.Add(clone);
                                    _groupAnchors[clone] = anchor;
                                    _charactersCache.Add(clone);
                                    BloodMoon.Utils.Logger.Log($"Boss spawned: {clone.name} preset={preset?.name} at {clone.transform.position}");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logger.Error($"Create Boss Error: {ex}");
                            }
                    }
                }

                _sceneSetupDone = true;
            }
            catch (System.Exception e)
            {
                Logger.Error($"Setup Failed: {e}");
            }
            finally
            {
                _setupRunning = false;
            }
        });
        }
        
        /// <summary>
        /// 检查角色是否是宠物或NPC
        /// </summary>
        /// <param name="character">角色控制器</param>
        /// <returns>如果是宠物或NPC则返回true</returns>
        private bool IsPetOrNPC(CharacterMainControl character)
        {
            if (character == null) return false;
            
            try
            {
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Checking if character {character.name} is pet/NPC");
                }
                
                var preset = character.characterPreset;
                if (preset != null)
                {
                    var icon = preset.GetCharacterIcon();
                    if (icon != null)
                    {
                        var petIcon = GameplayDataSettings.UIStyle.PetCharacterIcon;
                        if (petIcon != null && icon == petIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"Character {character.name} is a pet (pet icon detected)");
                            return true;
                        }
                        
                        var merchantIcon = GameplayDataSettings.UIStyle.MerchantCharacterIcon;
                        if (merchantIcon != null && icon == merchantIcon)
                        {
                            BloodMoon.Utils.Logger.Warning($"Character {character.name} is a merchant (merchant icon detected)");
                            return true;
                        }
                        
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} icon check passed (not pet/merchant)");
                        }
                    }
                    else
                    {
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no icon");
                        }
                    }
                }
                else
                {
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} has no characterPreset");
                    }
                }
                
                string characterName = character.name.ToLower();
                string[] petKeywords = { "pet", "dog", "cat", "animal", "companion" };
                string[] npcKeywords = { "npc", "merchant", "trader", "quest", "civilian" };
                
                foreach (var keyword in petKeywords)
                {
                    if (characterName.Contains(keyword))
                    {
                        BloodMoon.Utils.Logger.Warning($"Character {character.name} is likely a pet (name contains '{keyword}')");
                        return true;
                    }
                }
                
                foreach (var keyword in npcKeywords)
                {
                    if (characterName.Contains(keyword))
                    {
                        BloodMoon.Utils.Logger.Warning($"Character {character.name} is likely an NPC (name contains '{keyword}')");
                        return true;
                    }
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} name check passed (no pet/NPC keywords)");
                }
                
                var petAI = character.GetComponent<PetAI>();
                if (petAI != null)
                {
                    BloodMoon.Utils.Logger.Warning($"Character {character.name} has PetAI component");
                    return true;
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} component check passed (no PetAI)");
                }
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[BossManager] Character {character.name} is NOT a pet/NPC - allowing generation");
                }
                
                return false;
            }
            catch (System.Exception ex)
            {
                BloodMoon.Utils.Logger.Error($"Error checking if character is pet/NPC: {ex.Message}");
                return false;
            }
        }
    }
}
