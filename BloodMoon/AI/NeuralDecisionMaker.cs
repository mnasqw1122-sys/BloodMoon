using System.Collections.Generic;
using UnityEngine;
using BloodMoon.Utils;
using System.IO;

namespace BloodMoon.AI
{
    public class NeuralDecisionMaker
    {
        private NeuralNetwork _network;
        private List<string> _actionNames;
        private bool _isInitialized = false;
        private string _brainPath;

        // 特征索引
        // 0: 生命值百分比
        // 1: 到目标的距离（归一化）
        // 2: 是否有视线（0或1）
        // 3: 压力（归一化）
        // 4: 弹药百分比
        // 5: 是否正在装弹
        // 6: 性格攻击性
        // 7: 性格谨慎性
        // 8: 性格团队合作
        // 9: 目标生命值百分比（如果已知）
        private const int INPUT_SIZE = 10;
        
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

        public NeuralDecisionMaker(List<string> actionNames)
        {
            _actionNames = actionNames;
            // 输入：10个特征
            // 隐藏层：16个神经元
            // 输出：动作数量
            
            _brainPath = Path.Combine(BloodMoon.Utils.Logger.ModDirectory, "global_brain.json");
            
            if (File.Exists(_brainPath))
            {
                 try {
                    string json = File.ReadAllText(_brainPath);
                    _network = NeuralNetwork.LoadFromString(json);
                    // 检查拓扑匹配
                    if (_network == null || _network.Layers == null || _network.Layers.Length < 1 || _network.Layers[0] != INPUT_SIZE)
                    {
                        // 结构不匹配或加载失败，请重置
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
        
        private void CreateNewNetwork()
        {
            _network = new NeuralNetwork(new int[] { INPUT_SIZE, 16, _actionNames.Count });
            _network.InitializeRandom();
        }

        public void ReportPerformance(float survivalTime, int kills, int damageDealt)
        {
            // 简单的适应度函数
            float fitness = survivalTime + (kills * 50f) + (damageDealt * 0.1f);
            
            // 如果这个AI表现得非常出色，就将其大脑保存为新的基准模型
            // 阈值应理想地具有动态性或足够高
            if (fitness > 500f) // 示例阈值
            {
                try {
                    string json = _network.SaveToString();
                    File.WriteAllText(_brainPath, json);
                    BloodMoon.Utils.Logger.Log($"New Best Brain Saved! Fitness: {fitness}");
                } catch {}
            }
        }

        public virtual Dictionary<string, float> GetActionScores(AIContext ctx)
        {
            if (!_isInitialized) return new Dictionary<string, float>();

            float[] inputs = ExtractFeatures(ctx);
            float[] outputs = _network.FeedForward(inputs);

            var scores = new Dictionary<string, float>();
            for (int i = 0; i < outputs.Length; i++)
            {
                if (i < _actionNames.Count)
                {
                    string action = _actionNames[i];
                    float baseScore = outputs[i];
                    
                    // 应用静态权重
                    if (_actionWeights.TryGetValue(action, out float w))
                    {
                        baseScore *= w;
                    }
                    
                    scores[action] = baseScore;
                }
            }
            
            ApplyContextAdjustment(scores, ctx);
            
            return scores;
        }

        private void ApplyContextAdjustment(Dictionary<string, float> scores, AIContext ctx)
        {
            // 低生命值 -> 增加治疗/撤退/掩护
            float hpPercent = ctx.Character != null ? ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth : 1f;
            if (hpPercent < 0.3f)
            {
                if (scores.ContainsKey("Heal")) scores["Heal"] *= 2.0f;
                if (scores.ContainsKey("Retreat")) scores["Retreat"] *= 1.5f;
                if (scores.ContainsKey("Cover")) scores["Cover"] *= 1.3f;
                if (scores.ContainsKey("Engage")) scores["Engage"] *= 0.5f;
            }

            // 弹药不足 -> 降低攻击，增加装填
            var gun = ctx.Character?.GetGun();
            if (gun != null && gun.BulletCount == 0)
            {
                if (scores.ContainsKey("Reload")) scores["Reload"] *= 2.0f;
                if (scores.ContainsKey("Engage")) scores["Engage"] *= 0.2f;
            }
        }

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

            f[1] = Mathf.Clamp01(ctx.DistToTarget / 50f); // 将0-50米标准化
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
                f[9] = 1f; // 若健康状况未知，则假设为完全健康
            }

            return f;
        }
    }
}
