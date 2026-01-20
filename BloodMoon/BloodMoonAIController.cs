using UnityEngine;
using Duckov.Utilities;
using Duckov;
using Duckov.ItemUsage;
using Cysharp.Threading.Tasks;
using ItemStatsSystem;
using Pathfinding;
using System.Collections.Generic;
using System.Linq;
using BloodMoon.Utils;
using BloodMoon.AI;

namespace BloodMoon
{
    public enum AIRole
    {
        Standard,
        Assault,
        Sniper,
        Support
    }

    public class BloodMoonAIController : MonoBehaviour
    {
        // --- 组件 ---
        public AIRole Role; // 分配给此AI的角色
        private CharacterMainControl _c = null!;
        private AIDataStore _store = null!;
        private Seeker? _seeker;
        private Path? _path;
        
        // --- 决策系统 ---
        private AIContext _context = new AIContext();
        private List<AIAction> _actions = new List<AIAction>();
        private AIAction? _currentAction;
        private NeuralDecisionMaker _neuralBrain = null!;
        private StableBehaviorSystem _behaviorSystem = null!;
        private float _globalCooldown = 0f;
        private Dictionary<string, float> _actionSwitchCooldowns = new Dictionary<string, float>();
        
        private async UniTaskVoid FindWeaponsAsync()
        {
            if (_c == null) return;
            await ComprehensiveWeaponSystem.Instance.FindWeaponsForAI(_c);
        }
        
        // --- 移动和路径查找 ---
        private int _currentWaypoint;
        private bool _waitingForPath;
        private float _repathTimer;
        private Vector3 _lastPathTarget;
        private float _wallCheckTimer;
        private Vector3 _cachedAvoidanceDir;
        private float _stuckCheckInterval;
        private float _doorStuckTimer;
        private Vector3 _lastDoorCheckPos;
        private float _aliveTime;
        private Vector3 _prevPos;
        private float _stuckTimer;
        private float _lastPathRequestTime; // 上次路径请求时间
        private const float MIN_PATH_REQUEST_INTERVAL = 0.5f; // 最小路径请求间隔（秒）
        
        // --- 战斗状态 ---
        private float _shootTimer;
        private float _strafeTimer;
        private int _strafeDir = 1;
        private Vector3 _targetCoverPos;
        private float _coverCooldown;
        private float _dashCooldown;
        private bool _hurtRecently;
        private float _skillCooldown;
        private float _fireHoldTimer;
        private float _pressureScore;
        private float _chaseDelayTimer;
        private bool _canChase = true;
        public bool CanChase => _canChase;
        
        // --- 疗愈状态 ---
        private float _healWaitTimer;
        public bool IsHealing => _healWaitTimer > 0f;

        // --- 协作 ---
        private CharacterMainControl? _leader;
        private int _wingIndex = -1;
        public bool IsBoss;

        public CharacterMainControl? CurrentTarget => _context.Target; // 暴露给SquadManager使用

        public void SetChaseDelay(float seconds)
        {
            _chaseDelayTimer = Mathf.Max(0f, seconds);
            _canChase = _chaseDelayTimer <= 0f;
        }

        public void SetWingAssignment(CharacterMainControl leader, int index)
        {
            _leader = leader;
            _wingIndex = index;
        }

        private Squad? _currentSquad;
        public void SetSquad(Squad? squad)
        {
            _currentSquad = squad;
            if (squad != null)
            {
                // 更新小队上下文
                if (_context != null) _context.SquadOrder = SquadManager.Instance.GetOrder(this) ?? "";
            }
        }
        
        private static readonly List<BloodMoonAIController> _all = new List<BloodMoonAIController>();
        public static List<BloodMoonAIController> AllControllers => _all;

        public bool HasWeapon => _c != null && (_c.PrimWeaponSlot()?.Content != null || _c.SecWeaponSlot()?.Content != null || _c.MeleeWeaponSlot()?.Content != null);
        public float GetHealthPercentage() => _c != null ? _c.Health.CurrentHealth / _c.Health.MaxHealth : 0f;
        
        public void SetTacticalOrder(string order)
        {
            if (_context != null) _context.SquadOrder = order;
        }

        private static readonly List<CharacterMainControl> _nearbyCache = new List<CharacterMainControl>();
        private static readonly Collider[] _nonAllocColliders = new Collider[16];
        private float _sepCooldown;
        private UnityEngine.Vector3 _sepBgResult;

        // --- 搜索与巡逻 ---
        private Vector3 _searchPoint;
        private float _searchTimer;
        private Vector3 _patrolTarget;
        private float _patrolWaitTimer;
        
        private float _bossCommandCooldown;
        public bool IsRaged => IsBoss && _c != null && (_c.Health.CurrentHealth / _c.Health.MaxHealth) < 0.4f;

        public void Init(CharacterMainControl c, AIDataStore store)
        {
            _c = c;
            _store = store;
            _aliveTime = 0f;
            _seeker = c.GetComponent<Seeker>();
            if (_seeker == null) _seeker = c.gameObject.AddComponent<Seeker>();
            
            var vanilla = c.GetComponent<AICharacterController>();
            if (vanilla != null) vanilla.enabled = false;
            
            _c.Health.OnHurtEvent.AddListener(OnHurt);
            _c.Health.OnDeadEvent.AddListener(OnDeadAI);
            
            if (!_all.Contains(this)) _all.Add(this);
            
            // 初始化上下文
            _context.Character = _c;
            _context.Controller = this;
            _context.Store = _store;
            _context.Personality = AIPersonality.GenerateRandom(); // 分配随机人格
            
            // 基于人格确定角色
            if (_context.Personality.Aggression > 0.7f && _context.Personality.Caution < 0.4f) Role = AIRole.Assault;
            else if (_context.Personality.Teamwork > 0.7f) Role = AIRole.Support;
            else if (_context.Personality.Caution > 0.7f) Role = AIRole.Sniper;
            else Role = AIRole.Standard;

            // 调试日志人格
            #if DEBUG
            Utils.Logger.Debug($"[AI {_c.name}] Created with Personality: {_context.Personality} | Role: {Role}");
            #endif
            
            // 初始化操作
            _actions.Add(new Action_Unstuck());
            _actions.Add(new Action_Heal());
            _actions.Add(new Action_BossCommand()); // 新领袖行动
            _actions.Add(new Action_Rush());
            _actions.Add(new Action_Reload());
            _actions.Add(new Action_ThrowGrenade());
            _actions.Add(new Action_Retreat());
            _actions.Add(new Action_TakeCover());
            _actions.Add(new Action_Suppress());
            _actions.Add(new Action_Engage());
            _actions.Add(new Action_Flank());
            _actions.Add(new Action_Chase());
            _actions.Add(new Action_Search());
            _actions.Add(new Action_Patrol());
            _actions.Add(new Action_Panic());

            // 初始化神经脑
            var actionNames = _actions.Select(a => a.Name).ToList();
            _neuralBrain = new ContextAwareDecisionMaker(actionNames);

            // 初始化行为系统
            _behaviorSystem = new StableBehaviorSystem();
            _behaviorSystem.Initialize();

            // 查找武器（异步）
            FindWeaponsAsync().Forget();

            var preset = c.characterPreset;
            if (preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon)
            {
                IsBoss = true;
            }
            
            // 随机化时钟偏移以分散CPU负载
            _aiTickTimer = Random.Range(0f, AI_TICK_INTERVAL);
            
            if (SquadManager.Instance != null) SquadManager.Instance.RegisterAI(this);
        }

        private float _aiTickTimer;
        private const float AI_TICK_INTERVAL = 0.1f;

