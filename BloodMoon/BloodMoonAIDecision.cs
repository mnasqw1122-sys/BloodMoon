using UnityEngine;
using Duckov;
using Duckov.Utilities;
using Duckov.ItemUsage;
using System.Collections.Generic;

namespace BloodMoon
{
    // Context object to pass data between Controller and Actions
    public class AIContext
    {
        public CharacterMainControl? Character;
        public BloodMoonAIController? Controller;
        public AIDataStore? Store;
        public CharacterMainControl? Target;
        
        // Target Persistence
        private float _targetSwitchCooldown;
        private const float MIN_TARGET_SWITCH_TIME = 3.0f; // Minimum time to stick to a target
        private float _timeWithCurrentTarget;
        
        // Sensory Data
        public float DistToTarget;
        public Vector3 DirToTarget;
        public bool HasLoS;
        public float LastSeenTime;
        public Vector3 LastKnownPos;
        public float Pressure; // 0 to 10+
        
        // State Flags
        public bool IsHurt;
        public bool IsLowAmmo;
        public bool IsReloading;
        public bool IsStuck;
        public bool CanChase;
        
        // Target State
        public bool TargetIsReloading;
        public bool TargetIsHealing;

        public bool IsBoss => Controller != null && Controller.IsBoss;
        public bool IsRaged => Controller != null && Controller.IsRaged;

        // Static Cache for Target Acquisition
        private static List<CharacterMainControl> _cachedCharacters = new List<CharacterMainControl>();
        private static float _lastCacheTime;

        private static void CacheAllCharacters()
        {
            if (Time.time - _lastCacheTime < 2.0f) return; // Increased from 1.0f to 2.0f
            _lastCacheTime = Time.time;
            
            // Optimized: Only get characters in active scenes
            _cachedCharacters.Clear();
            var allCharacters = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>();
            
            // Filter out characters in inactive scenes or not in raid map
            foreach (var c in allCharacters)
            {
                if (c != null && c.gameObject.activeInHierarchy)
                {
                    _cachedCharacters.Add(c);
                }
            }
        }

        public void UpdateSensors()
        {
            if (Character == null) return;

            // Update target persistence timers
            if (_targetSwitchCooldown > 0f)
            {
                _targetSwitchCooldown -= Time.deltaTime;
            }

            if (Target != null)
            {
                _timeWithCurrentTarget += Time.deltaTime;
            }
            else
            {
                _timeWithCurrentTarget = 0f;
            }

            // 1. Update Global Cache periodically (Shared by all AI)
            // Only update cache if we don't have a valid target
            // This reduces expensive operations when AI already has a target
            if (Target == null || Time.time - LastSeenTime > 5f)
            {
                CacheAllCharacters();
            }

            // 2. Select Best Target
            CharacterMainControl? bestTarget = null;
            float bestScore = -99999f;
            Vector3 myPos = Character.transform.position;
            CharacterMainControl? currentTarget = Target;

            // Scan for other hostile targets
            if (_cachedCharacters != null)
            {
                var myTeam = Character.Team;
                int count = _cachedCharacters.Count;
                
                // Ensure Player is considered (sometimes might be missed if cache is stale or filter issues)
                if (CharacterMainControl.Main != null && !_cachedCharacters.Contains(CharacterMainControl.Main))
                {
                    _cachedCharacters.Add(CharacterMainControl.Main);
                    count++;
                }

                for(int i=0; i<count; i++)
                {
                    var c = _cachedCharacters[i];
                    if (c == null || c == Character) continue;
                    if (c.Health.CurrentHealth <= 0) continue;
                    
                    // Hostility Logic: Attack different teams
                    if (c.Team != myTeam) 
                    {
                        // Additional check: Ensure target is not a BloodMoon AI (same mod)
                        // This prevents friendly fire between our mod's enemies
                        bool isBloodMoonAI = c.GetComponent<BloodMoonAIController>() != null;
                        if (isBloodMoonAI) continue;
                        
                        float dist = Vector3.Distance(c.transform.position, myPos);
                        float score = 1000f - dist; // Base Score: Closer is better

                        // --- Priority Rules ---
                        
                        // 1. Player Priority (Huge Bonus)
                        // If player is alive, we almost ALWAYS want to target them unless they are very far away
                        if (c == CharacterMainControl.Main)
                        {
                            score += 500f; // Equivalent to being 500m closer
                        }

                        // 2. Target Stickiness (Enhanced Hysteresis)
                        // Prevent rapid switching between targets of similar value
                        if (c == currentTarget)
                        {
                            // Base stickiness bonus
                            float stickinessBonus = 25f; // Increased from 15f
                            
                            // Additional bonus based on time spent with target
                            stickinessBonus += Mathf.Min(30f, _timeWithCurrentTarget * 2f); // Up to 30f bonus for 15s with target
                            
                            score += stickinessBonus;
                        }
                        // Penalty for switching targets too soon
                        else if (currentTarget != null && _targetSwitchCooldown > 0f)
                        {
                            // Reduce score by a significant amount if we're still in cooldown
                            score -= 100f;
                        }
                        
                        // 3. Distance Cap for "Ignoring" Player
                        // If player is extremely far (>150m) and there is a native enemy right here (<10m), 
                        // the native enemy might score higher:
                        // Player Score: 1000 - 150 + 500 = 1350
                        // Native Score: 1000 - 10 = 990
                        // Result: Still Player. 
                        // Let's adjust. If we really want to kill native enemies when player is absent or super far, 
                        // the player bonus handles most cases.
                        // But if player is present, we WANT to ignore native enemies mostly.
                        
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTarget = c;
                        }
                    }
                }
            }

