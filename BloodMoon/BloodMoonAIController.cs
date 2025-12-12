using UnityEngine;
using Duckov.Utilities;
using Duckov;
using Duckov.ItemUsage;
using Cysharp.Threading.Tasks;
using ItemStatsSystem;
using Pathfinding;
using System.Collections.Generic;
using System.Linq;

namespace BloodMoon
{
    public class BloodMoonAIController : MonoBehaviour
    {
        // --- Components ---
        private CharacterMainControl _c = null!;
        private AIDataStore _store = null!;
        private Seeker? _seeker;
        private Path? _path;
        
        // --- Decision System ---
        private AIContext _context = new AIContext();
        private List<AIAction> _actions = new List<AIAction>();
        private AIAction? _currentAction;
        
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
        private Vector3 _spawnPos;
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
        private static readonly List<BloodMoonAIController> _all = new List<BloodMoonAIController>();
        private static readonly Collider[] _nonAllocColliders = new Collider[16];
        private float _sepCooldown;
        private System.Threading.Tasks.Task<UnityEngine.Vector3>? _sepTask;
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
            _spawnPos = c.transform.position;
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

            var preset = c.characterPreset;
            if (preset != null && preset.GetCharacterIcon() == GameplayDataSettings.UIStyle.BossCharacterIcon)
            {
                IsBoss = true;
            }
            
            // Randomize tick offset to distribute CPU load
            _aiTickTimer = Random.Range(0f, AI_TICK_INTERVAL);
        }

        private float _aiTickTimer;
        private const float AI_TICK_INTERVAL = 0.1f;

        private void Update()
        {
            if (!LevelManager.LevelInited || _c == null || CharacterMainControl.Main == null) return;
            
            // Safeguard: Ensure vanilla AI stays disabled
            var vanilla = _c.GetComponent<AICharacterController>();
            if (vanilla != null && vanilla.enabled) vanilla.enabled = false;

            _aliveTime += Time.deltaTime;
            
            // Global Updates (Timers need smooth updates)
            UpdateTimers();

            // Throttle Decision Making
            _aiTickTimer += Time.deltaTime;
            if (_aiTickTimer < AI_TICK_INTERVAL) return;
            _aiTickTimer = 0f;

            _context.UpdateSensors();
            
            // Cooldowns (Update based on interval)
            foreach (var a in _actions) a.UpdateCooldown(AI_TICK_INTERVAL);
            
            // Decision Logic
            AIAction? bestAction = null;
            float bestScore = -1f;
            
            foreach (var action in _actions)
            {
                float score = action.Evaluate(_context);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAction = action;
                }
            }
            
            if (bestAction != null && bestAction != _currentAction)
            {
                if (_currentAction != null) _currentAction.OnExit(_context);
                _currentAction = bestAction;
                _currentAction.OnEnter(_context);
            }
            
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
            