        private void Update()
        {
            if (!LevelManager.LevelInited || _c == null || CharacterMainControl.Main == null) 
            {
                if (_c == null) enabled = false;
                return;
            }
            
            // 保障：确保基础AI功能保持禁用状态
            var vanilla = _c.GetComponent<AICharacterController>();
            if (vanilla != null && vanilla.enabled) vanilla.enabled = false;

            _aliveTime += Time.deltaTime;
            
            // 全局更新（计时器需要平滑更新）
            UpdateTimers();

            // 节流决策制定
            _aiTickTimer += Time.deltaTime;
            if (_aiTickTimer >= AI_TICK_INTERVAL)
            {
                _aiTickTimer = 0f;
                _context.UpdateSensors();
                
                // 更新当前操作的动作时间
                if (_currentAction != null)
                {
                    _currentAction.UpdateActionTime(AI_TICK_INTERVAL);
                }
                
                // 冷却时间（基于间隔更新）
                foreach (var a in _actions) a.UpdateCooldown(AI_TICK_INTERVAL);
                
                // 更新切换冷却时间
                var keys = new List<string>(_actionSwitchCooldowns.Keys);
                foreach(var k in keys) 
                {
                    _actionSwitchCooldowns[k] -= AI_TICK_INTERVAL;
                    if (_actionSwitchCooldowns[k] <= 0) _actionSwitchCooldowns.Remove(k);
                }

                if (_globalCooldown <= 0f)
                {
                    // 具有行为持久性的决策逻辑
                    AIAction? bestAction = null;
                    float bestScore = -1f;
                    
                    // 获取神经网络分数（实验性）
                    var neuralScores = _neuralBrain.GetActionScores(_context);

                    Dictionary<string, float> allScores = new Dictionary<string, float>();

                    foreach (var action in _actions)
                    {
                        // 检查切换冷却时间
                        if (_actionSwitchCooldowns.ContainsKey(action.Name)) 
                        {
                            allScores[action.Name] = -1f;
                            continue;
                        }

                        float score = action.Evaluate(_context);
                        
                        // 混合决策：规则库（90%）+ 神经网络（10%）
                        if (neuralScores.TryGetValue(action.Name, out float nScore))
                        {
                            // 动态权重：随时间增加神经影响（5分钟后最高达40%）
                            float neuralWeight = Mathf.Lerp(0.1f, 0.4f, Mathf.Clamp01(_aliveTime / 300f));
                            score = score * (1f - neuralWeight) + nScore * neuralWeight;
                        }
                        
                        allScores[action.Name] = score;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestAction = action;
                        }
                    }
                    
                    // 使用稳定行为系统
                    string proposedActionName = bestAction?.Name ?? (_currentAction?.Name ?? "Patrol");
                    
                    List<string> availableActions = _actions.Select(a => a.Name).ToList();
                    string stableActionName = _behaviorSystem.GetStableAction(proposedActionName, bestScore, _context, availableActions, allScores);
                    
                    AIAction? stableAction = _actions.FirstOrDefault(a => a.Name == stableActionName);
                    if (stableAction == null) stableAction = bestAction;
                    
                    bool shouldSwitchAction = false;
                    
                    if (stableAction != null && stableAction != _currentAction)
                    {
                        // 检查当前操作是否可以被中断
                        if (_currentAction == null || _currentAction.CanBeInterrupted())
                        {
                            shouldSwitchAction = true;
                        }
                    }
                    
                    // 检查当前操作是否应退出
                    if (_currentAction != null && _currentAction.ShouldExit())
                    {
                        shouldSwitchAction = true;
                        // 若被迫退出，若当前稳定操作仍为有效操作，则选择最佳原始操作
                        if (stableAction == _currentAction) stableAction = bestAction;
                    }
                    
                    if (shouldSwitchAction && stableAction != null && stableAction != _currentAction)
                    {
                        if (_currentAction != null) 
                        {
                            _currentAction.OnExit(_context);
                            // 为OLD操作设置冷却时间，以防止我们立即切换回该操作（从2.0f增加到3.0f）
                            _actionSwitchCooldowns[_currentAction.Name] = 3.0f;
                        }
                        
                        // 设置全局冷却时间（从0.5f增加到1.0f以减少频繁切换）
                        _globalCooldown = 1.0f;

                        // 操作切换调试日志
                        if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                        {
                            BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Switch Action: {_currentAction?.Name ?? "None"} -> {stableAction.Name}");
                        }

                        _currentAction = stableAction;
                        _currentAction.OnEnter(_context);
                    }
                }
            }
            
            // 执行操作（每帧执行以实现平滑移动/瞄准）
            if (_currentAction != null)
            {
                _currentAction.Execute(_context);
            }
        }

        private void UpdateTimers()
        {
            _shootTimer -= Time.deltaTime;
            _coverCooldown -= Time.deltaTime;
            _dashCooldown -= Time.deltaTime;
            _skillCooldown -= Time.deltaTime;
            _fireHoldTimer -= Time.deltaTime;
            _globalCooldown -= Time.deltaTime;
            _pressureScore = Mathf.Max(0f, _pressureScore - Time.deltaTime * 0.5f);
            
            if (!_canChase)
            {
                _chaseDelayTimer -= Time.deltaTime;
                if (_chaseDelayTimer <= 0f) _canChase = true;
            }
            
            _healWaitTimer -= Time.deltaTime;
            _bossCommandCooldown -= Time.deltaTime;
        }
        
        // --- 公共动作方法 ---

        public bool PerformBossCommand()
        {
            if (_bossCommandCooldown > 0f) return false;
            
            // 使用网格对附近的随从造成伤害
            int count = 0;
            if (_store != null && _store.Grid != null)
            {
                _store.Grid.Query(_c.transform.position, 25f, _nearbyCache);
                foreach (var ally in _nearbyCache)
                {
                    if (ally == null || ally == _c) continue;
                    
                    var ai = ally.GetComponent<BloodMoonAIController>();
                    if (ai != null && !ai.IsBoss && ally.Team == _c.Team)
                    {
                        ai.ReceiveBuff();
                        count++;
                    }
                }
            }
            
            // 视觉/音频反馈可在此处
            // _c.PlaySound("Roar"); 
            
            _bossCommandCooldown = 20f; // 冷却期
            return count > 0;
        }

        public void ReceiveBuff()
        {
            // 士气提升
            _pressureScore = 0f;
            _canChase = true;
            _chaseDelayTimer = 0f;
            // 或许应该强行催促？
            // _forceAction = ActionType.Rush; 
        }

        public bool HasGrenade()
        {
             var inv = _c.CharacterItem?.Inventory; if (inv == null) return false;
             foreach(var item in inv) {
                if(item == null) continue;
                var ss = item.GetComponent<ItemSetting_Skill>();
                if(ss != null && ss.Skill != null && !item.GetComponent<Drug>()) {
                    return true;
                }
             }
             return false;
        }

        public bool HasHealingItem()
        {
             // 首先检查持有的物品
             if (_c.CurrentHoldItemAgent != null)
             {
                 var item = _c.CurrentHoldItemAgent.Item;
                 if (item.GetComponent<Drug>() != null || item.GetComponent<FoodDrink>() != null) return true;
             }
             
             var inv = _c.CharacterItem?.Inventory; if (inv == null) return false;
             foreach(var item in inv) {
                if(item == null) continue;
                if(item.GetComponent<Drug>() != null || item.GetComponent<FoodDrink>() != null) return true;
             }
             return false;
        }

        public bool PerformThrowGrenade()
        {
             var inv = _c.CharacterItem?.Inventory; if (inv == null) return false;
             
             // 智能检测：若目标距离过近（<8米）或过远（>25米），则不触发投掷
             if (_context.Target != null)
             {
                 float d = Vector3.Distance(_c.transform.position, _context.Target.transform.position);
                 if (d < 8f || d > 25f) return false;
             }

             foreach(var item in inv) {
                if(item == null) continue;
                var ss = item.GetComponent<ItemSetting_Skill>();
                if(ss != null && ss.Skill != null && !item.GetComponent<Drug>()) {
                     _c.ChangeHoldItem(item);
                     _c.SetSkill(SkillTypes.itemSkill, ss.Skill, ss.Skill.gameObject);
                     if(_c.StartSkillAim(SkillTypes.itemSkill)) {
                         _c.ReleaseSkill(SkillTypes.itemSkill);
                         return true;
                     }
                }
             }
             return false;
        }

