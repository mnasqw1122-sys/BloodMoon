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
        // --- Components ---
        public AIRole Role; // The role assigned to this AI
        private CharacterMainControl _c = null!;
        private AIDataStore _store = null!;
        private Seeker? _seeker;
        private Path? _path;
        
        // --- Decision System ---
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
        
        // --- Movement & Pathfinding ---
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
        
        // --- Combat State ---
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
        
        // --- Healing State ---
        private float _healWaitTimer;
        public bool IsHealing => _healWaitTimer > 0f;

        // --- Coordination ---
        private CharacterMainControl? _leader;
        private int _wingIndex = -1;
        public bool IsBoss;

        public CharacterMainControl? CurrentTarget => _context.Target; // Exposed for SquadManager

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
                // Update Squad Context
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

        // --- Search & Patrol ---
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
            
            // Initialize Context
            _context.Character = _c;
            _context.Controller = this;
            _context.Store = _store;
            _context.Personality = AIPersonality.GenerateRandom(); // Assign random personality
            
            // Determine Role based on Personality
            if (_context.Personality.Aggression > 0.7f && _context.Personality.Caution < 0.4f) Role = AIRole.Assault;
            else if (_context.Personality.Teamwork > 0.7f) Role = AIRole.Support;
            else if (_context.Personality.Caution > 0.7f) Role = AIRole.Sniper;
            else Role = AIRole.Standard;

            // Debug Log Personality
            #if DEBUG
            Utils.Logger.Debug($"[AI {_c.name}] Created with Personality: {_context.Personality} | Role: {Role}");
            #endif
            
            // Initialize Actions
            _actions.Add(new Action_Unstuck());
            _actions.Add(new Action_Heal());
            _actions.Add(new Action_BossCommand()); // New Boss Action
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

            // Initialize Neural Brain
            var actionNames = _actions.Select(a => a.Name).ToList();
            _neuralBrain = new ContextAwareDecisionMaker(actionNames);

            // Initialize Behavior System
            _behaviorSystem = new StableBehaviorSystem();
            _behaviorSystem.Initialize();

            // Find Weapons (Async)
            FindWeaponsAsync().Forget();

            var preset = c.characterPreset;
            if (preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon)
            {
                IsBoss = true;
            }
            
            // Randomize tick offset to distribute CPU load
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
            
            // Safeguard: Ensure vanilla AI stays disabled
            var vanilla = _c.GetComponent<AICharacterController>();
            if (vanilla != null && vanilla.enabled) vanilla.enabled = false;

            _aliveTime += Time.deltaTime;
            
            // Global Updates (Timers need smooth updates)
            UpdateTimers();

            // Throttle Decision Making
            _aiTickTimer += Time.deltaTime;
            if (_aiTickTimer >= AI_TICK_INTERVAL)
            {
                _aiTickTimer = 0f;
                _context.UpdateSensors();
                
                // Update action time for current action
                if (_currentAction != null)
                {
                    _currentAction.UpdateActionTime(AI_TICK_INTERVAL);
                }
                
                // Cooldowns (Update based on interval)
                foreach (var a in _actions) a.UpdateCooldown(AI_TICK_INTERVAL);
                
                // Update switching cooldowns
                var keys = new List<string>(_actionSwitchCooldowns.Keys);
                foreach(var k in keys) 
                {
                    _actionSwitchCooldowns[k] -= AI_TICK_INTERVAL;
                    if (_actionSwitchCooldowns[k] <= 0) _actionSwitchCooldowns.Remove(k);
                }

                if (_globalCooldown <= 0f)
                {
                    // Decision Logic with behavior persistence
                    AIAction? bestAction = null;
                    float bestScore = -1f;
                    
                    // Get Neural Scores (Experimental)
                    var neuralScores = _neuralBrain.GetActionScores(_context);

                    Dictionary<string, float> allScores = new Dictionary<string, float>();

                    foreach (var action in _actions)
                    {
                        // Check switch cooldown
                        if (_actionSwitchCooldowns.ContainsKey(action.Name)) 
                        {
                            allScores[action.Name] = -1f;
                            continue;
                        }

                        float score = action.Evaluate(_context);
                        
                        // Hybrid Decision: Rule Base (90%) + Neural Net (10%)
                        if (neuralScores.TryGetValue(action.Name, out float nScore))
                        {
                            // Dynamic Weight: Increase neural influence over time (up to 40% after 5 mins)
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
                    
                    // Use StableBehaviorSystem
                    string proposedActionName = bestAction?.Name ?? (_currentAction?.Name ?? "Patrol");
                    
                    List<string> availableActions = _actions.Select(a => a.Name).ToList();
                    string stableActionName = _behaviorSystem.GetStableAction(proposedActionName, bestScore, _context, availableActions, allScores);
                    
                    AIAction? stableAction = _actions.FirstOrDefault(a => a.Name == stableActionName);
                    if (stableAction == null) stableAction = bestAction;
                    
                    bool shouldSwitchAction = false;
                    
                    if (stableAction != null && stableAction != _currentAction)
                    {
                        // Check if current action can be interrupted
                        if (_currentAction == null || _currentAction.CanBeInterrupted())
                        {
                            shouldSwitchAction = true;
                        }
                    }
                    
                    // Check if current action should exit
                    if (_currentAction != null && _currentAction.ShouldExit())
                    {
                        shouldSwitchAction = true;
                        // If forced to exit, pick the best raw action if stable action is still the current one
                        if (stableAction == _currentAction) stableAction = bestAction;
                    }
                    
                    if (shouldSwitchAction && stableAction != null && stableAction != _currentAction)
                    {
                        if (_currentAction != null) 
                        {
                            _currentAction.OnExit(_context);
                            // Set cooldown for the OLD action so we don't switch back immediately
                            _actionSwitchCooldowns[_currentAction.Name] = 2.0f;
                        }
                        
                        // Set Global Cooldown
                        _globalCooldown = 0.5f;

                        // Debug Log for Action Switch
                        #if DEBUG
                        BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Switch Action: {_currentAction?.Name ?? "None"} -> {stableAction.Name}");
                        #endif

                        _currentAction = stableAction;
                        _currentAction.OnEnter(_context);
                    }
                }
            }
            
            // Execute Action (Every Frame for smooth movement/aiming)
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
        
        // --- Public Action Methods ---

        public bool PerformBossCommand()
        {
            if (_bossCommandCooldown > 0f) return false;
            
            // Buff nearby minions using Grid
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
            
            // Visual/Audio feedback could go here
            // _c.PlaySound("Roar"); 
            
            _bossCommandCooldown = 20f; // Cooldown
            return count > 0;
        }

        public void ReceiveBuff()
        {
            // Morale boost
            _pressureScore = 0f;
            _canChase = true;
            _chaseDelayTimer = 0f;
            // Maybe force a rush?
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
             // Check held item first
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
             
             // Smart Check: Don't throw if target is too close (< 8m) or too far (> 25m)
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
             // Run away from target
             var dir = (_c.transform.position - _context.Target.transform.position).normalized;
             // Try to find a point far away
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
                  // Fire at last known position even without LoS
                  _c.SetAimPoint(_context.LastKnownPos + Vector3.up * 1.0f);
                  _c.Trigger(true, true, false);
             }
        }

        public void PerformHeal()
        {
            // Logic:
            // 1. If we have a healing item in hand, use it.
            // 2. If we are NOT safe (HasLoS), we MUST prioritize finding cover before switching/using.
            // 3. If we are safe OR we can't find cover, then switch and use.
            // 4. Distinguish between Health Recovery (Medkits) and Buffs (Stimulants/Painkillers)
            
            bool isSafe = !_context.HasLoS;
            
            // Try to find cover if exposed
            // Bosses might ignore this if they are just topping up, but generally hiding is good
            if (!isSafe && _coverCooldown <= 0f)
            {
                 // Sprint to cover!
                 if (MoveToCover()) 
                 {
                     _c.SetRunInput(true); // Run to cover!
                     
                     // Defensive Fire: Shoot back while running if possible!
                     if (_context.Target != null && _context.HasLoS)
                     {
                         _c.SetAimPoint(_context.Target.transform.position + Vector3.up * 1.2f);
                         _c.Trigger(true, true, false);
                     }
                     
                     return; // Don't heal yet, just run
                 }
                 else
                 {
                     // Failed to find cover AND we are exposed.
                     // Do NOT stop to heal in the open if enemy is looking at us.
                     if (_context.HasLoS)
                     {
                         // Abort healing attempt to allow Engage/Panic to take over next frame
                         return; 
                     }
                 }
            }
            
            // Stop to heal if we are safe or gave up on cover
            _c.SetRunInput(false);
            _c.movementControl.SetMoveInput(Vector3.zero);
            
            // Current Health Status
            float hpPercent = _c.Health.CurrentHealth / _c.Health.MaxHealth;
            bool needHealth = hpPercent < 0.8f;
            // Simplified check: assume we need buffs if healthy but in combat, or if we have specific debuffs (not easily checkable here without deep integration)
            // For now, let's say we want buffs if HP > 50% but we are in "Pressure" state
            bool wantBuff = hpPercent > 0.4f && _pressureScore > 5f; 

            // 1. Check Held Item
            if (_c.CurrentHoldItemAgent != null)
            {
                var item = _c.CurrentHoldItemAgent.Item;
                bool isDrug = item.GetComponent<Drug>() != null;
                bool isFood = item.GetComponent<FoodDrink>() != null;
                bool isBuff = item.GetComponent<Duckov.ItemUsage.AddBuff>() != null; // Explicit namespace to be safe

                // Decision: Use if it matches our need
                bool shouldUse = false;
                if (needHealth && (isDrug || isFood)) shouldUse = true;
                if (wantBuff && isBuff) shouldUse = true;
                
                // Fallback: If holding a medkit and desperate, use it even if just for small heal
                if (hpPercent < 0.95f && (isDrug || isFood)) shouldUse = true;

                if (shouldUse)
                {
                    bool reloading = _c.GetGun()?.IsReloading() ?? false;
                    if (!reloading && _healWaitTimer <= 0f) 
                    {
                        _c.UseItem(item);
                        _healWaitTimer = 4.0f; // Assume ~4s for healing animation
                    }
                    return;
                }
            }

            // 2. Find Best Item for Needs
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
                
                // Prioritize Healing if hurt
                if (needHealth)
                {
                    if (drug != null) score += (int)drug.healValue + item.Quality * 10;
                    if (food != null) score += 5 + item.Quality * 5; // Food is lower prio than meds
                }
                
                // Prioritize Buffs if under pressure and not dying
                if (wantBuff)
                {
                    if (buff != null) score += 50 + item.Quality * 10;
                }
                
                // If critical (<30%), prioritize BIG heals above all
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
                // Switch
                if (_c.CurrentHoldItemAgent?.Item != bestItem)
                {
                    _c.ChangeHoldItem(bestItem);
                    _healWaitTimer = 0.5f; // Wait for equip
                }
            }
        }

        public void PerformReload()
        {
            var gun = _c.GetGun();
            if (gun != null && !gun.IsReloading())
            {
                // Logic for smart reload (best ammo)
                // Simplified for now, just reload
                gun.BeginReload();
            }
            
            // Move to cover while reloading
            if (_context.HasLoS)
            {
                // 1. Dash to cover if possible and available
                if (_dashCooldown <= 0f)
                {
                     // Dash sideways or back
                     var dir = -_context.DirToTarget + Random.insideUnitSphere * 0.5f;
                     dir.y = 0;
                     _c.movementControl.SetMoveInput(dir.normalized);
                     _c.Dash();
                     _dashCooldown = 3f;
                }

                // 2. If exposed, try to move to cover
                if (MoveToCover()) return;
                
                // 3. If no cover found, erratic movement (strafe)
                // Reuse EngageTarget strafe logic partially or just random move
                 _strafeTimer -= Time.deltaTime;
                if (_strafeTimer <= 0f)
                {
                    _strafeDir = Random.value > 0.5f ? 1 : -1;
                    _strafeTimer = 0.3f; // Fast switching
                }
                
                if (_context.Target != null)
                {
                     var dir = (_context.Target.transform.position - _c.transform.position).normalized;
                     var perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
                     // Move back and sideways
                     var move = (-dir * 0.5f + perp).normalized;
                     _c.movementControl.SetMoveInput(move);
                     _c.SetRunInput(true); // Run while reloading if possible
                }
            }
            else
            {
                // Strafe slightly or stand still
                 _c.movementControl.SetMoveInput(Vector3.zero);
            }
        }

        public bool MoveToCover()
        {
            if (_targetCoverPos == Vector3.zero || Vector3.Distance(_c.transform.position, _targetCoverPos) < 1.0f)
            {
                // Find new cover
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

            // Weapon Type Logic
            var currentHold = _c.CurrentHoldItemAgent;
            bool isMelee = currentHold is ItemAgent_MeleeWeapon;

            var gun = _c.GetGun();
            bool isSniper = gun != null && gun.BulletDistance > 80f; // Heuristic
            bool isShotgun = gun != null && gun.BulletCount < 10 && gun.BulletDistance < 20f; // Heuristic
            
            Vector3 aimPos = PredictAim(_context.Target);
            
            // Apply Difficulty-based Inaccuracy
            if (AdaptiveDifficulty.Instance != null)
            {
                float accMult = AdaptiveDifficulty.Instance.AccuracyMultiplier;
                // Add noise based on difficulty (1.0 = normal, 0.8 = accurate, 1.2 = inaccurate)
                // If multiplier < 1 (harder), noise is reduced.
                // Base noise radius e.g. 0.5m
                float noise = 0.5f * accMult;
                aimPos += Random.insideUnitSphere * noise;
            }
            
            _c.SetAimPoint(aimPos);
            
            // Movement during combat (Strafing)
            Vector3 dir = _context.DirToTarget;

            if (isMelee)
            {
                // Direct approach for melee
                _c.movementControl.SetMoveInput(dir);
                _c.SetRunInput(true);

                // Attack if in range
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }

                // Random dash to close gap or dodge
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
                // Randomize duration based on weapon
                if (isSniper) _strafeTimer = Random.Range(2.0f, 4.0f); // Move less often
                else if (isShotgun) _strafeTimer = Random.Range(0.3f, 0.8f); // Jittery
                else _strafeTimer = Random.Range(0.5f, 1.5f);
            }
            
            var perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
            
            // Wall check for strafe
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
                // Aggressive push
                move = dir * 0.8f + perp * 0.4f; 
                // Dash forward if far
                if (dist > 8f && _dashCooldown <= 0f) 
                {
                    _c.movementControl.SetMoveInput(dir);
                    _c.Dash();
                    _dashCooldown = 2.5f;
                }
            }
            else if (isSniper)
            {
                // Keep distance, steady aim
                if (dist < pref - 5f) move = -dir; // Retreat
                else if (_shootTimer > 0f) move = perp * 0.3f; // Slow strafe while cooling down
                else move = Vector3.zero; // Stop to shoot
            }
            else // Rifle/Standard
            {
                 // Dynamic strafing
                 move = (perp * 0.8f + dir * 0.2f).normalized;
                 if (dist < pref - 3f) move = -dir + perp * 0.5f;
                 else if (dist > pref + 3f) move = dir + perp * 0.2f;

                 // Random roll during combat
                 if (_dashCooldown <= 0f && Random.value < 0.01f) // 1% chance per frame
                 {
                     _c.movementControl.SetMoveInput(move);
                     _c.Dash();
                     _dashCooldown = Random.Range(3f, 6f);
                 }
            }
            
            _c.movementControl.SetMoveInput(move.normalized);
            
            // Tactical Stance: ADS if shooting and not moving fast
            bool shouldAds = (dist > 15f && !isShotgun) || (isSniper);
            _c.SetAdsInput(shouldAds);
            _c.SetRunInput(!shouldAds && move.magnitude > 0.1f);
            
            // Shooting
            if (_shootTimer <= 0f)
            {
                bool fire = true;
                // For sniper, ensure we are steady?
                if (isSniper && _c.Velocity.magnitude > 1f) fire = false;

                if (fire)
                {
                    _c.Trigger(true, true, false);
                    // Burst logic
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
            
            // Look where we are going
            if (move.sqrMagnitude > 0.1f)
            {
                _c.SetAimPoint(_c.transform.position + move * 10f);
            }
        }
        
        public void PerformTacticalFlank()
        {
            if (_context.Target == null) return;
            
            // Try to find a position that is:
            // 1. Within range (15-25m)
            // 2. Has LoS to target (optional, but good for final approach)
            // 3. Is at an angle > 60 degrees relative to target's forward (Flanking)
            
            var targetForward = _context.Target.transform.forward;
            var targetPos = _context.Target.transform.position;
            
            // Try left and right flanks
            Vector3 bestPos = Vector3.zero;
            float bestScore = -1f;
            
            // Optimized: Fewer iterations, wider spread
            for (int i = 0; i < 6; i++)
            {
                // Sample semi-circle behind/side of target
                // Angle logic: 90 is right, -90 is left. We want to avoid 0 (front) and 180 (back is okay but maybe too far)
                // Let's aim for 45-135 degrees relative to player facing
                
                float side = (i % 2 == 0) ? 1f : -1f;
                float angle = UnityEngine.Random.Range(45f, 135f) * side;
                
                var rot = Quaternion.AngleAxis(angle, Vector3.up);
                var dir = rot * targetForward; // Relative to player facing
                var cand = targetPos + dir * UnityEngine.Random.Range(12f, 22f);
                
                // Validate ground
                if (Physics.Raycast(cand + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                {
                    cand = hit.point;
                    
                    // Score: Distance from us (closer is better to minimize travel time) + Flank Angle - Heat
                    float dist = Vector3.Distance(_c.transform.position, cand);
                    float heat = _store.GetHeatAt(cand, Time.time, 5f);
                    
                    // Prefer positions that provide cover (Raycast to target blocked?)
                    // For flanking, we actually WANT LoS at the END, but travel path should be safe.
                    // But 'cand' is the destination. We want a destination with Cover OR good LoS.
                    
                    // Simple Score: 
                    float score = 100f - dist - (heat * 15f);
                    
                    // Angle Bonus: Closer to 90 degrees is better
                    float angleDiff = Mathf.Abs(90f - Mathf.Abs(angle));
                    score += (45f - angleDiff);

                    if (score > bestScore)
                    {
                        // Check if reachable (navmesh/raycast check roughly)
                         var origin = _c.transform.position + Vector3.up;
                         var toCand = cand - _c.transform.position;
                         // Simple wall check
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
                // Fallback to simple move
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

            // Pick a new point if we don't have one or are close
            if (_patrolTarget == Vector3.zero || Vector3.Distance(_c.transform.position, _patrolTarget) < 3f)
            {
                // Find a random point on the map
                // Try 5 times
                bool found = false;
                for(int i=0; i<5; i++)
                {
                    // Random direction and distance (30m - 80m) for wide roaming
                    var rnd = Random.insideUnitCircle.normalized * Random.Range(30f, 80f);
                    var candidate = _c.transform.position + new Vector3(rnd.x, 0, rnd.y);
                    
                    if (Physics.Raycast(candidate + Vector3.up * 10f, Vector3.down, out var hit, 20f, GameplayDataSettings.Layers.groundLayerMask))
                    {
                        // Check if reachable (simple check)
                        _patrolTarget = hit.point;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                     // Small move if failed
                     _patrolTarget = _c.transform.position + _c.transform.forward * 10f;
                }
                
                // Pause before moving to new point
                _patrolWaitTimer = Random.Range(2f, 5f);
                return;
            }
            
            MoveTo(_patrolTarget);
            _c.SetRunInput(false); // Walk during patrol
        }

        public void PerformSearch()
        {
            if (_searchTimer <= 0f)
            {
                // Intelligent Search
                Vector3 searchCenter = _context.LastKnownPos;
                
                // If context LastKnownPos is very old or zero, use Global Store
                if (_store != null && (Time.time - _context.LastSeenTime > 15f || searchCenter == Vector3.zero))
                {
                     if (_store.LastKnownPlayerPos != Vector3.zero)
                        searchCenter = _store.LastKnownPlayerPos;
                }
                
                // If still zero, search random nearby
                if (searchCenter == Vector3.zero) searchCenter = _c.transform.position;

                bool found = false;
                
                // Try 5 times to find a cover spot near search center
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
            
            // Sweep look
            float angle = Mathf.Sin(Time.time * 2f) * 60f;
            var lookDir = Quaternion.AngleAxis(angle, Vector3.up) * _c.transform.forward;
            _c.SetAimPoint(_c.transform.position + lookDir * 10f);
        }
        public void PerformRush()
        {
            if (_context.Target == null) return;
            // Move directly to target
            MoveTo(_context.Target.transform.position);
            _c.SetRunInput(true);
            
            // Shoot while running if possible (hip fire?)
            _c.SetAimPoint(_context.Target.transform.position + Vector3.up * 1.2f);
            
            // Force trigger if we have ammo and line of sight roughly
            if (_context.HasLoS)
            {
                 _c.Trigger(true, true, false);
            }
        }

        public void PerformUnstuck()
        {
            // Aggressive unstuck
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
            _context.IsStuck = false; // Reset flag
        }

        public void PerformPanic()
        {
            // Run away from danger (heat) or target
            Vector3 runDir = Vector3.zero;
            if (_context.Target != null)
            {
                runDir = (_c.transform.position - _context.Target.transform.position).normalized;
            }
            else
            {
                runDir = Random.insideUnitSphere; runDir.y = 0;
            }
            
            // Add some noise
            runDir += Random.insideUnitSphere * 0.5f;
            runDir.Normalize();
            
            MoveTo(_c.transform.position + runDir * 10f);
            _c.SetRunInput(true);
            
            // Random shooting
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

        // --- Helper Methods (Reused & Adapted) ---
        
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

            // Door / Stuck Check Logic (Optimized frequency)
            if (Time.time - _stuckCheckInterval > 3.0f) // Increased from 2.0f to reduce checks
            {
                _stuckCheckInterval = Time.time;
                if (Vector3.Distance(_c.transform.position, _lastDoorCheckPos) < 0.5f)
                {
                    _doorStuckTimer += 3.0f; // Increased from 2.0f
                }
                else
                {
                    _doorStuckTimer = 0f;
                    _lastDoorCheckPos = _c.transform.position;
                }

                if (_doorStuckTimer > 9.0f) // Increased from 6.0f
                {
                    _context.IsStuck = true; // Signal Unstuck Action
                    _doorStuckTimer = 0f;
                    _repathTimer = -1f; 
                    _waitingForPath = false; 
                    return (_c.transform.position - targetPos).normalized;
                }
            }

            // Optimized target validation with distance thresholds
            float distToTarget = Vector3.Distance(_c.transform.position, targetPos);
            
            // Tiered distance logic for performance optimization
            if (distToTarget > 150f) // Very far - use simple movement
            {
                // Target is very far, use simpler movement without pathfinding
                return (targetPos - _c.transform.position).normalized;
            }
            else if (distToTarget < 3f) // Very close - no need for pathfinding
            {
                // Target is very close, move directly
                return (targetPos - _c.transform.position).normalized;
            }

            _repathTimer -= Time.deltaTime;
            bool shouldRepath = false;
            
            // Optimized pathfinding frequency with multiple checks
            if (_path == null || _path.vectorPath == null || _path.vectorPath.Count == 0 || _path.error)
            {
                shouldRepath = true;
                BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Path invalid, requesting repath");
            }
            else if (_repathTimer <= 0f)
            {
                // Only repath if target has moved significantly
                float targetMoveDist = Vector3.Distance(_lastPathTarget, targetPos);
                if (targetMoveDist > 5f) // Increased from 3f to reduce unnecessary repaths
                {
                    shouldRepath = true;
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Target moved {targetMoveDist:F1}m, requesting repath");
                }
                else
                {
                    // Extend repath timer since target hasn't moved much
                    _repathTimer = 1.0f;
                }
            }
            // Simplified path validity check with larger tolerance
            else if (_path.vectorPath.Count > 0 && Vector3.Distance(_c.transform.position, _path.vectorPath[0]) > 20f) // Increased from 15f
            {
                shouldRepath = true;
                BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Far from path start ({Vector3.Distance(_c.transform.position, _path.vectorPath[0]):F1}m), requesting repath");
            }

            if (shouldRepath && !_waitingForPath)
            {
                // Performance optimization: skip pathfinding if too many AI are already pathfinding
                if (BloodMoon.AI.RuntimeMonitor.Instance != null && 
                    BloodMoon.AI.RuntimeMonitor.Instance.ConcurrentPathfindingCount > 5)
                {
                    BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Too many concurrent pathfinding ({BloodMoon.AI.RuntimeMonitor.Instance.ConcurrentPathfindingCount}), using fallback");
                    return (targetPos - _c.transform.position).normalized;
                }
                
                _lastPathTarget = targetPos;
                _repathTimer = 3.0f; // Increased from 2.0f to further reduce pathfinding load
                _waitingForPath = true;
                
                // Start pathfinding with performance monitoring
                BloodMoon.AI.RuntimeMonitor.Instance?.IncrementPathfindingCount();
                _seeker.StartPath(_c.transform.position, targetPos, OnPathComplete);
                
                BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Starting path to target {distToTarget:F1}m away");
            }

            if (_path == null || _path.vectorPath == null || _path.vectorPath.Count == 0 || _path.error)
            {
                // Enhanced fallback logic when pathfinding fails
                _wallCheckTimer -= Time.deltaTime;
                if (_wallCheckTimer <= 0f)
                {
                    _wallCheckTimer = 0.1f;
                    var dir = (targetPos - _c.transform.position).normalized;
                    _cachedAvoidanceDir = dir;
                    
                    // Multiple raycasts for better obstacle detection
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
                            // No obstacle in this direction, use it
                            _cachedAvoidanceDir = rayDir;
                            break;
                        }
                    }
                }
                
                // Add a small randomness to fallback direction to prevent infinite loops
                Vector3 randomOffset = new Vector3(Random.insideUnitCircle.x, 0f, Random.insideUnitCircle.y) * 0.1f;
                Vector3 finalDir = _cachedAvoidanceDir + randomOffset;
                finalDir.y = 0;
                return finalDir.normalized;
            }

            // Follow Path with improved waypoint handling
            float nextWaypointDistance = 3f;
            bool reachedEnd = false;
            float dist;
            
            // Ensure we're not stuck on an invalid waypoint
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

            // Prevent currentWaypoint from going out of bounds
            _currentWaypoint = Mathf.Clamp(_currentWaypoint, 0, _path.vectorPath.Count - 1);

            if (reachedEnd || _currentWaypoint >= _path.vectorPath.Count)
            {
                if (Vector3.Distance(_c.transform.position, targetPos) > 1.5f)
                {
                    // Check if we're actually close to the target, if not, repath
                    _repathTimer = 0f; // Force repath next cycle
                    return (targetPos - _c.transform.position).normalized;
                }
                return Vector3.zero;
            }

            // Get direction to next waypoint (with safety check)
            if (_currentWaypoint < 0 || _currentWaypoint >= _path.vectorPath.Count)
            {
                BloodMoon.Utils.Logger.Warning($"[AI {_c.name}] Invalid waypoint index: {_currentWaypoint}, path count: {_path.vectorPath.Count}");
                return Vector3.zero;
            }
            
            Vector3 dirToWaypoint = (_path.vectorPath[_currentWaypoint] - _c.transform.position).normalized;
            
            // Check if waypoint is reachable (simple raycast check)
            var origin = _c.transform.position + Vector3.up * 1.0f;
            var waypointPos = _path.vectorPath[_currentWaypoint] + Vector3.up * 1.0f;
            var waypointDir = (waypointPos - origin).normalized;
            var waypointDist = Vector3.Distance(origin, waypointPos);
            
            // If waypoint is blocked, try to find a better direction
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            if (Physics.Raycast(origin, waypointDir, waypointDist, mask))
            {
                // Waypoint is blocked, try to slide around
                var slide = Vector3.ProjectOnPlane(dirToWaypoint, Vector3.up).normalized;
                if (Vector3.Dot(slide, dirToWaypoint) > 0.1f) dirToWaypoint = slide;
            }

            return dirToWaypoint;
        }
        
        // Check if target position is valid for pathfinding
        private bool IsValidTargetPosition(Vector3 pos)
        {
            // Check if position is on ground
            if (!Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
            {
                return false;
            }
            
            // Check if position is too far from current position
            if (Vector3.Distance(_c.transform.position, pos) > 100f)
            {
                return false;
            }
            
            // Check if position is accessible (simple raycast check from above)
            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
            if (Physics.Raycast(pos + Vector3.up * 5f, Vector3.down, 10f, mask))
            {
                return false;
            }
            
            return true;
        }
        
        // Find a nearby valid position when target is invalid
        private Vector3 FindNearbyValidPosition(Vector3 originalPos)
        {
            // Try to find a valid position in a spiral pattern around the original
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
            
            // Fallback to current position if no valid nearby position found
            return _c.transform.position + (_c.transform.forward * 5f);
        }

        private void OnPathComplete(Path p)
        {
            // Decrement concurrent pathfinding count
            BloodMoon.AI.RuntimeMonitor.Instance?.DecrementPathfindingCount();
            
            if (!p.error)
            {
                _path = p;
                _currentWaypoint = 0;
                BloodMoon.Utils.Logger.Debug($"[AI {_c.name}] Path completed successfully, waypoints: {p.vectorPath?.Count ?? 0}");
            }
            else
            {
                BloodMoon.Utils.Logger.Warning($"[AI {_c.name}] Path failed: {p.errorLog}");
            }
            _waitingForPath = false;
        }
        
        private bool FindCover(CharacterMainControl player, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            if (player == null) return false;
            
            // Try store known cover first
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
                    
                    // Evaluate heat
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

            // Hysteresis thresholds
            float meleeEnterDist = 3.5f;
            float meleeExitDist = 6.0f;
            
            // Check availability
            bool hasMelee = _c.MeleeWeaponSlot()?.Content != null;
            bool hasPrim = _c.PrimWeaponSlot()?.Content != null;
            bool hasSec = _c.SecWeaponSlot()?.Content != null;

            // --- Emergency Switch Logic ---
            // If primary is empty and target is close, switch to secondary instantly
            if (holdingGun && dist < 12f && hasSec)
            {
                var gun = _c.GetGun();
                if (gun != null && gun.BulletCount <= 0 && !gun.IsReloading())
                {
                    var sec = _c.SecWeaponSlot().Content;
                    if (currentHold?.Item != sec)
                    {
                        _c.SwitchToWeapon(1); // Secondary
                        _weaponSwitchTimer = 1.5f;
                        return;
                    }
                }
            }
            
            // 1. Critical Close Range Logic (Force Melee if available)
            if (hasMelee && dist < meleeEnterDist)
            {
                if (!holdingMelee)
                {
                    _c.SwitchToWeapon(-1);
                    _weaponSwitchTimer = MIN_WEAPON_SWITCH_INTERVAL;
                }
                
                // Attack logic is handled here or in EngageTarget? 
                // EngageTarget calls ChooseWeapon then handles movement/shooting.
                // If holding melee, we should attack.
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }
                return;
            }

            // 2. Transition out of Melee
            if (holdingMelee && dist < meleeExitDist)
            {
                // Keep holding melee if we are still somewhat close
                var melee = _c.GetMeleeWeapon();
                if (melee != null && melee.AttackableTargetInRange())
                {
                    _c.Attack();
                }
                return;
            }

            // 3. Gun Logic
            if (_weaponSwitchTimer <= 0f)
            {
                // Prefer Primary if > 10m or Secondary empty
                // Prefer Secondary if < 10m
                
                bool wantPrimary = (dist > 10f) || !hasSec;
                
                if (wantPrimary && hasPrim)
                {
                    // Check ammo?
                     var gunItem = _c.PrimWeaponSlot().Content;
                     // simple check
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
                     // Fallback to primary
                     _c.SwitchToWeapon(0);
                }
                else if (hasMelee)
                {
                    // Fallback to melee if no guns
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
             // Simplified skill usage
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
            
            // 1. Reactive Dash (Hurt)
            if (_hurtRecently)
            {
                // Dodge sideways
                var dir = _c.movementControl.MoveInput;
                if (dir.magnitude < 0.1f) dir = Random.insideUnitSphere;
                dir.y = 0;
                
                _c.movementControl.SetMoveInput(dir.normalized);
                _c.Dash();
                _dashCooldown = Random.Range(2f, 4f);
                _hurtRecently = false;
                return;
            }
            
            // 2. Proactive Dash (Randomly while moving to avoid shots)
            if (_context.HasLoS && _c.Velocity.magnitude > 2f)
            {
                if (Random.value < 0.005f) // Small chance per frame
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
                // Mark this spot as dangerous
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
            
            // Assume player kill if AI dies for now (simplification)
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
            
            // Reset stuck timer if we're not trying to move
            if (desired.sqrMagnitude < 0.04f)
            {
                _stuckTimer = 0f;
                _context.IsStuck = false;
                return desired;
            }
            
            // Check if we're actually trying to move but not succeeding
            bool isTryingToMove = desired.magnitude > 0.1f;
            bool isMoving = moved > 0.05f; // Increased from 0.02f for better detection
            
            if (isTryingToMove && !isMoving)
            {
                _stuckTimer += Time.deltaTime;
                
                // Only mark as stuck after persistent failure to move
                if (_stuckTimer > 1.0f) // Increased from 0.5f to reduce false positives
                {
                    // Additional checks to prevent false stuck detection
                    bool isExecutingStationaryAction = false;
                    
                    // Check if current action is something that requires stationary position
                    if (_currentAction != null)
                    {
                        string actionName = _currentAction.Name;
                        isExecutingStationaryAction = actionName == "Suppression" || actionName == "Engage" || actionName == "Heal";
                    }
                    
                    // Don't mark as stuck if executing stationary actions
                    if (!isExecutingStationaryAction)
                    {
                        _context.IsStuck = true; 
                        
                        // Smart unlock direction: try different angles instead of just 90 degrees
                        float randomAngle = Random.Range(45f, 135f) * (Random.value > 0.5f ? 1f : -1f);
                        return Quaternion.AngleAxis(randomAngle, Vector3.up) * desired;
                    }
                }
            }
            else
            {
                // Reset stuck state if we're moving
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
                
                // Use Spatial Grid for neighbor lookup
                if (_store != null && _store.Grid != null)
                {
                    _store.Grid.Query(selfPos, 5.0f, _nearbyCache);
                }
                else
                {
                    // Fallback should rarely happen
                    return desired;
                }
                
                int count = _nearbyCache.Count;
                
                // Skip if too many neighbors (crowd optimization)
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

                    // Separation logic based on relationship
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
                        // Avoid enemies slightly
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
                
                // Simplified Wall Avoidance (only check front and sides)
                Vector3 wallAvoidance = Vector3.zero;
                int wallCheckCount = 3; // Reduced from 5 to 3
                for (int i = 0; i < wallCheckCount; i++)
                {
                    float angle = (i - 1) * 45f; // -45, 0, 45 degrees
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
                
                // Limit maximum separation force to prevent erratic behavior
                float maxSeparationForce = 0.8f;
                if (_sepBgResult.magnitude > maxSeparationForce)
                {
                    _sepBgResult = _sepBgResult.normalized * maxSeparationForce;
                }
                
                // Simplified separation weight calculation
                float separationWeight = 0.7f; // Fixed weight for better performance
                _sepBgResult *= separationWeight;
                
                _sepCooldown = 0.2f; // Increased cooldown for better performance
            }
            
            // Combine separation force with desired movement
            Vector3 finalMove = desired + _sepBgResult;
            
            // Simplified direction blending
            if (desired.magnitude > 0.1f)
            {
                float dot = Vector3.Dot(desired.normalized, finalMove.normalized);
                if (dot < 0.2f) // If directions are too different
                {
                    // Simple blend back towards original direction
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
