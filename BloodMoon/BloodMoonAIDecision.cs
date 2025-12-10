using UnityEngine;
using Duckov;
using Duckov.Utilities;
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

    public class Action_Heal : AIAction
    {
        public Action_Heal() { Name = "Heal"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null) return 0f;
            float healthPct = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Critical heal: Very low HP
            if (healthPct < 0.3f) return 0.95f;
            
            // Tactical heal: Medium HP and safe
            if (healthPct < 0.6f && (!ctx.HasLoS || ctx.Pressure < 0.5f)) return 0.75f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformHeal();
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
            
            // If high pressure or target is hiding, boost score
            if (!ctx.HasLoS || ctx.Pressure > 2f) return 0.82f;
            
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
                if (engaging > 2) return 0.7f; // High priority to flank if crowded
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
            // Simple flank: Move 45 degrees to the side of the target direction
            var dir = ctx.DirToTarget;
            // Randomize flank direction
            float angle = (ctx.Character.GetInstanceID() % 2 == 0) ? 45f : -45f;
            var flankDir = Quaternion.AngleAxis(angle, Vector3.up) * dir;
            var target = ctx.Character.transform.position + flankDir * 10f;
            ctx.Controller?.MoveTo(target);
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