        public void PerformRetreat()
        {
             if (_context.Target == null) return;
             // 逃离目标
             var dir = (_c.transform.position - _context.Target.transform.position).normalized;
             // 尝试找到一个遥远的点
             var retreatPos = _c.transform.position + dir * 15f;
             if (Physics.Raycast(retreatPos + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
             {
                 MoveTo(hit.point);
             }
             else
             {
                 MoveTo(retreatPos);
             }
             _c.SetRunInput(true);
        }

        public void PerformSuppression()
        {
             if (_context.Target != null)
             {
                  // 在最后已知位置开火，即使没有直接视线
                  _c.SetAimPoint(_context.LastKnownPos + Vector3.up * 1.0f);
                  _c.Trigger(true, true, false);
             }
        }

        public void PerformHeal()
        {
            // 逻辑：
            // 1. 若手持治疗物品，则使用该物品。
            // 2. 若当前不安全（无遮挡物），则必须优先寻找掩体，再进行切换/使用操作。
            // 3. 若当前安全或无法找到掩体，则执行切换并使用操作。
            // 4. 区分健康恢复类（急救包）与增益类（兴奋剂/止痛药）物品。
            
            bool isSafe = !_context.HasLoS;
            
            // 尝试寻找遮盖物以避免暴露
            // 如果领袖只是进行补充（如招聘），可能会忽略这一点，但总体而言，隐藏（相关信息）是较好的做法
            if (!isSafe && _coverCooldown <= 0f)
            {
                 // 冲刺去接！
                 if (MoveToCover()) 
                 {
                     _c.SetRunInput(true); // 跑着去追！
                     
                     // 防御性火力：若可能，在移动时进行反击！
                     if (_context.Target != null && _context.HasLoS)
                     {
                         _c.SetAimPoint(_context.Target.transform.position + Vector3.up * 1.2f);
                         _c.Trigger(true, true, false);
                     }
                     
                     return; // 不要急于治愈，先逃跑
                 }
                 else
                 {
                     // 未能找到覆盖物且我们处于暴露状态。
                     // 若敌人正在观察，切勿在开阔地带停留以进行治疗。
                     if (_context.HasLoS)
                     {
                         // 中止治疗尝试，以便Engage/Panic在下一帧接管
                         return; 
                     }
                 }
            }
            
            // 若处于安全状态或已放弃保护措施，则停止并进行修复
            _c.SetRunInput(false);
            _c.movementControl.SetMoveInput(Vector3.zero);
            
            // 当前健康状况
            float hpPercent = _c.Health.CurrentHealth / _c.Health.MaxHealth;
            bool needHealth = hpPercent < 0.8f;
            // 简化检查：假设在健康状态下处于战斗中时需要增益效果，或者在存在特定减益效果时（此处无法通过深度集成轻松检测）需要增益效果。
            // 目前，假设我们希望在生命值（HP）高于50%且处于“压力”状态时获得增益效果
            bool wantBuff = hpPercent > 0.4f && _pressureScore > 5f; 

            // 1. 检查持有物品
            if (_c.CurrentHoldItemAgent != null)
            {
                var item = _c.CurrentHoldItemAgent.Item;
                bool isDrug = item.GetComponent<Drug>() != null;
                bool isFood = item.GetComponent<FoodDrink>() != null;
                bool isBuff = item.GetComponent<Duckov.ItemUsage.AddBuff>() != null; // 显式命名空间以确保安全

                // 决策：若其符合我们的需求，则予以采用
                bool shouldUse = false;
                if (needHealth && (isDrug || isFood)) shouldUse = true;
                if (wantBuff && isBuff) shouldUse = true;
                
                // 备用方案：若持有急救包且情况危急，即使仅能进行小幅治疗也应使用它
                if (hpPercent < 0.95f && (isDrug || isFood)) shouldUse = true;

                if (shouldUse)
                {
                    bool reloading = _c.GetGun()?.IsReloading() ?? false;
                    if (!reloading && _healWaitTimer <= 0f) 
                    {
                        _c.UseItem(item);
                        _healWaitTimer = 4.0f; // 假设治疗动画耗时约4秒
                    }
                    return;
                }
            }

            // 2. 查找满足需求的最佳物品
            var inv = _c.CharacterItem?.Inventory; 
            if (inv == null) return;
            
            Item? bestItem = null;
            int bestScore = -1;
            
            foreach (var item in inv)
            {
                if (item == null) continue;
                
                var drug = item.GetComponent<Drug>();
                var food = item.GetComponent<FoodDrink>();
                var buff = item.GetComponent<Duckov.ItemUsage.AddBuff>();
                
                int score = 0;
                
                // 若受伤，应优先进行治疗
                if (needHealth)
                {
                    if (drug != null) score += (int)drug.healValue + item.Quality * 10;
                    if (food != null) score += 5 + item.Quality * 5; // 食物的优先级低于药品
                }
                
                // 在承受压力且未处于濒死状态时，应优先选择增益效果
                if (wantBuff)
                {
                    if (buff != null) score += 50 + item.Quality * 10;
                }
                
                // 若关键值（<30%），则优先选择大治疗
                if (hpPercent < 0.3f)
                {
                    if (drug != null) score += (int)drug.healValue * 2;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestItem = item;
                }
            }
            
            if (bestItem != null && bestScore > 0)
            {
                // 转换
                if (_c.CurrentHoldItemAgent?.Item != bestItem)
                {
                    _c.ChangeHoldItem(bestItem);
                    _healWaitTimer = 0.5f; // 等待装备
                }
            }
        }

        public void PerformReload()
        {
            var gun = _c.GetGun();
            if (gun != null && !gun.IsReloading())
            {
                // 智能重载逻辑（最佳弹药）
                // 目前简化处理，仅重新加载
                gun.BeginReload();
            }
            
            // 移动至掩体后重新装填
            if (_context.HasLoS)
            {
                // 1. 若可能且条件允许，应使用破折号进行覆盖
                if (_dashCooldown <= 0f)
                {
                     // 向侧面或后方猛拉
                     var dir = -_context.DirToTarget + Random.insideUnitSphere * 0.5f;
                     dir.y = 0;
                     _c.movementControl.SetMoveInput(dir.normalized);
                     _c.Dash();
                     _dashCooldown = 3f;
                }

                // 2. 若暴露，应尝试移动以遮盖
                if (MoveToCover()) return;
                
                // 3. 若未检测到覆盖，将出现异常移动（侧移）
                // 部分复用EngageTarget strafe逻辑或仅执行随机移动
                 _strafeTimer -= Time.deltaTime;
                if (_strafeTimer <= 0f)
                {
                    _strafeDir = Random.value > 0.5f ? 1 : -1;
                    _strafeTimer = 0.3f; // 快速切换
                }
                
                if (_context.Target != null)
                {
                     var dir = (_context.Target.transform.position - _c.transform.position).normalized;
                     var perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
                     // 向后及侧方移动
                     var move = (-dir * 0.5f + perp).normalized;
                     _c.movementControl.SetMoveInput(move);
                     _c.SetRunInput(true); // 在可能的情况下，在重新加载时运行
                }
            }
            else
            {
                // 略微横移或保持静止
                 _c.movementControl.SetMoveInput(Vector3.zero);
            }
        }

        public bool MoveToCover()
        {
            if (_targetCoverPos == Vector3.zero || Vector3.Distance(_c.transform.position, _targetCoverPos) < 1.0f)
            {
                // 查找新的封面
                if (_context.Target == null || !FindCover(_context.Target, out _targetCoverPos))
                {
                    return false;
                }
            }
            
            MoveTo(_targetCoverPos);
            _c.SetRunInput(true);
            return true;
        }

        public void EngageTarget()
        {
            if (_context.Target == null) return;
            
            float dist = _context.DistToTarget;
            ChooseWeapon(dist);

            // 武器类型逻辑
            var currentHold = _c.CurrentHoldItemAgent;
            bool isMelee = currentHold is ItemAgent_MeleeWeapon;

            var gun = _c.GetGun();
            bool isSniper = gun != null && gun.BulletDistance > 80f; // 启发式判断
            bool isShotgun = gun != null && gun.BulletCount < 10 && gun.BulletDistance < 20f; // 启发式判断
            
            Vector3 aimPos = PredictAim(_context.Target);
            
            // 应用基于难度的不准确性
            if (AdaptiveDifficulty.Instance != null)
            {
                float accMult = AdaptiveDifficulty.Instance.AccuracyMultiplier;
                // 根据难度添加噪声（1.0 = 正常，0.8 = 准确，1.2 = 不准确）
                // 若乘数小于1（难度更高），噪声将被降低。
                // 基础噪声半径 例如 0.5米
                float noise = 0.5f * accMult;
                aimPos += Random.insideUnitSphere * noise;
            }
            
            _c.SetAimPoint(aimPos);
            
            // 战斗中的移动（侧移）
            Vector3 dir = _context.DirToTarget;

            if (isMelee)
            {
                // 近战的直接方法
                _c.movementControl.SetMoveInput(dir);
                _c.SetRunInput(true);

                // 若在范围内则发起攻击
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }

                // 随机破折号用于闭合间隙或躲避
                if (dist > 5f && _dashCooldown <= 0f && Random.value < 0.02f)
                {
                    _c.Dash();
                    _dashCooldown = Random.Range(2f, 4f);
                }
                
                HandleDash();
                return;
            }
            
            _strafeTimer -= Time.deltaTime;
            if (_strafeTimer <= 0f)
            {
                _strafeDir = Random.value > 0.5f ? 1 : -1;
                // 根据武器随机化持续时间
                if (isSniper) _strafeTimer = Random.Range(2.0f, 4.0f); // 减少移动频率
                else if (isShotgun) _strafeTimer = Random.Range(0.3f, 0.8f); // 抖动的
                else _strafeTimer = Random.Range(0.5f, 1.5f);
            }
            
            var perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
            
            // 侧移墙检查
            var origin = _c.transform.position + Vector3.up * 1.0f;
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            if (Physics.Raycast(origin, perp, 1.5f, mask))
            {
                _strafeDir *= -1;
                perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
            }
            
            Vector3 move = Vector3.zero;
            float pref = GetPreferredDistance();

            if (isShotgun)
            {
                // 激进推进
                move = dir * 0.8f + perp * 0.4f; 
                // 若距离遥远，则应勇往直前
                if (dist > 8f && _dashCooldown <= 0f) 
                {
                    _c.movementControl.SetMoveInput(dir);
                    _c.Dash();
                    _dashCooldown = 2.5f;
                }
            }
            else if (isSniper)
            {
                // 保持距离，瞄准稳定
                if (dist < pref - 5f) move = -dir; // 撤退行动
                else if (_shootTimer > 0f) move = perp * 0.3f; // 缓慢侧移以进行冷却
                else move = Vector3.zero; // 停止射击
            }
            else // 步枪/标准型
            {
                 // 动态扫射
                 move = (perp * 0.8f + dir * 0.2f).normalized;
                 if (dist < pref - 3f) move = -dir + perp * 0.5f;
                 else if (dist > pref + 3f) move = dir + perp * 0.2f;

                 // 战斗中的随机判定
                 if (_dashCooldown <= 0f && Random.value < 0.01f) // 每帧1%的概率
                 {
                     _c.movementControl.SetMoveInput(move);
                     _c.Dash();
                     _dashCooldown = Random.Range(3f, 6f);
                 }
            }
            
            _c.movementControl.SetMoveInput(move.normalized);
            
            // 战术姿势：射击时若未快速移动，则采用ADS（瞄准镜/机械瞄具）姿势
            bool shouldAds = (dist > 15f && !isShotgun) || (isSniper);
            _c.SetAdsInput(shouldAds);
            _c.SetRunInput(!shouldAds && move.magnitude > 0.1f);
            
            // 射击
            if (_shootTimer <= 0f)
            {
                bool fire = true;
                // 对于狙击手，确保我们保持稳定？
                if (isSniper && _c.Velocity.magnitude > 1f) fire = false;

                if (fire)
                {
                    _c.Trigger(true, true, false);
                    // 突发逻辑
                    if (isSniper)
                        _shootTimer = Random.Range(1.5f, 2.5f);
                    else if (isShotgun)
                         _shootTimer = Random.Range(0.5f, 1.0f);
                    else
                        _shootTimer = Random.Range(0.2f, 0.5f);
                }
            }
            else
            {
                _c.Trigger(false, false, false);
            }
            
            ManageSkillUse(true, dist);
            HandleDash();
        }
        
        public void MoveTo(Vector3 target)
        {
            Vector3 move = GetPathMove(target);
            move = TryUnstuck(move);
            move = ApplyFormationSeparation(move);
            TryOpenDoorAhead(move);
            _c.movementControl.SetMoveInput(move);
            _c.SetRunInput(true);
            
            // 看我们要去哪里
            if (move.sqrMagnitude > 0.1f)
            {
                _c.SetAimPoint(_c.transform.position + move * 10f);
            }
        }
        
        public void PerformTacticalFlank()
        {
            if (_context.Target == null) return;
            
            // 尝试寻找一个位置，该位置应满足：
            // 1. 在15至25米范围内
            // 2. 对目标有视线（可选，但有助于最终接近）
            // 3. 与目标的正前方成大于60度的角度（侧翼攻击）
            
            var targetForward = _context.Target.transform.forward;
            var targetPos = _context.Target.transform.position;
            
            // 尝试左翼和右翼
            Vector3 bestPos = Vector3.zero;
            float bestScore = -1f;
            
            // 优化：迭代次数更少，传播范围更广
            for (int i = 0; i < 6; i++)
            {
                // 样本半圆位于目标后方/侧方
                // 角度逻辑：90度为右，-90度为左。我们希望避免0度（正前方）和180度（正后方可以接受但可能距离过远）
                // 让我们将角度设定为相对于玩家朝向的45至135度之间
                
                float side = (i % 2 == 0) ? 1f : -1f;
                float angle = UnityEngine.Random.Range(45f, 135f) * side;
                
                var rot = Quaternion.AngleAxis(angle, Vector3.up);
                var dir = rot * targetForward; // 相对于面向玩家
                var cand = targetPos + dir * UnityEngine.Random.Range(12f, 22f);
                
                // 验证基准
                if (Physics.Raycast(cand + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    cand = hit.point;
                    
                    // 得分：距离（距离越近越好，以最小化旅行时间）+ 侧翼角度 - 热量
                    float dist = Vector3.Distance(_c.transform.position, cand);
                    float heat = _store.GetHeatAt(cand, Time.time, 5f);
                    
                    // 优先选择能提供掩护的位置（目标是否被遮挡？）
                    // 对于侧翼掩护，我们实际上需要在末端获得视线（LoS），但行进路径应保持安全。
                    // 但'cand'是目标位置。我们希望目标位置具有覆盖范围或良好的视线条件。
                    
                    // 简单得分 
                    float score = 100f - dist - (heat * 15f);
                    
                    // 角度奖金：角度越接近90度越好
                    float angleDiff = Mathf.Abs(90f - Mathf.Abs(angle));
                    score += (45f - angleDiff);

                    if (score > bestScore)
                    {
                        // 检查是否可到达（通过导航网格/射线检测大致判断）
                         var origin = _c.transform.position + Vector3.up;
                         var toCand = cand - _c.transform.position;
                         // 简单的墙体检查
                         if (!Physics.Raycast(origin, toCand.normalized, toCand.magnitude, GameplayDataSettings.Layers.wallLayerMask))
                         {
                             bestScore = score;
                             bestPos = cand;
                         }
                    }
                }
            }
            
            if (bestScore > 0f)
            {
                MoveTo(bestPos);
                _c.SetRunInput(true);
            }
            else
            {
                // 回退到简单移动
                var dir = Vector3.Cross(_context.DirToTarget, Vector3.up);
                if (Random.value > 0.5f) dir = -dir;
                MoveTo(_c.transform.position + dir * 10f);
            }
        }
        
        public void PerformPatrol()
        {
            _patrolWaitTimer -= Time.deltaTime;
            if (_patrolWaitTimer > 0f)
            {
                 _c.movementControl.SetMoveInput(Vector3.zero);
                 _c.SetRunInput(false);
                 return;
            }

            // 选择一个新的点，如果我们没有一个点或者当前点接近目标
            if (_patrolTarget == Vector3.zero || Vector3.Distance(_c.transform.position, _patrolTarget) < 3f)
            {
                // 在地图上找到一个随机点
                // 尝试5次
                bool found = false;
                for(int i=0; i<5; i++)
                {
                    // 随机方向和距离（30米 - 80米）用于广域漫游
                    var rnd = Random.insideUnitCircle.normalized * Random.Range(30f, 80f);
                    var candidate = _c.transform.position + new Vector3(rnd.x, 0, rnd.y);
                    
                    if (Physics.Raycast(candidate + Vector3.up * 10f, Vector3.down, out var hit, 20f, GameplayDataSettings.Layers.groundLayerMask))
                    {
                        // 检查是否可访问（简单检查）
                        _patrolTarget = hit.point;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                     // 若失败则进行小幅调整
                     _patrolTarget = _c.transform.position + _c.transform.forward * 10f;
                }
                
                // 在转向新要点前暂停
                _patrolWaitTimer = Random.Range(2f, 5f);
                return;
            }
            
            MoveTo(_patrolTarget);
            _c.SetRunInput(false); // 巡逻时行走
        }

        public void PerformSearch()
        {
            if (_searchTimer <= 0f)
            {
                // 智能搜索
                Vector3 searchCenter = _context.LastKnownPos;
                
                // 如果上下文中的LastKnownPos非常旧或为零，则使用全局存储
                if (_store != null && (Time.time - _context.LastSeenTime > 15f || searchCenter == Vector3.zero))
                {
                     if (_store.LastKnownPlayerPos != Vector3.zero)
                        searchCenter = _store.LastKnownPlayerPos;
                }
                
                // 若仍为零，则搜索附近的随机位置
                if (searchCenter == Vector3.zero) searchCenter = _c.transform.position;

                bool found = false;
                
                // 尝试5次以在搜索中心附近找到一个隐蔽位置
                for (int i = 0; i < 5; i++)
                {
                    var rnd = Random.insideUnitCircle * 15f;
                    var p = searchCenter + new Vector3(rnd.x, 0f, rnd.y);
                    if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                    {
                        _searchPoint = hit.point;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                     var rnd = Random.insideUnitCircle * 10f;
                     var p = searchCenter + new Vector3(rnd.x, 0f, rnd.y);
                     if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                        _searchPoint = hit.point;
                     else
                        _searchPoint = p;
                }
                
                _searchTimer = 6f;
            }
            _searchTimer -= Time.deltaTime;
            MoveTo(_searchPoint);
            _c.SetRunInput(false);
            
            // 快速扫视
            float angle = Mathf.Sin(Time.time * 2f) * 60f;
            var lookDir = Quaternion.AngleAxis(angle, Vector3.up) * _c.transform.forward;
            _c.SetAimPoint(_c.transform.position + lookDir * 10f);
        }
        public void PerformRush()
        {
            if (_context.Target == null) return;
            // 直接移至目标
            MoveTo(_context.Target.transform.position);
            _c.SetRunInput(true);
            
            // 尽可能在奔跑时射击（腰射？）
            _c.SetAimPoint(_context.Target.transform.position + Vector3.up * 1.2f);
            
            // 若拥有弹药且存在大致的视线通路，则强制触发
            if (_context.HasLoS)
            {
                 _c.Trigger(true, true, false);
            }
        }

        public void PerformUnstuck()
        {
            // 激进的非阻塞
            Vector3 rnd = Random.insideUnitSphere;
            rnd.y = 0f;
            _c.movementControl.SetMoveInput(rnd.normalized);
            _c.SetRunInput(true);
            if (_dashCooldown <= 0f)
            {
                _c.Dash();
                _dashCooldown = 2f;
            }
            _stuckTimer = 0f;
            _context.IsStuck = false; // 硬件复位标志
        }

        public void PerformPanic()
        {
            // 远离危险（高温）或目标
            Vector3 runDir = Vector3.zero;
            if (_context.Target != null)
            {
                runDir = (_c.transform.position - _context.Target.transform.position).normalized;
            }
            else
            {
                runDir = Random.insideUnitSphere; runDir.y = 0;
            }
            
            // 添加一些噪声
            runDir += Random.insideUnitSphere * 0.5f;
            runDir.Normalize();
            
            MoveTo(_c.transform.position + runDir * 10f);
            _c.SetRunInput(true);
            
            // 随机射击
            if (Random.value < 0.05f)
            {
                _c.Trigger(true, true, false);
                _c.SetAimPoint(_c.transform.position + _c.transform.forward * 5f + Random.insideUnitSphere * 2f);
            }
            else
            {
                _c.Trigger(false, false, false);
            }
        }

        // --- 辅助方法（复用与适配） ---
        
        public bool CheckLineOfSight(CharacterMainControl target)
        {
            if (target == null) return false;
            var origin = _c.transform.position + Vector3.up * 1.2f;
            var tPos = target.transform.position + Vector3.up * 1.2f;
            var dir = tPos - origin;
            var mask = GameplayDataSettings.Layers.fowBlockLayers;
            if (!Physics.Raycast(origin, dir.normalized, dir.magnitude, mask)) return true;
            return false;
        }

        private Vector3 GetPathMove(Vector3 targetPos)
        {
            if (_seeker == null) return (targetPos - _c.transform.position).normalized;

            // 门/卡滞检查逻辑（优化频率）
            if (Time.time - _stuckCheckInterval > 3.0f) // 从2.0f增加以减少检查
            {
                _stuckCheckInterval = Time.time;
                if (Vector3.Distance(_c.transform.position, _lastDoorCheckPos) < 0.5f)
                {
                    _doorStuckTimer += 3.0f; // 从2.0f增加
                }
                else
                {
                    _doorStuckTimer = 0f;
                    _lastDoorCheckPos = _c.transform.position;
                }

                if (_doorStuckTimer > 9.0f) // 从6.0f增加
                {
                    _context.IsStuck = true; // 信号解除阻塞操作
                    _doorStuckTimer = 0f;
                    _repathTimer = -1f; 
                    _waitingForPath = false; 
                    return (_c.transform.position - targetPos).normalized;
                }
            }

            // 优化的目标验证与距离阈值
            float distToTarget = Vector3.Distance(_c.transform.position, targetPos);
            
            // 分层距离逻辑用于性能优化
            if (distToTarget > 100f) // 非常远——采用简单运动（从150f降低到100f）
            {
                // 目标距离非常远，采用无需路径规划的简单移动方式
                // 记录日志以便调试
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Target too far ({distToTarget:F1}m), using direct movement");
                }
                return (targetPos - _c.transform.position).normalized;
            }
            else if (distToTarget < 3f) // 非常接近——无需路径规划
            {
                // 目标非常近，直接移动
                return (targetPos - _c.transform.position).normalized;
            }

            _repathTimer -= Time.deltaTime;
            bool shouldRepath = false;
            
            // 优化的路径查找频率并进行多次检查
            if (_path == null || _path.vectorPath == null || _path.vectorPath.Count == 0 || _path.error)
            {
                shouldRepath = true;
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Path invalid, requesting repath");
                }
            }
            else if (_repathTimer <= 0f)
            {
                // 仅在目标发生显著移动时重新定位
                float targetMoveDist = Vector3.Distance(_lastPathTarget, targetPos);
                if (targetMoveDist > 10f) // 从5f增加到10f以减少不必要的重新路径
                {
                    shouldRepath = true;
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Target moved {targetMoveDist:F1}m, requesting repath");
                    }
                }
                else
                {
                    // 延长重路径计时器，因为目标移动不大
                    _repathTimer = 2.0f; // 从1.0f增加到2.0f
                }
            }
            // 简化路径有效性检查，具有较大容差
            else if (_path.vectorPath.Count > 0 && Vector3.Distance(_c.transform.position, _path.vectorPath[0]) > 30f) // 从20f增加到30f
            {
                shouldRepath = true;
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Far from path start ({Vector3.Distance(_c.transform.position, _path.vectorPath[0]):F1}m), requesting repath");
                }
            }

            if (shouldRepath && !_waitingForPath)
            {
                // 路径查找频率限制：检查是否距离上次路径请求时间太短
                float timeSinceLastPath = Time.time - _lastPathRequestTime;
                if (timeSinceLastPath < MIN_PATH_REQUEST_INTERVAL)
                {
                    // 路径请求太频繁，使用回退移动
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Path request too frequent ({timeSinceLastPath:F2}s < {MIN_PATH_REQUEST_INTERVAL:F2}s), using fallback");
                    }
                    return (targetPos - _c.transform.position).normalized;
                }
                
                // 性能优化：简单的并发路径查找限制
                // 移除RuntimeMonitor依赖，使用简化版本
                // 可以在这里添加简单的并发控制逻辑，如果需要的话
                
                _lastPathTarget = targetPos;
                _repathTimer = 3.0f; // 从2.0f增加以进一步降低路径查找负载
                _waitingForPath = true;
                _lastPathRequestTime = Time.time; // 记录本次路径请求时间
                
                // 启动路径规划
                _seeker.StartPath(_c.transform.position, targetPos, OnPathComplete);
                
                if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                {
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Starting path to target {distToTarget:F1}m away");
                }
            }