            // Buff nearby minions
            int count = 0;
            foreach (var ally in _all)
            {
                if (ally == null || ally == this || ally.IsBoss) continue;
                
                float d = Vector3.Distance(_c.transform.position, ally.transform.position);
                if (d < 25f)
                {
                    ally.ReceiveBuff();
                    count++;
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
             if (_c.CurrentHoldItemAgent != null && _c.CurrentHoldItemAgent.Item.GetComponent<Drug>() != null) return true;
             
             var inv = _c.CharacterItem?.Inventory; if (inv == null) return false;
             foreach(var item in inv) {
                if(item == null) continue;
                if(item.GetComponent<Drug>() != null) return true;
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
            _c.SetRunInput(false);
            _c.movementControl.SetMoveInput(Vector3.zero);
            
            // 1. If holding drug
            if (_c.CurrentHoldItemAgent != null && _c.CurrentHoldItemAgent.Item.GetComponent<Drug>() != null)
            {
                // 2. If holding but not using, use it
                bool reloading = _c.GetGun()?.IsReloading() ?? false;
                if (!reloading && _healWaitTimer <= 0f) 
                {
                    _c.UseItem(_c.CurrentHoldItemAgent.Item);
                    _healWaitTimer = 4.0f; // Assume ~4s for healing animation to be safe
                }
                return;
            }

            // 3. Find cover if exposed (unless we are boss and just want to tank it, or very desperate)
            // Bosses ignore cover check if HP > 40% to be aggressive
            bool skipCover = IsBoss && _c.Health.CurrentHealth > _c.Health.MaxHealth * 0.4f;
            if (!skipCover && _context.HasLoS && _coverCooldown <= 0f)
            {
                 if (MoveToCover()) return;
            }

            // 4. Find best healing item
            var inv = _c.CharacterItem?.Inventory; 
            if (inv == null) return;
            
            Item? bestItem = null;
            int bestQuality = -1;
            
            foreach (var item in inv)
            {
                if (item == null) continue;
                var d = item.GetComponent<Drug>();
                if (d != null)
                {
                    // Prefer higher quality items (often better meds)
                    if (item.Quality > bestQuality) 
                    { 
                        bestQuality = item.Quality; 
                        bestItem = item; 
                    }
                }
            }
            
            if (bestItem != null)
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
                // If exposed, try to move to cover
                if (MoveToCover()) return;
                
                // If no cover found, erratic movement (strafe)
                // Reuse EngageTarget strafe logic partially or just random move
                 _strafeTimer -= Time.deltaTime;
                if (_strafeTimer <= 0f)
                {
                    _strafeDir = Random.value > 0.5f ? 1 : -1;
                    _strafeTimer = 0.5f;
                }
                
                if (_context.Target != null)
                {
                     var dir = (_context.Target.transform.position - _c.transform.position).normalized;
                     var perp = Vector3.Cross(dir, Vector3.up) * _strafeDir;
                     _c.movementControl.SetMoveInput(perp);
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
            
            Vector3 aimPos = PredictAim(_context.Target);
            _c.SetAimPoint(aimPos);
            
            // Movement during combat (Strafing)
            Vector3 dir = _context.DirToTarget;
            
            _strafeTimer -= Time.deltaTime;
            if (_strafeTimer <= 0f)
            {
                _strafeDir = Random.value > 0.5f ? 1 : -1;
                _strafeTimer = Random.Range(0.6f, 1.6f);
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
            
            Vector3 move = (perp * 0.8f + dir * 0.2f).normalized;
            
            // Maintain distance
            float pref = GetPreferredDistance();
            if (dist < pref - 2f) move = -dir + perp * 0.5f; // Back up
            else if (dist > pref + 2f) move = dir + perp * 0.2f; // Close in
            
            _c.movementControl.SetMoveInput(move.normalized);
            
            // Tactical Stance: ADS if shooting and not moving fast
            bool shouldAds = dist > 10f && _shootTimer <= 0f; 
            _c.SetAdsInput(shouldAds);
            _c.SetRunInput(!shouldAds && move.magnitude > 0.1f);
            
            // Shooting
            if (_shootTimer <= 0f)
            {
                _c.Trigger(true, true, false);
                // Burst logic
                var gun = _c.GetGun();
                if (gun != null && gun.BulletDistance > 50f) // Sniper/DMR
                    _shootTimer = Random.Range(0.8f, 1.5f);
                else
                    _shootTimer = Random.Range(0.2f, 0.5f);
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
                    
                    // Score: Distance from us (closer is better to minimize travel time) + Flank Angle
                    float dist = Vector3.Distance(_c.transform.position, cand);
                    float score = 100f - dist;
                    
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
                // Intelligent Search: Check near cover points around last known position
                bool found = false;
                
                // Try 5 times to find a cover spot near last known pos
                for (int i = 0; i < 5; i++)
                {
                    var rnd = Random.insideUnitCircle * 15f;
                    var p = _context.LastKnownPos + new Vector3(rnd.x, 0f, rnd.y);
                    if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                    {
                        // Check if this spot provides cover relative to... where? 
                        // Maybe relative to the open area or just a valid nav spot.
                        // For search, we just want to move around the area.
                        _searchPoint = hit.point;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                {
                     var rnd = Random.insideUnitCircle * 10f;
                     var p = _context.LastKnownPos + new Vector3(rnd.x, 0f, rnd.y);
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

            // Door / Stuck Check Logic (Check every 1.0s)
            if (Time.time - _stuckCheckInterval > 1.0f)
            {
                _stuckCheckInterval = Time.time;
                if (Vector3.Distance(_c.transform.position, _lastDoorCheckPos) < 0.5f)
                {
                    _doorStuckTimer += 1.0f;
                }
                else
                {
                    _doorStuckTimer = 0f;
                    _lastDoorCheckPos = _c.transform.position;
                }

                if (_doorStuckTimer > 3.0f)
                {
                    _context.IsStuck = true; // Signal Unstuck Action
                    _doorStuckTimer = 0f;
                    _repathTimer = -1f; 
                    _waitingForPath = false; 
                    return (_c.transform.position - targetPos).normalized;
                }
            }

            _repathTimer -= Time.deltaTime;
            if (!_waitingForPath && (_path == null || _repathTimer <= 0f || Vector3.Distance(targetPos, _lastPathTarget) > 2.0f))
            {
                _lastPathTarget = targetPos;
                _repathTimer = 0.5f;
                _waitingForPath = true;
                _seeker.StartPath(_c.transform.position, targetPos, OnPathComplete);
            }

            if (_path == null || _path.vectorPath == null || _path.vectorPath.Count == 0)
            {
                // Fallback (Raycast avoidance)
                 _wallCheckTimer -= Time.deltaTime;
                if (_wallCheckTimer <= 0f)
                {
                    _wallCheckTimer = 0.1f;
                    var dir = (targetPos - _c.transform.position).normalized;
                    _cachedAvoidanceDir = dir;
                    var origin = _c.transform.position + Vector3.up * 1.0f;
                    var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                    if (Physics.Raycast(origin, dir, out var hit, 3.5f, mask))
                    {
                        var slide = Vector3.ProjectOnPlane(dir, hit.normal).normalized;
                        if (Vector3.Dot(slide, dir) > 0.1f) _cachedAvoidanceDir = slide;
                        else _cachedAvoidanceDir = Vector3.zero;
                    }
                }
                return _cachedAvoidanceDir;
            }

            // Follow Path
            float nextWaypointDistance = 3f;
            bool reachedEnd = false;
            float dist;
            while (true)
            {
                dist = Vector3.Distance(_c.transform.position, _path.vectorPath[_currentWaypoint]);
                if (dist < nextWaypointDistance)
                {
                    if (_currentWaypoint + 1 < _path.vectorPath.Count)
                    {
                        _currentWaypoint++;
                        continue;
                    }
                    reachedEnd = true;
                    break;
                }
                break;
            }

            if (reachedEnd)
            {
                if (Vector3.Distance(_c.transform.position, targetPos) > 1.5f)
                    return (targetPos - _c.transform.position).normalized;
                return Vector3.zero;
            }

            return (_path.vectorPath[_currentWaypoint] - _c.transform.position).normalized;
        }

        private void OnPathComplete(Path p)
        {
            if (!p.error)
            {
                _path = p;
                _currentWaypoint = 0;
            }
            _waitingForPath = false;
        }
        
        private bool FindCover(CharacterMainControl player, out Vector3 coverPos)
        {
            // Reuse existing FindCover logic
            coverPos = Vector3.zero;
            if (player == null) return false;
            
            // Try store known cover first
            if (_store.TryGetKnownCover(player, _c.transform.position, 18f, out var known))
            {
                 coverPos = known; return true;
            }
            
            var mask = GameplayDataSettings.Layers.fowBlockLayers;
            // Optimize: Use 8 rays (45 deg) instead of 12, and start from random offset
            int startIdx = Random.Range(0, 8);
            
            for (int i = 0; i < 8; i++)
            {
                int idx = (startIdx + i) % 8;
                var angle = idx * 45f * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Random.Range(4f, 10f);
                var pos = _c.transform.position + offset;
                
                // Quick line check from cover to target
                var origin = pos + Vector3.up * 1.0f;
                var target = player.transform.position + Vector3.up * 1.2f;
                var dir = target - origin;
                
                if (Physics.Raycast(origin, dir.normalized, dir.magnitude, mask))
                {
                    // Snap to ground
                    if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 4f, GameplayDataSettings.Layers.groundLayerMask))
                        coverPos = hit.point;
                    else
                        coverPos = pos;
                    return true;
                }
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
            if (_dashCooldown <= 0f && _hurtRecently)
            {
                _c.Dash();
                _dashCooldown = 3f;
                _hurtRecently = false;
            }
        }
        
        private void OnHurt(DamageInfo dmg)
        {
            _hurtRecently = true;
            _pressureScore += 2f;
            _context.Pressure = _pressureScore;
            // Force re-evaluate actions next frame potentially
        }
        
        private void OnDeadAI(DamageInfo dmg)
        {
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
            if (desired.sqrMagnitude < 0.04f) { _stuckTimer = 0f; return desired; }
            
            if (moved < 0.02f)
            {
                _stuckTimer += Time.deltaTime;
                if (_stuckTimer > 0.5f)
                {
                    // Report stuck to context so Unstuck action can take over next frame
                    _context.IsStuck = true; 
                    return Quaternion.AngleAxis(90, Vector3.up) * desired;
                }
            }
            else
            {
                _stuckTimer = 0f;
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
            // Reuse existing
            _sepCooldown -= Time.deltaTime;
            if (_sepTask != null && _sepTask.IsCompleted)
            {
                _sepBgResult = _sepTask.Result;
                _sepTask = null;
            }
            if (_sepCooldown <= 0f && _sepTask == null)
            {
                var snap = _all.Select(x => x != null && x._c != null ? x._c.transform.position : Vector3.positiveInfinity).ToArray();
                var selfPos = _c.transform.position;
                _sepTask = System.Threading.Tasks.Task.Run(() =>
                {
                    Vector3 sep = Vector3.zero; 
                    foreach (var p in snap)
                    {
                        if (p.Equals(Vector3.positiveInfinity)) continue;
                        var delta = selfPos - p; delta.y = 0f;
                        float d = delta.magnitude;
                        if (d > 0f && d < 1.2f) sep += delta.normalized * (1.2f - d);
                    }
                    return sep * 0.6f;
                });
                _sepCooldown = 0.2f;
            }
            return (desired + _sepBgResult).normalized;
        }
        
        private void OnDestroy()
        {
            _all.Remove(this);
        }
    }
}
