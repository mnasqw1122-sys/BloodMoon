using System;
using System.Collections.Generic;
using UnityEngine;
using BloodMoon.Utils;

namespace BloodMoon.AI
{
    /// <summary>
    /// 神经决策器。
    /// 注意：神经网络权重从未被训练（Mutate 无调用者），旧版还把随机权重写入/读出 global_brain.json
    /// （主线程 IO + 并发写盘 + "越活越强的假学习"）。已移除全部文件读写，决策实际由规则库负责
    /// （BMAIC 中混合权重为 0），本类保留为纯内存结构。
    /// </summary>
    public class NeuralDecisionMaker
    {
        private NeuralNetwork _network = null!;
        private List<string> _actionNames;
        private bool _isInitialized = false;

        private const int INPUT_SIZE = 10;
        
        /// <summary>
        /// 动作权重字典
        /// </summary>
        private Dictionary<string, float> _actionWeights = new Dictionary<string, float>
        {
            { "Engage", 1.2f },       
            { "Chase", 1.0f },
            { "Cover", 1.5f },        
            { "Flank", 1.3f },        
            { "Retreat", 1.1f },
            { "Heal", 2.0f },         
            { "Grenade", 0.8f },      
            { "Unstuck", 0.7f },      
            { "Reload", 1.0f },
            { "BossCommand", 1.5f }   
        };

        /// <summary>
        /// 构造函数，初始化神经决策器（纯内存，不读盘）
        /// </summary>
        /// <param name="actionNames">可用的动作名称列表</param>
        public NeuralDecisionMaker(List<string> actionNames)
        {
            _actionNames = actionNames;
            CreateNewNetwork();
            _isInitialized = true;
            BloodMoon.Utils.Logger.Log($"NeuralDecisionMaker initialized with {actionNames.Count} output actions.");
        }
        
        /// <summary>
        /// 创建新的神经网络
        /// </summary>
        private void CreateNewNetwork()
        {
            _network = new NeuralNetwork(new int[] { INPUT_SIZE, 16, _actionNames.Count });
            _network.InitializeRandom();
        }

        /// <summary>
        /// 报告AI的性能表现（旧版会写 global_brain.json：随机权重被"存档为最优"+并发写盘。
        /// 已改为纯记录，不再落盘。）
        /// </summary>
        public void ReportPerformance(float survivalTime, int kills, int damageDealt)
        {
            // 保留签名（BMAIC 死亡回调调用），不做任何 IO
        }

        /// <summary>
        /// 获取动作分数
        /// </summary>
        /// <param name="ctx">AI上下文</param>
        /// <returns>动作分数字典</returns>
        public virtual Dictionary<string, float> GetActionScores(AIContext ctx)
        {
            if (!_isInitialized) return new Dictionary<string, float>();

            float[] inputs = ExtractFeatures(ctx);
            float[] outputs = _network.FeedForward(inputs);

            var scores = new Dictionary<string, float>();
            
            int minLength = Math.Min(outputs.Length, _actionNames.Count);
            for (int i = 0; i < minLength; i++)
            {
                string action = _actionNames[i];
                float baseScore = outputs[i];
                
                if (_actionWeights.TryGetValue(action, out float w))
                {
                    baseScore *= w;
                }
                
                scores[action] = baseScore;
            }
            
            ApplyContextAdjustment(scores, ctx);
            
            return scores;
        }

        /// <summary>
        /// 应用上下文调整
        /// </summary>
        /// <param name="scores">动作分数</param>
        /// <param name="ctx">AI上下文</param>
        private void ApplyContextAdjustment(Dictionary<string, float> scores, AIContext ctx)
        {
            float hpPercent = ctx.Character != null ? ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth : 1f;
            if (hpPercent < 0.3f)
            {
                if (scores.ContainsKey("Heal")) scores["Heal"] *= 2.0f;
                if (scores.ContainsKey("Retreat")) scores["Retreat"] *= 1.5f;
                if (scores.ContainsKey("Cover")) scores["Cover"] *= 1.3f;
                if (scores.ContainsKey("Engage")) scores["Engage"] *= 0.5f;
            }

            var gun = ctx.Character?.GetGun();
            if (gun != null && gun.BulletCount == 0)
            {
                if (scores.ContainsKey("Reload")) scores["Reload"] *= 2.0f;
                if (scores.ContainsKey("Engage")) scores["Engage"] *= 0.2f;
            }
        }

        /// <summary>
        /// 提取特征
        /// </summary>
        /// <param name="ctx">AI上下文</param>
        /// <returns>特征数组</returns>
        private float[] ExtractFeatures(AIContext ctx)
        {
            float[] f = new float[INPUT_SIZE];
            
            if (ctx.Character != null)
            {
                f[0] = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
                
                var gun = ctx.Character.GetGun();
                f[4] = gun != null ? (float)gun.BulletCount / gun.Capacity : 0f;
                f[5] = ctx.IsReloading ? 1f : 0f;
            }

            f[1] = Mathf.Clamp01(ctx.DistToTarget / 50f);
            f[2] = ctx.HasLoS ? 1f : 0f;
            f[3] = Mathf.Clamp01(ctx.Pressure / 10f);

            f[6] = ctx.Personality.Aggression;
            f[7] = ctx.Personality.Caution;
            f[8] = ctx.Personality.Teamwork;

            if (ctx.Target != null)
            {
                f[9] = ctx.Target.Health.CurrentHealth / ctx.Target.Health.MaxHealth;
            }
            else
            {
                f[9] = 1f;
            }

            return f;
        }
    }
}