            if (_path == null || _path.vectorPath == null || _path.vectorPath.Count == 0 || _path.error)
            {
                // 增强的路径查找失败时的回退逻辑
                _wallCheckTimer -= Time.deltaTime;
                if (_wallCheckTimer <= 0f)
                {
                    _wallCheckTimer = 0.1f;
                    var dir = (targetPos - _c.transform.position).normalized;
                    _cachedAvoidanceDir = dir;
                    
                    // 多射线投射以实现更优的障碍物检测
                    var fallbackOrigin = _c.transform.position + Vector3.up * 1.0f;
                    var fallbackMask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                    
                    for (float angle = -30f; angle <= 30f; angle += 15f)
                    {
                        var rayDir = Quaternion.AngleAxis(angle, Vector3.up) * dir;
                        if (Physics.Raycast(fallbackOrigin, rayDir, out var hit, 3.5f, fallbackMask))
                        {
                            var slide = Vector3.ProjectOnPlane(rayDir, hit.normal).normalized;
                            if (Vector3.Dot(slide, dir) > 0.1f)
                            {
                                _cachedAvoidanceDir = slide;
                                break;
                            }
                        }
                        else
                        {
                            // 在此方向上无任何障碍，予以使用
                            _cachedAvoidanceDir = rayDir;
                            break;
                        }
                    }
                }
                
                // 向回退方向添加少量随机性以防止无限循环
                Vector3 randomOffset = new Vector3(Random.insideUnitCircle.x, 0f, Random.insideUnitCircle.y) * 0.1f;
                Vector3 finalDir = _cachedAvoidanceDir + randomOffset;
                finalDir.y = 0;
                return finalDir.normalized;
            }

