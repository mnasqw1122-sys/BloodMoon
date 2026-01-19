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
            
            // 强制约束
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
            
            // 根据武器状态进行调整
            if (action == "Reload" || action == "Shoot" || action == "Suppress")
            {
                if (!_currentState.HasPrimaryWeapon && !_currentState.HasSecondaryWeapon)
                {
                    // 如果没有武器，则大幅减少武器动作
                    multiplier = 0.1f;
                }
                else if (_currentState.AmmoCount == 0)
                {
                    if (action == "Reload") multiplier = 1.5f;
                    else multiplier = 0.3f;
                }
            }
            
            // 根据健康状况进行调整
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
            
            // 根据卡住状态进行调整
            if (action == "Unstuck")
            {
                multiplier = _currentState.IsStuck ? 1.5f : 0.2f;
            }
            
            // 根据战斗状态进行调整
            if (action == "Engage" || action == "Flank" || action == "TakeCover")
            {
                multiplier = _currentState.IsInCombat ? 1.2f : 0.7f;
            }
            
            return baseScore * multiplier;
        }
        
        private void EnforceContextConstraints(Dictionary<string, float> scores, AIContext ctx)
        {
            // 如果没有武器，则强制移除武器动作
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
            
            // 如果没有近战武器，则强制移除近战动作
            // （假设“MeleeAttack”是一个动作，尽管目前由“Engage”来处理它）
            // 但是如果我们有特定的近战动作：
            if (!_currentState.HasMeleeWeapon)
            {
                // 如果我们有一个特定的近战动作键
            }
            
            // 如果生命值较高，是否应将治疗效果降低至零，除非持有增益物品？
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
