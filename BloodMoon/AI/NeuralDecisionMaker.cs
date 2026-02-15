using System;
using System.Collections.Generic;
using UnityEngine;
using BloodMoon.Utils;
using System.IO;

namespace BloodMoon.AI
{
    /// <summary>
    /// 神经决策器，使用神经网络进行AI决策
    /// </summary>
    public class NeuralDecisionMaker
    {
        private NeuralNetwork _network;
        private List<string> _actionNames;
        private bool _isInitialized = false;
        private string _brainPath;

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
        /// 构造函数，初始化神经决策器
        /// </summary>
        /// <param name="actionNames">可用的动作名称列表</param>
        public NeuralDecisionMaker(List<string> actionNames)
        {
            _actionNames = actionNames;
            
            _brainPath = Path.Combine(BloodMoon.Utils.Logger.ModDirectory, "global_brain.json");
            
            if (File.Exists(_brainPath))
            {
                try {
                    string json = File.ReadAllText(_brainPath);
                    _network = NeuralNetwork.LoadFromString(json);
                    if (_network == null || _network.Layers == null || _network.Layers.Length < 3 || _network.Layers[0] != INPUT_SIZE || _network.Layers[_network.Layers.Length - 1] != actionNames.Count)
                    {
                        CreateNewNetwork();
                    }
                } catch { CreateNewNetwork(); }
            }
            else
            {
                CreateNewNetwork();
            }
            
            _network!.InitializeRandom();
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
        /// 报告AI的性能表现
        /// </summary>
        /// <param name="survivalTime">存活时间</param>
        /// <param name="kills">击杀数</param>
        /// <param name="damageDealt">造成的伤害</param>
        public void ReportPerformance(float survivalTime, int kills, int damageDealt)
        {
            float fitness = survivalTime + (kills * 50f) + (damageDealt * 0.1f);
            
            if (fitness > 500f)
            {
                try {
                    string json = _network.SaveToString();
                    File.WriteAllText(_brainPath, json);
                    BloodMoon.Utils.Logger.Log($"New Best Brain Saved! Fitness: {fitness}");
                } catch {}
            }
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