            // Fallback: If no target found, but player is alive, target player
            if (bestTarget == null && CharacterMainControl.Main != null && CharacterMainControl.Main.Health.CurrentHealth > 0)
            {
                 // Only if player is not ally
                 if (CharacterMainControl.Main.Team != Character.Team)
                    bestTarget = CharacterMainControl.Main;
            }

            // Check if target is changing
            if (bestTarget != currentTarget)
            {
                // Set cooldown only if we're switching from a valid target
                if (currentTarget != null)
                {
                    _targetSwitchCooldown = MIN_TARGET_SWITCH_TIME;
                }
                _timeWithCurrentTarget = 0f;
            }

            Target = bestTarget;
            if (Target == null) return;
            
            var diff = Target.transform.position - Character.transform.position;
            diff.y = 0f;
            DistToTarget = diff.magnitude;
            DirToTarget = diff.normalized;
            
            // Basic LoS Check (Controller should implement the detailed one)
            if (Controller != null)
            {
                HasLoS = Controller.CheckLineOfSight(Target);
            }
            else
            {
                HasLoS = false;
            }
            
            if (HasLoS)
            {
                LastSeenTime = Time.time;
                LastKnownPos = Target.transform.position;
            }
            
            // Update Pressure
            Pressure = Mathf.Max(0f, Pressure - Time.deltaTime * 0.5f);
            
            // Check Health
            IsHurt = Character.Health.CurrentHealth < Character.Health.MaxHealth * 0.4f;
            
            // Check Ammo
            var gun = Character.GetGun();
            IsLowAmmo = gun != null && gun.BulletCount < (gun.Capacity * 0.2f);
            IsReloading = gun != null && gun.IsReloading();
            
            // Check Target State
            var tGun = Target.GetGun();
            TargetIsReloading = tGun != null && tGun.IsReloading();
            TargetIsHealing = Target.CurrentHoldItemAgent != null && Target.CurrentHoldItemAgent.Item.GetComponent<Drug>() != null; // Simplified check