            // 沿路径行驶并改进航点处理
            float nextWaypointDistance = 3f;
            bool reachedEnd = false;
            float dist;
            
            // 确保我们不会停留在无效的航点上
            while (_currentWaypoint < _path.vectorPath.Count)
            {
                dist = Vector3.Distance(_c.transform.position, _path.vectorPath[_currentWaypoint]);
                if (dist < nextWaypointDistance)
                {
                    _currentWaypoint++;
                    if (_currentWaypoint >= _path.vectorPath.Count)
                    {
                        reachedEnd = true;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            // 防止当前航点超出边界
            _currentWaypoint = Mathf.Clamp(_currentWaypoint, 0, _path.vectorPath.Count - 1);

            if (reachedEnd || _currentWaypoint >= _path.vectorPath.Count)
            {
                if (Vector3.Distance(_c.transform.position, targetPos) > 1.5f)
                {
                    // 检查是否已接近目标，若未接近，则重新规划路径
                    _repathTimer = 0f; // 强制在下一周期重新路由
                    return (targetPos - _c.transform.position).normalized;
                }
                return Vector3.zero;
            }

            // 获取下一个航点的方向（含安全检查）
            if (_currentWaypoint < 0 || _currentWaypoint >= _path.vectorPath.Count)
            {
                BloodMoon.Utils.Logger.Warning($"[AI {_c.name}] Invalid waypoint index: {_currentWaypoint}, path count: {_path.vectorPath.Count}");
                return Vector3.zero;
            }
            
            Vector3 dirToWaypoint = (_path.vectorPath[_currentWaypoint] - _c.transform.position).normalized;
            
            // 检查航点是否可到达（简单的射线检测检查）
            var origin = _c.transform.position + Vector3.up * 1.0f;
            var waypointPos = _path.vectorPath[_currentWaypoint] + Vector3.up * 1.0f;
            var waypointDir = (waypointPos - origin).normalized;
            var waypointDist = Vector3.Distance(origin, waypointPos);
            
            // 如果航点被阻挡，请尝试寻找更优方向
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            if (Physics.Raycast(origin, waypointDir, waypointDist, mask))
            {
                // 航点被阻挡，请尝试滑行绕过
                var slide = Vector3.ProjectOnPlane(dirToWaypoint, Vector3.up).normalized;
                if (Vector3.Dot(slide, dirToWaypoint) > 0.1f) dirToWaypoint = slide;
            }

            return dirToWaypoint;
        }
        
        // 检查目标位置是否适用于路径寻找
        private bool IsValidTargetPosition(Vector3 pos)
        {
            // 检查位置是否在地面上
            if (!Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
            {
                return false;
            }
            
            // 检查位置是否与当前位置过于遥远
            if (Vector3.Distance(_c.transform.position, pos) > 100f)
            {
                return false;
            }
            
            // 检查位置是否可访问（从上方进行简单的射线检测）
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            if (Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, 10f, mask))
            {
                return false;
            }
            
            return true;
        }
        
        // 当目标无效时，查找附近的有效位置
        private Vector3 FindNearbyValidPosition(Vector3 originalPos)
        {
            // 尝试在原始位置周围以螺旋模式找到一个有效位置
            for (int i = 1; i <= 5; i++)
            {
                float radius = i * 5f;
                int steps = i * 8;
                
                for (int j = 0; j < steps; j++)
                {
                    float angle = (float)j / steps * Mathf.PI * 2;
                    Vector3 offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    Vector3 candidate = originalPos + offset;
                    
                    if (IsValidTargetPosition(candidate))
                    {
                        return candidate;
                    }
                }
            }
            
            // 若未找到有效邻近位置，则回退到当前位置
            return _c.transform.position + (_c.transform.forward * 5f);
        }

        private void OnPathComplete(Path p)
        {
            if (!p.error)
            {
                _path = p;
                _currentWaypoint = 0;
                BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Path completed successfully, waypoints: {p.vectorPath?.Count ?? 0}");
            }
            else
            {
                BloodMoon.Utils.Logger.Warning($"[AI {_c.name}] Path failed: {p.errorLog}");
                
                // 路径查找失败的回退机制
                // 如果错误是"Couldn't find a node close to the end point"，尝试使用备用目标位置
                if (p.errorLog.Contains("Couldn't find a node close to the end point"))
                {
                    // 记录失败的目标位置
                    if (BloodMoon.Utils.ModConfig.Instance.EnableDebugLogging)
                    {
                        BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Pathfinding failed for distant target, using fallback movement");
                    }
                    
                    // 清除当前路径，强制使用直接移动
                    _path = null;
                    _currentWaypoint = 0;
                    
                    // 设置一个较长的重试延迟
                    _repathTimer = 5.0f;
                }
            }
            _waitingForPath = false;
        }
        
        private bool FindCover(CharacterMainControl player, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            if (player == null) return false;
            
            // 尝试先存储已知的封面
            if (_store.TryGetKnownCover(player, _c.transform.position, 18f, out var known))
            {
                 coverPos = known; return true;
            }
            
            var mask = GameplayDataSettings.Layers.fowBlockLayers;
            int startIdx = Random.Range(0, 8);
            
            Vector3 bestPos = Vector3.zero;
            float bestScore = -9999f;
            bool foundAny = false;

            for (int i = 0; i < 8; i++)
            {
                int idx = (startIdx + i) % 8;
                var angle = idx * 45f * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Random.Range(4f, 10f);
                var pos = _c.transform.position + offset;
                
                var origin = pos + Vector3.up * 1.0f;
                var target = player.transform.position + Vector3.up * 1.2f;
                var dir = target - origin;
                
                if (Physics.Raycast(origin, dir.normalized, dir.magnitude, mask))
                {
                    Vector3 candidate;
                    if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 4f, GameplayDataSettings.Layers.groundLayerMask))
                        candidate = hit.point;
                    else
                        candidate = pos;
                    
                    // 评估热量
                    float heat = _store.GetHeatAt(candidate, Time.time, 5f);
                    float score = -heat;
                    
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = candidate;
                        foundAny = true;
                    }
                }
            }
            
