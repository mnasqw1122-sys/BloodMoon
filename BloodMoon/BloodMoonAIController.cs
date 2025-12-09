using UnityEngine;
using Duckov.Utilities;
using Duckov;
using Duckov.ItemUsage;
using Cysharp.Threading.Tasks;
using ItemStatsSystem;
using Pathfinding;

namespace BloodMoon
{
        public class BloodMoonAIController : MonoBehaviour
        {
        private Seeker? _seeker;
        private Path? _path;
        private int _currentWaypoint;
        private bool _waitingForPath;
        private float _repathTimer;
        private Vector3 _lastPathTarget;

        private static readonly System.Collections.Generic.List<BloodMoonAIController> _all = new System.Collections.Generic.List<BloodMoonAIController>();
        private CharacterMainControl _c = null!;
        private AIDataStore _store = null!;
        private float _shootTimer;
        private float _strafeTimer;
        private int _strafeDir = 1;
        private Vector3 _targetCoverPos;
        private float _coverCooldown;
        private bool _movingToCover;
        private float _peekTimer;
        private bool _peeking;
        private float _dashCooldown;
        private bool _hurtRecently;
        private Vector3 _lastCoverPos;
        private float _skillCooldown;
        private enum AIState { engage, reposition, reload, heal }
        private AIState _state;
        private float _suppressTimer;
        private float _fireHoldTimer;
        private bool _dashMonitorActive;
        private float _dashMonitorStartDist;
        private float _dashMonitorStartTime;
        private bool _peekMonitorActive;
        private float _peekMonitorStartTime;
        private float _pressureScore;
        private bool _skillUsing;
        private float _skillAimTimer;
        private float _chaseDelayTimer;
        private bool _canChase;
        private float _flankScanCooldown;
        private int _approachSliceIdx;
        private UnityEngine.Vector3[]? _approachCand;
        private float[]? _approachHeats;
        private UnityEngine.Vector3 _approachBest;
        private float _approachBestScore;
        private float _approachGenCooldown;
        private int _flankSliceIdx;
        private UnityEngine.Vector3[]? _flankCand;
        private float[]? _flankHeats;
        private UnityEngine.Vector3 _flankBest;
        private float _flankBestScore;
        private float _flankGenCooldown;
        
        private Vector3 _prevPos;
        private float _stuckTimer;
        private float _roleTimer;
        private bool _isSuppressor;
        
        private Vector3 _currentApproachPoint;
        private float _approachStartTime;
        private bool _approachActive;
        private float _doorCooldown;
        private CharacterMainControl? _leader;
        
        private int _wingIndex = -1;
        private static readonly Collider[] _nonAllocColliders = new Collider[16];

        private Vector3 _spawnPos;
        private float _aliveTime;

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
            _roleTimer = Random.Range(6f, 10f);
            _isSuppressor = Random.value < 0.5f;
            if (!_all.Contains(this)) _all.Add(this);
        }

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

        private bool _isPathingFrame;
        private float _wallCheckTimer;
        private Vector3 _cachedAvoidanceDir;

        private float _stuckCheckInterval;
        private float _doorStuckTimer;
        private Vector3 _lastDoorCheckPos;

        private void OnPathComplete(Path p)
        {
            if (!p.error)
            {
                _path = p;
                _currentWaypoint = 0;
            }
            _waitingForPath = false;
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

                // If stuck at a door/wall for > 3 seconds, force repath to a new location
                if (_doorStuckTimer > 3.0f)
                {
                    // Severe Stuck Check: If we are stuck near spawn for > 6s total alive time, it's a bad spawn
                    if (_aliveTime > 6.0f && Vector3.Distance(_c.transform.position, _spawnPos) < 2.5f)
                    {
                        _store.MarkStuckSpot(_spawnPos);
                    }
                    else if (_doorStuckTimer > 6.0f) // Extremely stuck elsewhere
                    {
                        _store.MarkStuckSpot(_c.transform.position);
                    }

                    _doorStuckTimer = 0f;
                    _lastDoorCheckPos = _c.transform.position;
                    
                    // Force recalculate path, maybe to a slightly different spot to "unstick"
                    _repathTimer = -1f; 
                    _waitingForPath = false; // Cancel current wait to force immediate repath
                    
                    // Try to move backwards slightly to unstuck
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
                // Fallback while waiting or if path failed
                _isPathingFrame = false;
                var fallbackDir = (targetPos - _c.transform.position).normalized;
                
                // Optimization: Throttle Raycast checks to 10 times per second
                _wallCheckTimer -= Time.deltaTime;
                if (_wallCheckTimer <= 0f)
                {
                    _wallCheckTimer = 0.1f;
                    _cachedAvoidanceDir = fallbackDir;
                    
                    var origin = _c.transform.position + Vector3.up * 1.0f;
                    var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                    
                    // Simple avoidance if blocked by a wall
                    if (Physics.Raycast(origin, fallbackDir, out var hit, 3.5f, mask))
                    {
                        // Slide along the wall instead of pushing into it
                        var slide = Vector3.ProjectOnPlane(fallbackDir, hit.normal).normalized;
                        // If sliding is also blocked or too sharp, maybe just wait/stop
                        if (Vector3.Dot(slide, fallbackDir) > 0.1f) 
                        {
                            _cachedAvoidanceDir = slide;
                        }
                        else
                        {
                             _cachedAvoidanceDir = Vector3.zero;
                        }
                    }
                }
                return _cachedAvoidanceDir;
            }

            _isPathingFrame = true;
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
                {
                     return (targetPos - _c.transform.position).normalized;
                }
                if (dist < 0.5f) return Vector3.zero;
            }

