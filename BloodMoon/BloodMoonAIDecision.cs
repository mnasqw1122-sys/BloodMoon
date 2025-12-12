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

        public void UpdateSensors()
        {
            if (Character == null || CharacterMainControl.Main == null) return;
            Target = CharacterMainControl.Main;
            
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
        
        public abstract float Evaluate(AIContext ctx);
        public abstract void Execute(AIContext ctx);
        public virtual void OnEnter(AIContext ctx) { }
        public virtual void OnExit(AIContext ctx) { }
        
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
            
            // If already committed to healing, keep going unless critical
            if (ctx.Controller.IsHealing) return 0.95f;

            // Optimization: Early exit if no meds
            if (!ctx.Controller.HasHealingItem()) return 0f;

            float healthPct = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Dynamic Logic: Score increases as health drops
            // Curve: (1 - HP)^1.5
            // 90% HP -> 0.03
            // 50% HP -> 0.35
            // 20% HP -> 0.71
            // 10% HP -> 0.85
            float urgency = Mathf.Pow(1.0f - healthPct, 1.5f);
            
            // Modifiers
            
            // 1. Safety Bonus: If hidden, take opportunity to heal
            if (!ctx.HasLoS) urgency += 0.25f;
            
            // 2. Pressure Penalty: If under fire, prioritize fighting/retreating unless desperate
            // Bosses are less affected by pressure
            float pressurePenalty = ctx.Controller.IsBoss ? 0.05f : 0.15f;
            if (ctx.Pressure > 1.5f) urgency -= pressurePenalty;
            
            // 3. Personality Variation (Randomness)
            // Use ID to create a fixed random offset for this agent
            float personality = (ctx.Character.GetInstanceID() % 100) / 500.0f; // 0.0 to 0.2
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
            // If no LoS, but we have a recent position, and we have ammo
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f && (Time.time - ctx.LastSeenTime < 3f) && !ctx.IsLowAmmo)
            {
                // If we are relatively safe
                if (ctx.Pressure < 2f) return 0.6f;
                
                // SQUAD TACTIC: Support flanking allies
                if (ctx.Store != null && ctx.Target != null)
                {
                    // If others are flanking (no direct API yet, but if engagement is high)
                    if (ctx.Store.GetEngagementCount(ctx.Target) > 1) return 0.75f;
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
            if (ctx.HasLoS && ctx.DistToTarget < 30f)
            {
                // Base score
                float score = 0.5f;
                // Bonus if we are safe (low pressure)
                if (ctx.Pressure < 1.0f) score += 0.2f;
                // Bonus if target is close
                if (ctx.DistToTarget < 15f) score += 0.1f;
                
                // Bosses are more aggressive
                if (ctx.Controller != null && ctx.Controller.IsBoss) score += 0.15f;
                
                return score;
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

    public class Action_Flank : AIAction
    {
        public Action_Flank() { Name = "Flank"; }
        public override float Evaluate(AIContext ctx)
        {
            // If we have LoS but are far away, or if we are blocked
            if (ctx.HasLoS && ctx.DistToTarget > 20f && ctx.Pressure < 1.5f)
            {
                return 0.45f;
            }
            
            // SQUAD TACTIC: If too many people engaging, flank instead
            if (ctx.Store != null && ctx.Target != null)
            {
                int engaging = ctx.Store.GetEngagementCount(ctx.Target);
                // Dynamic threshold based on boss presence
                int threshold = (ctx.Controller != null && ctx.Controller.IsBoss) ? 3 : 2;
                if (engaging > threshold) return 0.85f; // Increased priority to force flanking when crowded
            }

            // If no LoS but we know where they are, maybe flank instead of chase?
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f)
            {
                // 50/50 chance to flank vs chase if not urgent
                return 0.3f;
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