            if (foundAny)
            {
                coverPos = bestPos;
                return true;
            }
            return false;
        }

        private float _weaponSwitchTimer;
        private const float MIN_WEAPON_SWITCH_INTERVAL = 1.0f;

        private void ChooseWeapon(float dist)
        {
            if (_weaponSwitchTimer > 0f) 
            {
                _weaponSwitchTimer -= Time.deltaTime;
            }

            var currentHold = _c.CurrentHoldItemAgent;
            bool holdingMelee = currentHold is ItemAgent_MeleeWeapon;
            bool holdingGun = currentHold is ItemAgent_Gun;

            // 滞后阈值
            float meleeEnterDist = 3.5f;
            float meleeExitDist = 6.0f;
            
            // 检查可用性
            bool hasMelee = _c.MeleeWeaponSlot()?.Content != null;
            bool hasPrim = _c.PrimWeaponSlot()?.Content != null;
            bool hasSec = _c.SecWeaponSlot()?.Content != null;

            // --- 紧急切换逻辑 ---
            // 如果主目标为空且目标接近，则立即切换到次目标
            if (holdingGun && dist < 12f && hasSec)
            {
                var gun = _c.GetGun();
                if (gun != null && gun.BulletCount <= 0 && !gun.IsReloading())
                {
                    var sec = _c.SecWeaponSlot().Content;
                    if (currentHold?.Item != sec)
                    {
                        _c.SwitchToWeapon(1); // 次要的
                        _weaponSwitchTimer = 1.5f;
                        return;
                    }
                }
            }
            
            // 1. 关键近程逻辑（若可用则为近战力量）
            if (hasMelee && dist < meleeEnterDist)
            {
                if (!holdingMelee)
                {
                    _c.SwitchToWeapon(-1);
                    _weaponSwitchTimer = MIN_WEAPON_SWITCH_INTERVAL;
                }
                
                // 攻击逻辑是在此处处理还是在EngageTarget中处理？
                // EngageTarget 调用 ChooseWeapon，然后处理移动/射击。
                // 若持有近战武器，应发起攻击。
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }
                return;
            }

            // 2. 脱离近战
            if (holdingMelee && dist < meleeExitDist)
            {
                // 如果我们在一定程度上仍然距离较近，请继续进行近战攻击
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }
                return;
            }

            // 3. 枪支逻辑方法
            if (_weaponSwitchTimer <= 0f)
            {
                // 若主数据量大于1000万或次数据量为空，则优先选择主数据
                // 若距离小于10米，则优先选择次级选项
                
                bool wantPrimary = (dist > 10f) || !hasSec;
                
                if (wantPrimary && hasPrim)
                {
                    // 检查弹药？
                     var gunItem = _c.PrimWeaponSlot().Content;
                     // 简单检查
                     if (currentHold?.Item != gunItem)
                     {
                         _c.SwitchToWeapon(0);
                         _weaponSwitchTimer = MIN_WEAPON_SWITCH_INTERVAL;
                     }
                }
                else if (hasSec)
                {
                     var gunItem = _c.SecWeaponSlot().Content;
                     if (currentHold?.Item != gunItem)
                     {
                         _c.SwitchToWeapon(1);
                         _weaponSwitchTimer = MIN_WEAPON_SWITCH_INTERVAL;
                     }
                }
                else if (hasPrim)
                {
                     // 回退到主
                     _c.SwitchToWeapon(0);
                }
                else if (hasMelee)
                {
                    // 若无枪械则转为近战
                    _c.SwitchToWeapon(-1);
                }
            }
        }

        private float GetPreferredDistance()
        {
            var currentHold = _c.CurrentHoldItemAgent;
            if (currentHold is ItemAgent_MeleeWeapon) return 1.5f;

            var gun = _c.GetGun();
            return gun != null ? Mathf.Clamp(gun.BulletDistance * 0.5f, 5f, 20f) : 5f;
        }

        private Vector3 PredictAim(CharacterMainControl player)
        {
             var gun = _c.GetGun();
            Vector3 target = player.transform.position + Vector3.up * 1.35f;
            if (gun != null)
            {
                float t = Mathf.Clamp(Vector3.Distance(_c.transform.position, target) / Mathf.Max(1f, gun.BulletSpeed), 0f, 0.5f);
                target += player.Velocity * t;
            }
            return target;
        }
        
        private void ManageSkillUse(bool hasLoS, float dist)
        {
            if (_skillCooldown > 0f) return;
             // 简化技能应用
            var inv = _c.CharacterItem?.Inventory; if (inv == null) return;
            foreach(var item in inv) {
                if(item == null) continue;
                var ss = item.GetComponent<ItemSetting_Skill>();
                if(ss != null && ss.Skill != null) {
                     _c.ChangeHoldItem(item);
                     _c.SetSkill(SkillTypes.itemSkill, ss.Skill, ss.Skill.gameObject);
                     if(_c.StartSkillAim(SkillTypes.itemSkill)) {
                         _c.ReleaseSkill(SkillTypes.itemSkill);
                         _skillCooldown = 8f;
                         return;
                     }
                }
            }
        }
        
        private void HandleDash()
        {
            if (_dashCooldown > 0f) return;
            
            // 1. 反应（伤痛）
            if (_hurtRecently)
            {
                // 侧向躲避
                var dir = _c.movementControl.MoveInput;
                if (dir.magnitude < 0.1f) dir = Random.insideUnitSphere;
                dir.y = 0;
                
                _c.movementControl.SetMoveInput(dir.normalized);
                _c.Dash();
                _dashCooldown = Random.Range(2f, 4f);
                _hurtRecently = false;
                return;
            }
            
            // 2. 主动 Dash（移动时随机规避射击）
            if (_context.HasLoS && _c.Velocity.magnitude > 2f)
            {
                if (Random.value < 0.005f) // 每帧的小概率
                {
                     _c.Dash();
                     _dashCooldown = Random.Range(3f, 6f);
                }
            }
        }
        
        private void OnHurt(DamageInfo dmg)
        {
            _hurtRecently = true;
            _pressureScore += 2f;
            _context.Pressure = _pressureScore;

            if (_store != null)
            {
                // 将此地点标记为危险区域
                _store.MarkDanger(_c.transform.position);
            }
        }
        
        private void OnDeadAI(DamageInfo dmg)
        {
            if (_neuralBrain != null)
            {
                _neuralBrain.ReportPerformance(_aliveTime, 0, 0); 
            }

            if (SquadManager.Instance != null) SquadManager.Instance.UnregisterAI(this);
            
            // 暂且假设若AI死亡则判定为玩家击杀（简化处理）
            if (AdaptiveDifficulty.Instance != null) AdaptiveDifficulty.Instance.ReportPlayerKill();

            if (_c != null)
            {
                _store.RegisterDeath(_c.transform.position);
                _c.Trigger(false, false, false);
                _c.SetRunInput(false);
                _c.movementControl.SetMoveInput(Vector3.zero);
            }
            enabled = false;
        }
        
        private Vector3 TryUnstuck(Vector3 desired)
        {
            var pos = _c.transform.position;
            float moved = (pos - _prevPos).magnitude;
            _prevPos = pos;
            
            // 如果未尝试移动，则重置卡住的计时器
            if (desired.sqrMagnitude < 0.04f)
            {
                _stuckTimer = 0f;
                _context.IsStuck = false;
                return desired;
            }
            
            // 检查我们是否实际上在尝试移动但未成功
            bool isTryingToMove = desired.magnitude > 0.1f;
            bool isMoving = moved > 0.05f; // 从 0.02f 增加以提高检测效果
            
            if (isTryingToMove && !isMoving)
            {
                _stuckTimer += Time.deltaTime;
                
                // 仅在持续无法移动的情况下标记为卡住
                if (_stuckTimer > 1.0f) // 从0.5f增加以减少假阳性
                {
                    // 附加检查以防止错误的停滞检测
                    bool isExecutingStationaryAction = false;
                    
                    // 检查当前操作是否需要保持静止姿势
                    if (_currentAction != null)
                    {
                        string actionName = _currentAction.Name;
                        isExecutingStationaryAction = actionName == "Suppression" || actionName == "Engage" || actionName == "Heal";
                    }
                    
                    // 执行静止动作时，不要标记为卡住
                    if (!isExecutingStationaryAction)
                    {
                        _context.IsStuck = true; 
                        
                        // 智能解锁方向：尝试不同角度而非仅90度
                        float randomAngle = Random.Range(45f, 135f) * (Random.value > 0.5f ? 1f : -1f);
                        return Quaternion.AngleAxis(randomAngle, Vector3.up) * desired;
                    }
                }
            }
            else
            {
                // 如果正在移动，重置卡滞状态
                _stuckTimer = 0f;
                _context.IsStuck = false;
            }
            return desired;
        }
        
        private float _doorCooldown;
        private void TryOpenDoorAhead(Vector3 desired)
        {
            _doorCooldown -= Time.deltaTime;
            if (_doorCooldown > 0f) return;
            if (desired.sqrMagnitude < 1e-4f) return;
            var pos = _c.transform.position + desired.normalized * 1.4f + Vector3.up * 0.8f;
            int n = Physics.OverlapSphereNonAlloc(pos, 1.0f, _nonAllocColliders);
            for (int i = 0; i < n; i++)
            {
                var h = _nonAllocColliders[i]; if (h == null) continue;
                var door = h.GetComponentInParent<Door>();
                if (door == null) continue;
                if (!door.IsOpen && door.NoRequireItem)
                {
                    door.Open();
                    _doorCooldown = 0.6f;
                    break;
                }
            }
        }
        
        private Vector3 ApplyFormationSeparation(Vector3 desired)
        {
            _sepCooldown -= Time.deltaTime;
            if (_sepCooldown <= 0f)
            {
                _sepBgResult = Vector3.zero;
                Vector3 selfPos = _c.transform.position;
                
                // 使用空间网格进行邻域查找
                if (_store != null && _store.Grid != null)
                {
                    _store.Grid.Query(selfPos, 5.0f, _nearbyCache);
                }
                else
                {
                    // 回退应极少发生
                    return desired;
                }
                
                int count = _nearbyCache.Count;
                
                // 若邻居数量过多则跳过（人群优化）
                if (count > 30)
                {
                     _sepCooldown = 0.5f;
                     return desired;
                }

                for (int i = 0; i < count; i++)
                {
                    var other = _nearbyCache[i];
                    if (other == null || other == _c) continue;
                    
                    Vector3 delta = selfPos - other.transform.position;
                    delta.y = 0f;
                    float dSqr = delta.sqrMagnitude;
                    
                    if (dSqr < 0.001f) continue;

                    // 基于关系的分离逻辑
                    bool isAlly = other.Team == _c.Team;
                    
                    if (isAlly)
                    {
                        const float separationRadius = 2.5f;
                        const float separationRadiusSqr = separationRadius * separationRadius;
                        
                        if (dSqr < separationRadiusSqr)
                        {
                            float d = Mathf.Sqrt(dSqr);
                            float force = (separationRadius - d) / separationRadius;
                            _sepBgResult += delta.normalized * force;
                        }
                    }
                    else
                    {
                        // 略微避免敌人
                        const float avoidRadius = 4.0f;
                        const float avoidRadiusSqr = avoidRadius * avoidRadius;
                        
                        if (dSqr < avoidRadiusSqr)
                        {
                             float d = Mathf.Sqrt(dSqr);
                             float force = ((avoidRadius - d) / avoidRadius) * 1.5f;
                             _sepBgResult += delta.normalized * force;
                        }
                    }
                }
                
                // 简化避障（仅检测前方和侧面）
                Vector3 wallAvoidance = Vector3.zero;
                int wallCheckCount = 3; // 从5减少到3
                for (int i = 0; i < wallCheckCount; i++)
                {
                    float angle = (i - 1) * 45f; // -45度, 0度, 45度
                    Vector3 checkDir = Quaternion.AngleAxis(angle, Vector3.up) * _c.transform.forward;
                    
                    float checkDistance = 2.0f;
                    RaycastHit hit;
                    if (Physics.Raycast(
                        _c.transform.position + Vector3.up * 1.0f,
                        checkDir,
                        out hit,
                        checkDistance,
                        GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer))
                    {
                        float force = Mathf.Lerp(0f, 1f, (checkDistance - hit.distance) / checkDistance);
                        wallAvoidance += checkDir.normalized * force;
                    }
                }
                
                _sepBgResult += wallAvoidance;
                
                // 限制最大分离力以防止出现异常行为
                float maxSeparationForce = 0.8f;
                if (_sepBgResult.magnitude > maxSeparationForce)
                {
                    _sepBgResult = _sepBgResult.normalized * maxSeparationForce;
                }
                
                // 简化分离权重计算
                float separationWeight = 0.7f; // 固定权重以提升性能
                _sepBgResult *= separationWeight;
                
                _sepCooldown = 0.2f; // 增加冷却时间以提升性能
            }
            
            // 将分离力与期望运动相结合
            Vector3 finalMove = desired + _sepBgResult;
            
            // 简化方向融合
            if (desired.magnitude > 0.1f)
            {
                float dot = Vector3.Dot(desired.normalized, finalMove.normalized);
                if (dot < 0.2f) // 如果方向差异过大
                {
                    // 简单的混合向原始方向回溯
                    finalMove = Vector3.Lerp(finalMove, desired, 0.6f);
                }
            }
            
            return finalMove.normalized;
        }
        
        private void OnDestroy()
        {
            if (SquadManager.Instance != null) SquadManager.Instance.UnregisterAI(this);
            _all.Remove(this);
        }
    }
}