            // Check State
            if (Controller != null)
            {
                CanChase = Controller.CanChase;
            }
        }
    }

    public abstract class AIAction
    {
        public string Name = string.Empty;
        protected float _score;
        protected float _cooldown;
        
        // Behavior persistence
        protected float _timeInAction;
        protected float _minActionDuration;
        protected float _maxActionDuration;
        protected bool _shouldExit;
        
        public abstract float Evaluate(AIContext ctx);
        public abstract void Execute(AIContext ctx);
        public virtual void OnEnter(AIContext ctx) 
        { 
            _timeInAction = 0f;
            _shouldExit = false;
            // Set default min/max durations based on action type
            _minActionDuration = 1.0f;
            _maxActionDuration = 10.0f;
        }
        public virtual void OnExit(AIContext ctx) 
        { 
            _timeInAction = 0f;
            _shouldExit = false;
        }
        
        // Update action time and check if we should continue
        public void UpdateActionTime(float dt)
        {
            _timeInAction += dt;
        }
        
        // Check if action can be interrupted
        public bool CanBeInterrupted()
        {
            return _timeInAction > _minActionDuration;
        }
        
        // Check if action should automatically exit
        public bool ShouldExit()
        {
            return _shouldExit || _timeInAction > _maxActionDuration;
        }
        
        public bool IsCoolingDown() 
        { 
            return _cooldown > 0f; 
        }
        
        public void UpdateCooldown(float dt)
        {
            if (_cooldown > 0f) _cooldown -= dt;
        }
    }

    // --- Concrete Actions ---

    public class Action_BossCommand : AIAction
    {
        public Action_BossCommand() { Name = "BossCommand"; }
        public override float Evaluate(AIContext ctx)
        {
            if (!ctx.IsBoss || IsCoolingDown()) return 0f;
            
            // Trigger if we have allies nearby (Controller handles check)
            // Or just periodically if in combat
            if (ctx.HasLoS || ctx.Pressure > 1f)
            {
                return 0.65f;
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller != null && ctx.Controller.PerformBossCommand())
            {
                _cooldown = 20f;
            }
            else
            {
                _cooldown = 5f; // Retry sooner if failed (no allies)
            }
        }
    }

    public class Action_Heal : AIAction
    {
        public Action_Heal() { Name = "Heal"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Controller == null) return 0f;
            
            // If already committed to healing, but being attacked, lower priority
            if (ctx.Controller.IsHealing)
            {
                // If being attacked (high pressure or low health), allow interruption
                if (ctx.Pressure > 3.0f || ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth < 0.3f)
                {
                    return 0.5f; // Lower priority to allow attack actions
                }
                return 0.8f; // Lowered from 0.98f to allow interruption
            }

            // Optimization: Early exit if no meds
            if (!ctx.Controller.HasHealingItem()) return 0f;

            float healthPct = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Linear increase as health drops
            float urgency = (1.0f - healthPct); // e.g., 0.4 at 60% HP
            
            // 1. Critical Health Boost: Force heal if dying
            if (healthPct < 0.5f) urgency += 0.2f;
            if (healthPct < 0.25f) urgency += 0.4f;

            // 2. Safety Bonus: If hidden, take opportunity to heal
            if (!ctx.HasLoS) urgency += 0.2f;
            
            // 3. Pressure Penalty: Increased penalty when being attacked
            // Bosses are less affected by pressure
            float pressurePenalty = ctx.Controller.IsBoss ? 0.1f : 0.3f;
            if (ctx.Pressure > 1.0f) urgency -= pressurePenalty;
            if (ctx.Pressure > 3.0f) urgency -= pressurePenalty * 2f; // Double penalty when under heavy fire
            
            // 4. Personality Variation (Randomness)
            // Use ID to create a fixed random offset for this agent
            float personality = (ctx.Character.GetInstanceID() % 100) / 1000.0f; // 0.0 to 0.1
            urgency += personality; 
            
            return Mathf.Clamp01(urgency);
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformHeal();
        }
    }

    public class Action_Rush : AIAction
    {
        public Action_Rush() { Name = "Rush"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Target == null || ctx.Character == null) return 0f;
            
            // Don't rush if low health (unless raged boss)
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            if (hp < 0.4f && !ctx.IsRaged) return 0f;

            // Only rush if target is vulnerable
            if (ctx.TargetIsReloading || ctx.TargetIsHealing)
            {
                // And we are relatively close
                if (ctx.DistToTarget < 20f && ctx.HasLoS)
                {
                    // Bosses love to punish
                    if (ctx.IsBoss) 
                    {
                         if (ctx.IsRaged) return 0.98f; // Raged boss almost always rushes vulnerable target
                         return 0.9f;
                    }
                    return 0.75f;
                }
            }
            
            // Rage Mode Rush (even if target not vulnerable)
            if (ctx.IsRaged && ctx.HasLoS && ctx.DistToTarget < 15f)
            {
                return 0.85f;
            }

            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformRush();
        }
    }

    public class Action_Reload : AIAction
    {
        public Action_Reload() { Name = "Reload"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.IsReloading) return 0.9f; // Keep reloading if started
            if (ctx.Character == null) return 0f;
            var gun = ctx.Character.GetGun();
            if (gun == null) return 0f;
            
            if (gun.BulletCount == 0) return 0.92f; // Empty -> Must reload
            
            // Tactical reload
            if (ctx.IsLowAmmo && !ctx.HasLoS) return 0.7f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            // If we have cover and are reloading, move to cover?
            // Handled by PerformReload which might trigger cover logic
            ctx.Controller?.PerformReload();
        }
    }

    public class Action_ThrowGrenade : AIAction
    {
        public Action_ThrowGrenade() { Name = "ThrowGrenade"; }
        public override float Evaluate(AIContext ctx)
        {
            if (IsCoolingDown()) return 0f;
            if (ctx.Character == null || ctx.Target == null || ctx.Controller == null) return 0f;
            
            // Only throw if target is in cover or we haven't seen them move much
            // For now, simple check: distance 8-25m, and we have a grenade
            if (ctx.DistToTarget < 8f || ctx.DistToTarget > 25f) return 0f;
            
            if (!ctx.Controller.HasGrenade()) return 0f;
            
            // If target has been stationary (camping) or we have lost LoS recently but know position
            bool isCamping = Vector3.Distance(ctx.Target.transform.position, ctx.LastKnownPos) < 2.0f && (Time.time - ctx.LastSeenTime < 5f);
            
            // If high pressure or target is hiding, boost score
            if ((!ctx.HasLoS || isCamping) && ctx.Pressure > 1f) return 0.82f;
            
            return 0.4f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller == null) return;
            if (ctx.Controller.PerformThrowGrenade())
            {
                _cooldown = 15f;
            }
            else
            {
                _cooldown = 2f;
            }
        }
    }

    public class Action_Retreat : AIAction
    {
        public Action_Retreat() { Name = "Retreat"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null) return 0f;
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Bosses don't retreat easily
            if (ctx.Controller != null && ctx.Controller.IsBoss)
            {
                if (hp < 0.15f) return 0.5f; // Only when critical
                return 0f;
            }

            // If dying and under pressure
            if (hp < 0.3f && ctx.Pressure > 2.0f) return 0.95f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformRetreat();
        }
    }

    public class Action_TakeCover : AIAction
    {
        public Action_TakeCover() { Name = "TakeCover"; }
        public override float Evaluate(AIContext ctx)
        {
            if (IsCoolingDown()) return 0f;
            
            // High pressure -> Cover
            if (ctx.Pressure > 2.0f) return 0.85f;
            
            // Reloading or Healing in open -> Cover
            if ((ctx.IsReloading || ctx.IsHurt) && ctx.HasLoS) return 0.88f;
            
            // Just general caution
            if (ctx.HasLoS && ctx.DistToTarget < 10f) return 0.6f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller == null) return;
            if (ctx.Controller.MoveToCover())
            {
                // If successful, set cooldown so we don't spam cover search
                _cooldown = 2.0f;
            }
            else
            {
                // Failed to find cover? Panic/Drop score
                _cooldown = 1.0f; 
            }
        }
    }

    public class Action_Suppress : AIAction
    {
        public Action_Suppress() { Name = "Suppress"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Target == null) return 0f;
            
            // Cannot suppress without ammo
            if (ctx.IsLowAmmo || ctx.IsReloading) return 0f;

            // 1. SQUAD TACTIC: Support Flankers
            // If allies are flanking (we can infer or check store), we should suppress
            if (ctx.Store != null)
            {
                // Check if anyone else is engaging?
                int engaging = ctx.Store.GetEngagementCount(ctx.Target);
                // If we are part of a group (2+), one should suppress
                // Simple role assignment: ID based or Random?
                // Let's use distance: Farthest ally suppresses
                if (engaging > 0 && ctx.DistToTarget > 20f && ctx.HasLoS)
                {
                    return 0.7f;
                }
            }

            // 2. Blind Fire: If no LoS but recent contact
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f && (Time.time - ctx.LastSeenTime < 4f))
            {
                // Only if we have plenty of ammo
                var gun = ctx.Character.GetGun();
                if (gun != null && gun.BulletCount > gun.Capacity * 0.5f)
                {
                     return 0.65f;
                }
            }
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformSuppression();
        }
    }

    public class Action_Engage : AIAction
    {
        public Action_Engage() { Name = "Engage"; }
        public override float Evaluate(AIContext ctx)
        {
            float engageDist = 30f;
            if (ctx.Character != null)
            {
                var gun = ctx.Character.GetGun();
                if (gun != null) engageDist = Mathf.Max(30f, gun.BulletDistance * 0.8f);
            }

            if (ctx.HasLoS && ctx.DistToTarget < engageDist)
            {
                // Smooth engagement score based on distance
                float distFactor = Mathf.Clamp01((engageDist - ctx.DistToTarget) / engageDist);
                
                // Base score with smooth distance-based weighting
                float score = 0.3f + (distFactor * 0.4f); // Range: 0.3 to 0.7
                
                // Bonus if we are safe (low pressure) - smooth transition
                float pressureFactor = Mathf.Clamp01((3.0f - ctx.Pressure) / 3.0f);
                score += pressureFactor * 0.2f;
                
                // Bonus if target is close - smooth transition
                float closeRangeBonus = Mathf.Clamp01((15f - ctx.DistToTarget) / 15f);
                score += closeRangeBonus * 0.1f;
                
                // Bosses are more aggressive - consistent bonus
                if (ctx.Controller != null && ctx.Controller.IsBoss) score += 0.15f;
                
                // Health Logic Update:
                float hp = ctx.Character != null ? ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth : 1f;
                
                // If critical health AND enemy is close -> Desperate Fight (Cornered Beast)
                if (hp < 0.3f && ctx.DistToTarget < 10f)
                {
                    // Smooth desperate fight bonus based on health and distance
                    float desperateFactor = Mathf.Clamp01((0.3f - hp) / 0.3f) * Mathf.Clamp01((10f - ctx.DistToTarget) / 10f);
                    score += desperateFactor * 0.4f; // Fight for your life!
                }
                // Only penalize engagement if we are hurt but have distance (so we might retreat/heal)
                else if (hp < 0.4f && ctx.DistToTarget > 15f) 
                {
                    float retreatFactor = Mathf.Clamp01((0.4f - hp) / 0.4f) * Mathf.Clamp01((ctx.DistToTarget - 15f) / 15f);
                    score -= retreatFactor * 0.15f;
                }

                return Mathf.Clamp01(score);
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Store != null && ctx.Target != null && ctx.Character != null)
                ctx.Store.RegisterEngagement(ctx.Target, ctx.Character);
            ctx.Controller?.EngageTarget();
        }
        public override void OnExit(AIContext ctx)
        {
            if (ctx.Store != null && ctx.Target != null && ctx.Character != null)
                ctx.Store.UnregisterEngagement(ctx.Target, ctx.Character);
        }
    }

    public class Action_Chase : AIAction
    {
        public Action_Chase() { Name = "Chase"; }
        public override float Evaluate(AIContext ctx)
        {
            if (!ctx.CanChase) return 0f;

            if (ctx.HasLoS)
            {
                 // If too far, chase
                 if (ctx.DistToTarget > 20f) return 0.6f;
                 return 0.1f; // Prefer Engage if LoS
            }
            
            // No LoS, but we have a last known position
            if (ctx.LastSeenTime > 0f && Time.time - ctx.LastSeenTime < 20f)
            {
                return 0.55f;
            }
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.MoveTo(ctx.LastKnownPos);
        }
    }

    public class Action_Patrol : AIAction
    {
        public Action_Patrol() { Name = "Patrol"; }
        public override float Evaluate(AIContext ctx)
        {
            // If we have nothing better to do (no target, not stuck)
            if (ctx.Target == null && !ctx.IsStuck)
            {
                // Patrol is the default state
                return 0.2f;
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformPatrol();
        }
    }

    public class Action_Search : AIAction
    {
        public Action_Search() { Name = "Search"; }
        public override float Evaluate(AIContext ctx)
        {
            // Boost if global intel says player is elsewhere
             if (ctx.Store != null && ctx.Store.LastKnownPlayerPos != Vector3.zero && ctx.Character != null)
             {
                  float d = Vector3.Distance(ctx.Store.LastKnownPlayerPos, ctx.Character.transform.position);
                  if (d < 40f && !ctx.HasLoS) return 0.45f;
             }

            // If we lost the target for a while
            if (!ctx.HasLoS && (Time.time - ctx.LastSeenTime > 5f))
            {
                return 0.4f;
            }
            return 0.05f; // Low priority default
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformSearch();
        }
    }

    public class Action_Panic : AIAction
    {
        public Action_Panic() { Name = "Panic"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null) return 0f;
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Panic if low HP and high pressure
            if (hp < 0.25f && ctx.Pressure > 3.0f)
            {
                 // Bosses don't panic easily
                 if (ctx.IsBoss) return 0f;
                 return 1.0f; // Override everything
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformPanic();
        }
    }

    public class Action_Flank : AIAction
    {
        public Action_Flank() { Name = "Flank"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Target == null) return 0f;

            // 1. Basic Flanking Condition: Good health, some distance
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            if (hp < 0.4f) return 0f; // Don't flank if injured

            // 2. SQUAD TACTIC: If target is suppressed or engaging someone else
            if (ctx.Store != null)
            {
                int engaging = ctx.Store.GetEngagementCount(ctx.Target);
                // If 2+ allies are engaging, or 1 ally is suppressing, we should flank
                if (engaging >= 2) return 0.75f;
            }
            
            // 3. Situational: If we have LoS but are at suboptimal range/angle
            // Check if we are "in front" of target (dangerous)
            Vector3 toMe = (ctx.Character.transform.position - ctx.Target.transform.position).normalized;
            float angle = Vector3.Angle(ctx.Target.transform.forward, toMe);
            if (angle < 45f && ctx.HasLoS && ctx.Pressure < 2f)
            {
                // We are in their face, try to flank to side
                return 0.6f;
            }

            // 4. If no LoS but we know position, flank instead of direct chase
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f && Time.time - ctx.LastSeenTime < 10f)
            {
                return 0.5f;
            }

            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Character == null) return;
            // Use tactical flank logic in controller
            ctx.Controller?.PerformTacticalFlank();
        }
    }

    public class Action_Unstuck : AIAction
    {
        public Action_Unstuck() { Name = "Unstuck"; }
        public override float Evaluate(AIContext ctx)
        {
            // If controller reports stuck
            if (ctx.IsStuck) return 1.0f; // Max priority
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformUnstuck();
        }
    }
}