            Vector3 dir = (_path.vectorPath[_currentWaypoint] - _c.transform.position).normalized;
            return dir;
        }

        private void Update()
        {
            if (!LevelManager.LevelInited || _c == null || CharacterMainControl.Main == null) return;
            _aliveTime += Time.deltaTime;
            _isPathingFrame = false;
            var player = CharacterMainControl.Main;
            var dir = player.transform.position - _c.transform.position;
            dir.y = 0f;
            float dist = dir.magnitude;
            float avg = _store.GetAverageSpeed();
            ChooseWeapon(dist);
            Vector3 move = Vector3.zero;
            bool hasLoS = HasLineOfSight(player);
            if (hasLoS)
            {
                _store.LastKnownPlayerPos = player.transform.position;
                _store.LastSeenTime = Time.time;
                
                // Dodge Logic: Check if player is aiming at us
                var pFwd = player.transform.forward;
                var toMe = (_c.transform.position - player.transform.position).normalized;
                // Dot > 0.98 means aiming within ~11 degrees
                if (Vector3.Dot(pFwd, toMe) > 0.98f) 
                { 
                     _pressureScore += Time.deltaTime * 3.0f; // Pressure builds up very fast when aimed at
                     
                     // Occasional panic dash if cooldown is ready and not already dodging
                     if (_dashCooldown <= 0f && Random.value < (0.1f * Time.deltaTime)) 
                     {
                          _c.Dash();
                          _dashCooldown = Random.Range(1.5f, 3.0f);
                          // Force a strafe direction change
                          _strafeDir *= -1;
                     }
                }

                // Memory: Record player position if they are shooting at us or we are engaging them
                if (_hurtRecently || _c.GetGun()?.IsReloading() == true)
                {
                    _store.MarkPlayerAmbush(player.transform.position);
                }
            }
            HandleReload(dist, hasLoS);
            HandleAds(dist);
            HandleDash();
            ManagePeek(player, hasLoS, dist);
            _flankScanCooldown -= Time.deltaTime;
            if (_flankScanCooldown <= 0f)
            {
                CoordinateFlank(dir);
                _flankScanCooldown = 0.5f;
            }
            _roleTimer -= Time.deltaTime;
            if (_roleTimer <= 0f)
            {
                _isSuppressor = !_isSuppressor;
                _roleTimer = Random.Range(6f, 10f);
            }
            ManageSkillUse(hasLoS, dist);
            ManageHealing();
            UpdateState(hasLoS, dist);
            if (!_canChase)
            {
                _chaseDelayTimer -= Time.deltaTime;
                if (_chaseDelayTimer <= 0f) _canChase = true;
            }
            _fireHoldTimer = Mathf.Max(0f, _fireHoldTimer - Time.deltaTime);
            _pressureScore = Mathf.Max(0f, _pressureScore - Time.deltaTime * 0.5f);
            
            // Learning: Reward survival in LoS
            if (hasLoS && _pressureScore < 1.0f)
            {
                _store.ApplyReward("peek_prob", 0.01f * Time.deltaTime);
            }
            // Learning: Punish if under heavy pressure
            if (_pressureScore > 3.0f)
            {
                _store.ApplyReward("approach", -0.05f * Time.deltaTime);
                _store.ApplyReward("dash_prob", 0.05f * Time.deltaTime); // Dash more if hurt
            }

            float pressureFactor = GetPressureFactor();
            _store.RecenterWeights(Time.deltaTime * (pressureFactor < 0.9f ? 0.06f : 0.02f));
            if (_leader != null)
            {
                _store.UpdateLeaderPref(_leader.GetInstanceID(), _pressureScore, _leader.transform.position);
            }
            if (_dashMonitorActive)
            {
                if (Time.time - _dashMonitorStartTime > 2.0f)
                {
                    if (dist < _dashMonitorStartDist - 1.5f) _store.ApplyReward("dash_prob", 0.1f); else _store.ApplyReward("dash_prob", -0.05f);
                    _dashMonitorActive = false;
                }
            }
            if (_peekMonitorActive)
            {
                if (hasLoS)
                {
                    _store.ApplyReward("peek_prob", 0.1f);
                    _peekMonitorActive = false;
                }
                else if (Time.time - _peekMonitorStartTime > 2.0f)
                {
                    _store.ApplyReward("peek_prob", -0.05f);
                    _peekMonitorActive = false;
                }
            }
            if (_approachActive)
            {
                float elapsed = Time.time - _approachStartTime;
                if (hasLoS || dist < GetPreferredDistance() + 1.0f)
                {
                    _store.RecordApproachOutcome(_currentApproachPoint, true);
                    _store.ApplyReward("approach", 0.2f);
                    _approachActive = false;
                }
                else if (elapsed > 6f)
                {
                    _store.RecordApproachOutcome(_currentApproachPoint, false);
                    _store.ApplyReward("approach", -0.2f);
                    _approachActive = false;
                }
            }
            
            if (_movingToCover)
            {
                float distToCover = Vector3.Distance(_c.transform.position, _targetCoverPos);
                if (distToCover < 1.5f)
                {
                    _movingToCover = false;
                    _lastCoverPos = _targetCoverPos;
                }
                else
                {
                    move = GetPathMove(_targetCoverPos);
                    _c.SetRunInput(true);
                }
            }
            else
            {
                if ((_state == AIState.reload || _state == AIState.heal) && _coverCooldown <= 0f)
                {
                    if (FindCover(player, out _targetCoverPos))
                    {
                        _movingToCover = true;
                        _c.SetRunInput(true);
                        _coverCooldown = 3f;
                    }
                }
                if (!_movingToCover)
                {
                    bool moveCalculated = false;
                    if (_state == AIState.reposition || _state == AIState.engage)
                    {
                        if (!hasLoS)
                        {
                            Vector3 targetPosForSearch = (_store.LastSeenTime > 0f) ? _store.LastKnownPlayerPos : player.transform.position;
                            if (GetPressureFactor() < 0.8f && _coverCooldown <= 0f)
                            {
                                if (FindCover(player, out _targetCoverPos))
                                {
                                    _movingToCover = true;
                                    _c.SetRunInput(true);
                                    _coverCooldown = 3f;
                                    moveCalculated = true;
                                }
                            }
                            // Ambush / Hold Angle logic if pressure is high
                            else if (_pressureScore > 2.5f && _coverCooldown <= 0f)
                            {
                                if (FindCover(player, out _targetCoverPos))
                                {
                                    // Move to cover and WAIT (don't rush out immediately)
                                    _movingToCover = true;
                                    _c.SetRunInput(true);
                                    _coverCooldown = 5f; // Stay in cover longer
                                    _peekTimer = 0f; // Don't peek immediately
                                    _peeking = false;
                                    moveCalculated = true;
                                }
                            }
                            
                            if (!_movingToCover)
                            {
                                if (TryGetAggressiveApproachPoint(targetPosForSearch, out var ap))
                                {
                                    move = GetPathMove(ap);
                                    _c.SetRunInput(true);
                                    _currentApproachPoint = ap;
                                    _approachStartTime = Time.time;
                                    _approachActive = true;
                                    moveCalculated = true;
                                }
                                else if (TryGetFlankPoint(targetPosForSearch, out var fp))
                                {
                                    move = GetPathMove(fp);
                                    _c.SetRunInput(true);
                                    _currentApproachPoint = fp;
                                    _approachStartTime = Time.time;
                                    _approachActive = true;
                                    moveCalculated = true;
                                }
                                else
                                {
                                    var pGun = player.GetGun();
                                    if (pGun != null && pGun.IsReloading())
                                    {
                                        _store.RegisterPlayerReload();
                                        move = GetPathMove(_store.LastKnownPlayerPos);
                                        _c.SetRunInput(true);
                                        moveCalculated = true;
                                    }
                                    else if (_store.LastSeenTime > 0f && Time.time - _store.LastSeenTime < 60f)
                                    {
                                        Vector3 target = _store.LastKnownPlayerPos;
                                        float distToLastKnown = Vector3.Distance(_c.transform.position, target);
                                        
                                        // Enhanced Search Logic
                                        if (distToLastKnown < 5.0f)
                                        {
                                            // Reached last known pos, start searching locally
                                            if (_searchTimer <= 0f)
                                            {
                                                 // Pick a random point around the last known position to investigate
                                                 var rnd = Random.insideUnitCircle * Random.Range(4f, 12f);
                                                 Vector3 candidate = target + new Vector3(rnd.x, 0f, rnd.y);
                                                 
                                                 // Validate point is reachable/not in wall
                                                 if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out var hit, 10f, GameplayDataSettings.Layers.groundLayerMask))
                                                 {
                                                      _searchPoint = hit.point;
                                                      _searchTimer = Random.Range(4f, 8f);
                                                 }
                                                 else
                                                 {
                                                      _searchPoint = target; // Stay put if invalid
                                                 }
                                            }
                                            
                                            _searchTimer -= Time.deltaTime;
                                            move = GetPathMove(_searchPoint);
                                            _c.SetRunInput(false); // Walk carefully while searching
                                            
                                            // Look around while searching
                                            if (Time.time % 2.0f < 1.0f)
                                            {
                                                var lookDir = Quaternion.AngleAxis(Mathf.Sin(Time.time) * 60f, Vector3.up) * (_searchPoint - _c.transform.position).normalized;
                                                _c.SetAimPoint(_c.transform.position + lookDir * 10f);
                                            }
                                            moveCalculated = true;
                                        }
                                        else
                                        {
                                            move = GetPathMove(target);
                                            _c.SetRunInput(true);
                                            moveCalculated = true;
                                        }
                                    }
                                }
                            }
                        }
                        else if (_state == AIState.reposition && _dashCooldown <= 0f)
                        {
                            var origin = _c.transform.position + Vector3.up * 0.8f;
                            var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                            bool blocked = Physics.Raycast(origin, dir.normalized, 2.0f, mask);
                            float pf = GetPressureFactor();
                            float minD = pf < 0.8f ? 6f : 5f;
                            float maxD = pf < 0.8f ? 10f : 14f;
                            bool inRange = dist > minD && dist < maxD;
                            if (inRange && !blocked && Random.value < (_store.GetWeight("dash_prob", 0.6f) * pf))
                            {
                                move = dir.normalized;
                                _c.SetRunInput(true);
                                _c.Dash();
                                _dashCooldown = Random.Range(2.2f, 3.8f);
                                _dashMonitorActive = true;
                                _dashMonitorStartDist = dist;
                                _dashMonitorStartTime = Time.time;
                                moveCalculated = true;
                            }
                        }

                        if (!moveCalculated)
                        {
                            float prefer = GetPreferredDistance();
                            Vector3? wingPos = GetWingPosition(player);
                            
                            if (wingPos.HasValue && Vector3.Distance(_c.transform.position, wingPos.Value) > 2.0f)
                            {
                                // If we have a wing position, prioritizing moving there using A* Pathfinding
                                // This prevents getting stuck on walls by "steering" blindly
                                move = GetPathMove(wingPos.Value);
                                _c.SetRunInput(true);
                            }
                            else if (dist > prefer)
                            {
                                move = GetPathMove(player.transform.position);
                                _c.SetRunInput(true);
                            }
                            else if (dist < (pressureFactor > 1.0f ? 4.8f : 4f))
                            {
                                move = (-dir).normalized;
                                _c.SetRunInput(true);
                            }
                            else
                            {
                                _strafeTimer -= Time.deltaTime;
                                if (_strafeTimer <= 0f)
                                {
                                    _strafeDir = Random.value > 0.5f ? 1 : -1;
                                    _strafeTimer = Random.Range(0.6f, 1.6f);
                                }
                                var perp = Vector3.Cross(dir.normalized, Vector3.up) * _strafeDir;
                                
                                // Strafing Wall Check
                                // If the strafe direction is blocked by a wall, flip the direction immediately
                                var origin = _c.transform.position + Vector3.up * 1.0f;
                                var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                                if (Physics.Raycast(origin, perp, 1.5f, mask))
                                {
                                    _strafeDir *= -1;
                                    perp = Vector3.Cross(dir.normalized, Vector3.up) * _strafeDir;
                                }
                                
                                move = (perp * 0.9f + dir.normalized * 0.1f).normalized;
                                _c.SetRunInput(false);
                                if (_peeking && _lastCoverPos != Vector3.zero)
                                {
                                    var side = Vector3.Cross(Vector3.up, dir.normalized) * _strafeDir;
                                    move = (move + side * 0.6f).normalized;
                                }
                            }
                        }
                    }
                }
            }
            if (!_isPathingFrame)
            {
                move = TryUnstuck(move);
            }
            move = ApplyFormationSeparation(move);
            TryOpenDoorAhead(move);
            _c.movementControl.SetMoveInput(move);
            Vector3 aimPoint = PredictAim(player);
            _c.SetAimPoint(aimPoint);
            _shootTimer -= Time.deltaTime;
            if (_state == AIState.engage && hasLoS && dist < 32f)
            {
                _suppressTimer -= Time.deltaTime;
            float pf = GetPressureFactor();
            bool spray = _isSuppressor || _fireHoldTimer > 0f || Random.value < (_store.GetWeight("spray_prob", 0.15f) * pf);
            if (spray)
            {
                _c.Trigger(true, true, false);
                _shootTimer = 0.05f;
            }
                else if (_shootTimer <= 0f)
                {
                    var cam = GameCamera.Instance;
                    bool offscreen = cam != null && cam.IsOffScreen(_c.transform.position);
                    float baseInterval = offscreen ? 0.1f : Mathf.Lerp(0.18f, 0.5f, Random.value);
                    float interval = _isSuppressor ? baseInterval * 0.7f : baseInterval;
                    if (_suppressTimer <= 0f)
                    {
                        interval *= _isSuppressor ? 0.6f : 0.75f;
                        _suppressTimer = Random.Range(1.0f, 2.0f);
                    }
                    _shootTimer = interval;
                    _c.Trigger(true, true, false);
                }
            }
            else
            {
                _c.Trigger(false, false, false);
            }
            _coverCooldown -= Time.deltaTime;
            _store.RecordPlayerSpeed(player.Velocity.magnitude);
            _store.DecayAndPrune(Time.time, 120f);
        }

        private void ChooseWeapon(float dist)
        {
            var prim = _c.PrimWeaponSlot()?.Content;
            var sec = _c.SecWeaponSlot()?.Content;
            var g0 = _c.GetGun();
            int bc0 = g0 != null ? g0.BulletCount : 0;
            int bc1 = 0;
            
            float switchDist = 12f;
            int distHash = "BulletDistance".GetHashCode();
            if (prim != null)
            {
                 float d = prim.GetStatValue(distHash);
                 if (d > 60f) switchDist = 20f; // Sniper/DMR -> Use pistol up to 20m
                 else if (d < 25f) switchDist = 8f; // SMG/Shotgun -> Keep using it closer
            }

            if (sec != null)
            {
                var sgs = sec.GetComponent<ItemSetting_Gun>();
                bc1 = sgs != null ? sgs.BulletCount : 0;
            }
            if (dist < 4.5f)
            {
                _c.SwitchToWeapon(-1);
                TryMeleeEngage(CharacterMainControl.Main);
                return;
            }
            if (bc0 <= 0 && bc1 > 0)
            {
                // Fix: Only switch weapon if we have NO ammo for current gun in inventory
                // This allows the AI to reload the infinite ammo instead of switching
                if (!HasAmmoForGun(g0))
                {
                    _c.SwitchToWeapon(1);
                    return;
                }
            }
            if (dist < switchDist)
            {
                _c.SwitchToWeapon(1);
            }
            else
            {
                _c.SwitchToWeapon(0);
            }
        }

        private bool HasAmmoForGun(ItemAgent_Gun? gun)
        {
            if (gun == null) return false;
            var inv = _c.CharacterItem?.Inventory;
            if (inv == null) return false;
            
            // Check loaded bullets
            if (gun.BulletCount > 0) return true;

            // Check inventory
            var caliber = gun.Item?.Constants?.GetString("Caliber") ?? string.Empty;
            if (string.IsNullOrEmpty(caliber)) return false;

            foreach (var item in inv)
            {
                if (item == null) continue;
                if (!item.Tags.Contains(GameplayDataSettings.Tags.Bullet)) continue;
                var cal = item.Constants?.GetString("Caliber") ?? string.Empty;
                if (cal == caliber) return true;
            }
            return false;
        }

        private void TryMeleeEngage(CharacterMainControl player)
        {
            var melee = _c.GetMeleeWeapon();
            if (melee == null) return;
            if (melee.AttackableTargetInRange())
            {
                _c.Attack();
            }
            else if (player != null)
            {
                var toward = player.transform.position - _c.transform.position; toward.y = 0f;
                _c.movementControl.SetMoveInput(toward.normalized);
                _c.SetRunInput(true);
            }
        }

        private bool HasLineOfSight(CharacterMainControl player)
        {
            var origin = _c.transform.position + Vector3.up * 1.2f;
            var target = player.transform.position + Vector3.up * 1.2f;
            var dir = target - origin;
            // Use official FOW layers for vision check to match game logic
            var mask = GameplayDataSettings.Layers.fowBlockLayers;
            
            // If thermal is on, we might want to use fowBlockLayersWithThermal, but for AI we generally assume standard vision
            // unless we want them to cheat. Let's stick to standard FOW blocks which likely includes walls.
            
            if (!Physics.Raycast(origin, dir.normalized, dir.magnitude, mask)) return true;
            
            // Side checks for better accuracy
            var side = Vector3.Cross(Vector3.up, dir.normalized) * 0.4f;
            if (!Physics.Raycast(origin + side, (target - (origin + side)).normalized, dir.magnitude, mask)) return true;
            if (!Physics.Raycast(origin - side, (target - (origin - side)).normalized, dir.magnitude, mask)) return true;
            
            return false;
        }

        private bool FindCover(CharacterMainControl player, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            var center = _c.transform.position;
            if (_store.TryGetKnownCover(player, center, 18f, out var known))
            {
                coverPos = known;
                return true;
            }
            // Use FOW layers for cover check (hiding from view)
            var mask = GameplayDataSettings.Layers.fowBlockLayers;
            
            for (int i = 0; i < 12; i++)
            {
                var angle = i * 30f * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Random.Range(4f, 10f);
                var pos = center + offset;
                var origin = pos + Vector3.up * 1.0f;
                var target = player.transform.position + Vector3.up * 1.2f;
                var dir = target - origin;
                if (Physics.Raycast(origin, dir.normalized, dir.magnitude, mask))
                {
                    coverPos = pos;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetAggressiveApproachPoint(Vector3 center, out Vector3 point)
        {
            point = Vector3.zero;
            int N = 16;
            if (_approachCand == null || _approachGenCooldown <= 0f)
            {
                _approachCand = new Vector3[N];
                for (int i = 0; i < N; i++)
                {
                    var angle = (i * (360f / N) + Random.Range(-10f, 10f)) * Mathf.Deg2Rad;
                    var radius = Random.Range(9f, 14f);
                    _approachCand[i] = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                }
                _approachHeats = _store.ComputeHeatBatch(_approachCand, Time.time, 6f);
                _approachSliceIdx = 0; _approachBestScore = -1f; _approachBest = Vector3.zero; _approachGenCooldown = 0.5f;
            }
            _approachGenCooldown -= Time.deltaTime;
            int slice = 4;
            for (int j = 0; j < slice && _approachCand != null && _approachHeats != null; j++)
            {
                int i = (_approachSliceIdx + j) % N;
                var candidate = _approachCand[i];
                if (_approachHeats[i] > 0.35f) continue;
                var origin = candidate + Vector3.up * 1.2f;
                var target2 = center + Vector3.up * 1.2f;
                var dir2 = target2 - origin;
                var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                if (Physics.Raycast(origin, dir2.normalized, dir2.magnitude, mask)) continue;
                
                // Memory Check: Avoid areas exposed to known player ambush spots
                if (_store.IsPlayerAmbushSpot(candidate, 5.0f)) continue;

                float w = _store.GetApproachWeight(candidate);
                float obstaclePenalty = 1f;
                float baseScore = w * obstaclePenalty * (1f - _approachHeats[i]);
                float score = baseScore * _store.GetWeight("approach", 1f);
                if (score > _approachBestScore)
                {
                    _approachBestScore = score; _approachBest = candidate;
                }
            }
            _approachSliceIdx = (_approachSliceIdx + slice) % N;
            if (_approachBestScore > 0f) { point = _approachBest; return true; }
            return false;
        }

        private bool TryGetFlankPoint(Vector3 center, out Vector3 point)
        {
            point = Vector3.zero;
            var toMe = (_c.transform.position - center); toMe.y = 0f;
            var rnd2 = Random.insideUnitCircle.normalized;
            var baseDir = toMe.sqrMagnitude > 1e-3 ? toMe.normalized : new Vector3(rnd2.x, 0f, rnd2.y);
            var left = Quaternion.AngleAxis(70f, Vector3.up) * baseDir;
            var right = Quaternion.AngleAxis(-70f, Vector3.up) * baseDir;
            Vector3[] dirs = new Vector3[] { left, right };
            int FN = 8;
            if (_flankCand == null || _flankGenCooldown <= 0f)
            {
                _flankCand = new Vector3[FN]; int ci = 0;
                foreach (var d in dirs)
                {
                    for (int k = 0; k < FN / 2; k++) _flankCand[ci++] = center + d * Random.Range(10f, 16f);
                }
                _flankHeats = _store.ComputeHeatBatch(_flankCand, Time.time, 6f);
                _flankSliceIdx = 0; _flankBestScore = -1f; _flankBest = Vector3.zero; _flankGenCooldown = 0.5f;
            }
            _flankGenCooldown -= Time.deltaTime;
            int fslice = 2;
            for (int j = 0; j < fslice && _flankCand != null && _flankHeats != null; j++)
            {
                int i = (_flankSliceIdx + j) % FN;
                var candidate = _flankCand[i];
                if (_flankHeats[i] > 0.35f) continue;
                var origin = candidate + Vector3.up * 1.2f;
                var target2 = center + Vector3.up * 1.2f;
                var dir2 = target2 - origin;
                var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                if (Physics.Raycast(origin, dir2.normalized, dir2.magnitude, mask)) continue;
                float w = _store.GetApproachWeight(candidate);
                float obstaclePenalty = 1f;
                float baseScore = w * obstaclePenalty * (1f - _flankHeats[i]);
                float score = baseScore * _store.GetWeight("flank", 1f);
                if (score > _flankBestScore)
                {
                    _flankBestScore = score; _flankBest = candidate;
                }
            }
            _flankSliceIdx = (_flankSliceIdx + fslice) % FN;
            if (_flankBestScore > 0f) { point = _flankBest; return true; }
            return false;
        }

        private void HandleReload(float dist, bool hasLoS)
        {
            var gun = _c.GetGun();
            if (gun == null || gun.IsReloading()) return;
            
            int cap = Mathf.Max(1, gun.Capacity);
            int current = gun.BulletCount;
            float ratio = (float)current / cap;
            
            bool critical = current <= 0;
            // Tactical Reload: Reload if safe (No LoS) and ammo is below 60%
            // Or if ammo is very low (< 20%) even if we have LoS (desperate)
            bool tactical = !hasLoS && ratio < 0.6f && _state != AIState.engage;
            bool low = ratio < 0.2f; // 20%
            
            if (critical || tactical || (low && (!hasLoS || dist > 8f)))
            {
                // Ensure Best Ammo Logic
                var inv = _c.CharacterItem?.Inventory;
                var gunItemSetting = gun.GunItemSetting;
                if (inv != null && gunItemSetting != null)
                {
                    int apHash = "ArmorPiercingGain".GetHashCode();
                    int calHash = "Caliber".GetHashCode();
                    var gunRef = gun;
                    var gunItem = gunRef.Item;
                    if (gunItem != null && gunItem.Constants != null)
                    {
                        string cal = gunItem.Constants.GetString(calHash) ?? string.Empty;
                        float bestGain = -1f; Item? best = null; int totalCount = 0;
                        foreach (var it in inv)
                        {
                            if (it == null) continue;
                            if (!it.Tags.Contains(GameplayDataSettings.Tags.Bullet)) continue;
                            string ic = it.Constants?.GetString(calHash) ?? string.Empty;
                            if (!string.IsNullOrEmpty(cal) && ic != cal) continue;
                            totalCount++;
                            float g = it.Constants?.GetFloat(apHash, 0f) ?? 0f;
                            if (g > bestGain)
                            {
                                bestGain = g; best = it;
                            }
                        }
                        if (best != null)
                        {
                            gunItemSetting.SetTargetBulletType(best);
                        }
                        else
                        {
                            var types = gunItemSetting.GetBulletTypesInInventory(inv);
                            int bestId = -1; int bestCount = 0;
                            if (types != null)
                            {
                                foreach (var kv in types)
                                {
                                    if (kv.Value.count > bestCount)
                                    {
                                        bestCount = kv.Value.count; bestId = kv.Key;
                                    }
                                }
                            }
                            if (bestId != -1 && bestId != gunItemSetting.TargetBulletID)
                            {
                                gunItemSetting.SetTargetBulletType(bestId);
                            }
                        }
                        if (best == null && totalCount == 0)
                        {
                            UniTask.Void(async () =>
                            {
                                var filter = new ItemFilter { caliber = cal, minQuality = 5, maxQuality = 6, requireTags = new Tag[] { GameplayDataSettings.Tags.Bullet } };
                                var ids = ItemAssetsCollection.Search(filter);
                                if (ids != null && ids.Length > 0)
                                {
                                    var ammo = await ItemAssetsCollection.InstantiateAsync(ids.GetRandom());
                                    inv.AddItem(ammo);
                                    gunItemSetting.SetTargetBulletType(ammo);
                                    gunItemSetting.LoadBulletsFromInventory(inv).Forget();
                                }
                            });
                        }
                        else
                        {
                            gunItemSetting.LoadBulletsFromInventory(inv).Forget();
                        }
                    }
                }
                
                gun.BeginReload();
            }
        }

        private void HandleAds(float dist)
        {
            if (dist < 20f && !_c.Running)
            {
                _c.SetAdsInput(true);
            }
            else
            {
                _c.SetAdsInput(false);
            }
        }

        private void HandleDash()
        {
            _dashCooldown -= Time.deltaTime;
            if (_hurtRecently && _dashCooldown <= 0f)
            {
                var origin = _c.transform.position + Vector3.up * 0.8f;
                var forward = _c.transform.forward;
                var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                bool blocked = Physics.Raycast(origin, forward, 1.0f, mask);
                if (!blocked && _state != AIState.heal && _state != AIState.reload)
                {
                    _c.Dash();
                    _dashCooldown = Random.Range(2.0f, 4.0f);
                }
                _hurtRecently = false;
            }
        }

        private void OnHurt(DamageInfo dmg)
        {
            _hurtRecently = true;
            _store.MarkDanger(_c.transform.position);
            // Memory: If hurt by player, record source
            if (dmg.fromCharacter != null && dmg.fromCharacter.IsMainCharacter)
            {
                _store.MarkPlayerAmbush(dmg.fromCharacter.transform.position);
            }
            _fireHoldTimer = 1.2f;
            _pressureScore += 1.0f;
        }

        private void OnDeadAI(DamageInfo dmg)
        {
            if (_c != null)
            {
                _store.RegisterDeath(_c.transform.position);
                // Memory: If we know where the player is, mark it as a dangerous ambush spot
                if (_store.LastSeenTime > 0f && Time.time - _store.LastSeenTime < 10f)
                {
                    _store.MarkPlayerAmbush(_store.LastKnownPlayerPos);
                }
                _c.Trigger(false, false, false);
                _c.SetRunInput(false);
                _c.movementControl.SetMoveInput(Vector3.zero);
            }
            enabled = false;
        }

        private float GetPressureFactor()
        {
            if (_pressureScore >= 2.0f) return 0.6f;
            if (_pressureScore < 0.2f) return 1.15f;
            return 1.0f;
        }

        private float _sepCooldown;
        private Vector3 _sepCache;
        private System.Threading.Tasks.Task<UnityEngine.Vector3>? _sepTask;
        private UnityEngine.Vector3 _sepBgResult;
        private Vector3 ApplyFormationSeparation(Vector3 desired)
        {
            _sepCooldown -= Time.deltaTime;
            if (_sepTask != null && _sepTask.IsCompleted)
            {
                _sepBgResult = _sepTask.Result;
                _sepTask = null;
            }
            if (_sepCooldown <= 0f && _sepTask == null)
            {
                var snap = new Vector3[_all.Count];
                for (int i = 0; i < _all.Count; i++)
                {
                    var o = _all[i];
                    snap[i] = (o != null && o._c != null) ? o._c.transform.position : Vector3.positiveInfinity;
                }
                var selfPos = _c.transform.position;
                _sepTask = System.Threading.Tasks.Task.Run(() =>
                {
                    Vector3 sep = Vector3.zero; int count = 0;
                    for (int i = 0; i < snap.Length; i++)
                    {
                        var p = snap[i];
                        if (p.Equals(Vector3.positiveInfinity)) continue;
                        var delta = selfPos - p; delta.y = 0f;
                        float d = delta.magnitude;
                        if (d > 0f && d < 1.2f)
                        {
                            sep += delta.normalized * (1.2f - d);
                            count++; if (count >= 6) break;
                        }
                    }
                    return count > 0 ? sep * 0.6f : Vector3.zero;
                });
                _sepCooldown = 0.2f;
            }
            _sepCache = _sepBgResult;
            var adj2 = desired + _sepCache;
            if (adj2.sqrMagnitude > 1e-6f) return adj2.normalized;
            return adj2;
        }

        private Vector3? GetWingPosition(CharacterMainControl player)
        {
            if (_leader == null || _wingIndex < 0) return null;
            Vector3 dir = player.transform.position - _leader.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = _leader.transform.forward;
            int k = _wingIndex + 1;
            int side = (k % 2 == 0) ? 1 : -1;
            int rank = (k - 1) / 2;
            var pref = _store.GetLeaderPref(_leader.GetInstanceID());
            float angle = pref.sideAngle;
            float baseR = pref.baseRadius;
            float spacing = pref.spacing;
            Vector3 sideDir = Quaternion.AngleAxis(side * angle, Vector3.up) * dir.normalized;
            float dist = baseR + spacing * rank;
            Vector3 target = _leader.transform.position + sideDir * dist - dir.normalized * 1.0f;
            return target;
        }

        private Vector3 _searchPoint;
        private float _searchTimer;

        private float GetPreferredDistance()
        {
            var gun = _c.GetGun();
            if (gun == null) return 12f;
            float d = gun.BulletDistance;
            float basePref = Mathf.Clamp(d * 0.4f, 8f, 18f);
            
            // Adapt to Player Weapon
            var player = CharacterMainControl.Main;
            if (player != null)
            {
                var pGun = player.GetGun();
                if (pGun != null)
                {
                    float pRange = pGun.BulletDistance;
                    if (pRange < 25f) basePref += 6f; // Avoid Shotgun/SMG CQC
                    else if (pRange > 70f) basePref -= 5f; // Rush Snipers
                }
            }

            float heat = _store.GetHeatAt(_c.transform.position, Time.time, 8f);
            return Mathf.Clamp(basePref + heat * 3f, 6f, 25f);
        }

        private Vector3 PredictAim(CharacterMainControl player)
        {
            var gun = _c.GetGun();
            // Aim at chest/head height approx 1.35m up
            Vector3 target = player.transform.position + Vector3.up * 1.35f;
            
            if (gun != null)
            {
                Vector3 origin = _c.transform.position + Vector3.up * 1.5f;
                Vector3 to = target - origin;
                float distance = to.magnitude;
                float speed = gun.BulletSpeed;
                
                // Cap prediction time to avoid shooting wildly ahead
                float t = Mathf.Clamp(distance / Mathf.Max(1f, speed), 0.0f, 0.5f);
                
                // Predict based on velocity
                target += player.Velocity * t;
            }
            return target;
        }

        private Vector3 TryUnstuck(Vector3 desired)
        {
            var pos = _c.transform.position;
            float moved = (pos - _prevPos).magnitude;
            _prevPos = pos;
            if (desired.sqrMagnitude < 0.04f) { _stuckTimer = 0f; return desired; }
            
            // Check if we are physically moving in the desired direction
            // If we are pushing into a wall, our velocity will be low even if input is high
            if (moved < 0.02f)
            {
                _stuckTimer += Time.deltaTime;
                if (_stuckTimer > 0.4f)
                {
                    // Smart Unstuck: Raycast to find open space
                    var fwd = desired.normalized;
                    var right = Vector3.Cross(Vector3.up, fwd);
                    var mask = GameplayDataSettings.Layers.wallLayerMask | GameplayDataSettings.Layers.halfObsticleLayer;
                    
                    Vector3 bestDir = Vector3.zero;
                    
                    // Check Left 45, Right 45, Left 90, Right 90
                    Vector3[] checks = new Vector3[] { 
                        (fwd - right).normalized, 
                        (fwd + right).normalized, 
                        -right, 
                        right 
                    };
                    
                    foreach(var dir in checks) {
                        if (!Physics.Raycast(pos + Vector3.up, dir, 1.5f, mask)) {
                            bestDir = dir;
                            break;
                        }
                    }
                    
                    if (bestDir != Vector3.zero) return bestDir;

                    // Fallback to random bounce if trapped
                    var alt = Quaternion.AngleAxis(Random.value > 0.5f ? 110f : -110f, Vector3.up) * fwd;

                    if (Random.value < 0.3f && _dashCooldown <= 0f)
                    {
                        _c.Dash(); _dashCooldown = Random.Range(2.0f, 3.5f);
                    }
                    return alt;
                }
            }
            else
            {
                _stuckTimer = 0f;
            }
            return desired;
        }

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

        

        private void CoordinateFlank(Vector3 dirToPlayer)
        {
            var all = Object.FindObjectsOfType<BloodMoonAIController>();
            int left = 0, right = 0;
            foreach (var o in all)
            {
                if (o == null || o == this) continue;
                var delta = o._c.transform.position - _c.transform.position;
                delta.y = 0f;
                var cross = Vector3.Cross(dirToPlayer.normalized, delta.normalized).y;
                if (cross > 0f) right++; else left++;
            }
            _strafeDir = (right <= left) ? 1 : -1;
        }

        private void ManageSkillUse(bool hasLoS, float dist)
        {
            _skillCooldown -= Time.deltaTime;
            if (_skillUsing)
            {
                _skillAimTimer -= Time.deltaTime;
                if (_skillAimTimer <= 0f)
                {
                    _c.ReleaseSkill(SkillTypes.itemSkill);
                    _skillUsing = false;
                    _skillCooldown = Random.Range(5f, 9f);
                    // 回到枪械
                    var prim = _c.PrimWeaponSlot()?.Content;
                    if (prim != null) _c.ChangeHoldItem(prim);
                }
                else if (_skillAimTimer < -1.0f)
                {
                    // 兜底取消，防止卡住
                    _c.CancleSkill();
                    _skillUsing = false;
                    var prim2 = _c.PrimWeaponSlot()?.Content;
                    if (prim2 != null) _c.ChangeHoldItem(prim2);
                    _skillCooldown = Random.Range(5f, 9f);
                }
                return;
            }
            if (_skillCooldown > 0f) return;
            
            bool knownPos = _store.LastSeenTime > 0f && Time.time - _store.LastSeenTime < 8.0f;
            if (!hasLoS && !knownPos) return;
            
            // If we don't have LoS, use known position distance
            if (!hasLoS) dist = Vector3.Distance(_c.transform.position, _store.LastKnownPlayerPos);

            if (dist < 6f || dist > 25f) return;
            
            var inv = _c.CharacterItem?.Inventory; if (inv == null) return;
            Item? best = null; SkillBase? bestSkill = null; float bestScore = -1f;
            foreach (var item in inv)
            {
                if (item == null) continue;
                var skillSetting = item.GetComponent<ItemSetting_Skill>();
                if (skillSetting != null && skillSetting.Skill != null)
                {
                    var ctx = skillSetting.Skill.SkillContext;
                    bool grenade = ctx.isGrenade;
                    if (!hasLoS && !grenade) continue; // Only grenades can be used without LoS

                    float cast = ctx.castRange;
                    float score = (grenade ? 1.0f : 0.6f) * Mathf.Clamp01(1f - Mathf.Abs(dist - cast) / Mathf.Max(6f, cast));
                    if (score > bestScore)
                    {
                        bestScore = score; best = item; bestSkill = skillSetting.Skill;
                    }
                }
            }
            if (best != null && bestSkill != null)
            {
                _c.ChangeHoldItem(best);
                _c.SetSkill(SkillTypes.itemSkill, bestSkill, bestSkill.gameObject);
                if (_c.StartSkillAim(SkillTypes.itemSkill))
                {
                    var running = _c.skillAction.CurrentRunningSkill;
                    if (running != null && running.SkillContext.releaseOnStartAim)
                    {
                        _c.ReleaseSkill(SkillTypes.itemSkill);
                        _skillCooldown = Random.Range(5f, 9f);
                        var prim = _c.PrimWeaponSlot()?.Content; if (prim != null) _c.ChangeHoldItem(prim);
                    }
                    else
                    {
                        float rt = (running != null) ? running.SkillContext.skillReadyTime : 0.6f;
                        _skillAimTimer = Mathf.Max(0.3f, rt);
                        _skillUsing = true;
                    }
                }
                else
                {
                    _c.CancleSkill();
                    var prim = _c.PrimWeaponSlot()?.Content; if (prim != null) _c.ChangeHoldItem(prim);
                    _skillCooldown = Random.Range(3f, 6f);
                }
            }
        }

        private void ManageHealing()
        {
            var h = _c.Health; if (h == null) return;
            if (h.CurrentHealth < h.MaxHealth * 0.45f)
            {
                var player = CharacterMainControl.Main; if (player == null) return;
                bool los = HasLineOfSight(player);
                var d = player.transform.position - _c.transform.position; d.y = 0f;
                float dist = d.magnitude;
                if (!los || dist > 16f)
                {
                    var inv = _c.CharacterItem?.Inventory; if (inv == null) return;
                    foreach (var item in inv)
                    {
                        if (item == null) continue;
                        if (item.GetComponent<Drug>() != null)
                        {
                            _c.UseItem(item);
                            break;
                        }
                    }
                }
            }
        }

        private void UpdateState(bool hasLoS, float dist)
        {
            
            var h = _c.Health;
            var gun = _c.GetGun();
            bool lowHp = h != null && h.CurrentHealth < h.MaxHealth * 0.45f;
            bool lowAmmo = gun != null && gun.BulletCount <= Mathf.Max(1, gun.Capacity / 5);
            if (lowHp)
            {
                _state = AIState.heal;
                return;
            }
            if (lowAmmo || (gun != null && gun.IsReloading()))
            {
                _state = AIState.reload;
                if (!_movingToCover && FindCover(CharacterMainControl.Main, out _targetCoverPos))
                {
                    _movingToCover = true;
                }
                return;
            }
            if (!hasLoS || dist > GetPreferredDistance() + 4f || dist < 3.5f)
            {
                _state = AIState.reposition;
                return;
            }
            _state = AIState.engage;
        }

        private void ManagePeek(CharacterMainControl player, bool hasLoS, float dist)
        {
            bool nearCover = false;
            if (_lastCoverPos != Vector3.zero && Vector3.Distance(_c.transform.position, _lastCoverPos) < 1.8f)
            {
                nearCover = true;
            }
            else if (_targetCoverPos != Vector3.zero && !_movingToCover && Vector3.Distance(_c.transform.position, _targetCoverPos) < 1.8f)
            {
                nearCover = true;
            }
            if (!nearCover)
            {
                _peeking = false;
                return;
            }
            if (_peeking)
            {
                _peekTimer -= Time.deltaTime;
                if (_peekTimer <= 0f)
                {
                    _peeking = false;
                    _coverCooldown = Random.Range(1.2f, 2.2f);
                    _peekMonitorActive = true;
                    _peekMonitorStartTime = Time.time;
                }
                return;
            }
                if (!hasLoS && _coverCooldown <= 0f)
                {
                    if (Random.value < (_store.GetWeight("peek_prob", 0.2f) * GetPressureFactor()))
                    {
                        _peeking = true;
                        _peekTimer = Random.Range(0.8f, 1.5f);
                    }
                    return;
                }
            if (hasLoS && Random.value < (_store.GetWeight("peek_prob", 0.2f) * GetPressureFactor()))
            {
                _peeking = true;
                _peekTimer = Random.Range(0.4f, 0.9f);
            }
        }
        private void OnDestroy()
        {
            _all.Remove(this);
        }
    }
}
