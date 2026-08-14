using System.Collections.Generic;
using UnityEngine;
using BloodMoon.Utils;
using Duckov;

namespace BloodMoon.AI
{
    public class ContextAwareDecisionMaker : NeuralDecisionMaker
    {
        private struct AIState
        {
            public bool HasPrimaryWeapon;
            public bool HasSecondaryWeapon;
            public bool HasMeleeWeapon;
            public bool HasThrowable;
            public int AmmoCount;
            public float HealthPercentage;
            public bool IsStuck;
            public bool IsInCombat;
            public float DistanceToTarget;
        }

        private AIState _currentState;

        public ContextAwareDecisionMaker(List<string> actionNames) : base(actionNames)
        {
        }
        
        public override Dictionary<string, float> GetActionScores(AIContext ctx)
        {
            UpdateAIState(ctx);
            
            var baseScores = base.GetActionScores(ctx);
            var adjustedScores = new Dictionary<string, float>();
            
            foreach (var kvp in baseScores)
            {
                float adjustedScore = AdjustScoreByContext(kvp.Value, kvp.Key, ctx);
                adjustedScores[kvp.Key] = adjustedScore;
            }
            
            EnforceContextConstraints(adjustedScores, ctx);
            
            return adjustedScores;
        }
        
        private void UpdateAIState(AIContext ctx)
        {
            _currentState = new AIState
            {
                HasPrimaryWeapon = ctx.PrimaryWeapon != null,
                HasSecondaryWeapon = ctx.SecondaryWeapon != null,
                HasMeleeWeapon = ctx.MeleeWeapon != null,
                HasThrowable = ctx.ThrowableWeapon != null,
                AmmoCount = ctx.AmmoCount,
                HealthPercentage = ctx.HealthPercentage,
                IsStuck = ctx.IsStuck,
                IsInCombat = ctx.IsInCombat,
                DistanceToTarget = ctx.DistToTarget
            };
        }
        
        private float AdjustScoreByContext(float baseScore, string action, AIContext ctx)
        {
            float multiplier = 1.0f;
            
            if (action == "Reload" || action == "Shoot" || action == "Suppress")
            {
                if (!_currentState.HasPrimaryWeapon && !_currentState.HasSecondaryWeapon)
                {
                    multiplier = 0.1f;
                }
                else if (_currentState.AmmoCount == 0)
                {
                    if (action == "Reload") multiplier = 1.5f;
                    else multiplier = 0.3f;
                }
            }
            
            if (action == "Heal" || action == "Retreat" || action == "Panic")
            {
                if (_currentState.HealthPercentage < 0.3f)
                {
                    if (action == "Heal") multiplier = 2.0f;
                    if (action == "Retreat") multiplier = 1.8f;
                    if (action == "Panic") multiplier = 1.5f;
                }
                else if (_currentState.HealthPercentage > 0.8f)
                {
                    multiplier = 0.3f;
                }
            }
            
            if (action == "Unstuck")
            {
                multiplier = _currentState.IsStuck ? 1.5f : 0.2f;
            }
            
            if (action == "Engage" || action == "Flank" || action == "TakeCover")
            {
                multiplier = _currentState.IsInCombat ? 1.2f : 0.7f;
            }
            
            return baseScore * multiplier;
        }
        
        private void EnforceContextConstraints(Dictionary<string, float> scores, AIContext ctx)
        {
            if (!_currentState.HasPrimaryWeapon && !_currentState.HasSecondaryWeapon)
            {
                string[] weaponActions = { "Reload", "Shoot", "Suppress", "ThrowGrenade" };
                foreach (var action in weaponActions)
                {
                    if (scores.ContainsKey(action))
                    {
                        scores[action] = 0.0f;
                    }
                }
            }
            
            if (!_currentState.HasMeleeWeapon)
            {
            }
            
            if (_currentState.HealthPercentage > 0.9f)
            {
                if (scores.ContainsKey("Heal"))
                {
                    scores["Heal"] *= 0.1f;
                }
            }
        }
    }
}
