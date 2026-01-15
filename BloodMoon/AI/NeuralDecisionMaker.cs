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

        // Feature Indices
        // 0: Health %
        // 1: Dist to Target (normalized)
        // 2: Has LoS (0 or 1)
        // 3: Pressure (normalized)
        // 4: Ammo %
        // 5: Is Reloading
        // 6: Personality Aggression
        // 7: Personality Caution
        // 8: Personality Teamwork
        // 9: Target Health % (if known)
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
            // Input: 10 features
            // Hidden: 16 neurons
            // Output: Number of actions
            
            _brainPath = Path.Combine(BloodMoon.Utils.Logger.ModDirectory, "global_brain.json");
            
            if (File.Exists(_brainPath))
            {
                 try {
                    string json = File.ReadAllText(_brainPath);
                    _network = NeuralNetwork.LoadFromString(json);
                    // Check topology match
                    if (_network == null || _network.Layers == null || _network.Layers.Length < 1 || _network.Layers[0] != INPUT_SIZE)
                    {
                        // Structure mismatch or load failed, reset
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
            // Simple fitness function
            float fitness = survivalTime + (kills * 50f) + (damageDealt * 0.1f);
            
            // If this AI did exceptionally well, save its brain as the new baseline
            // Thresholds should ideally be dynamic or high enough
            if (fitness > 500f) // Example threshold
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
                    
                    // Apply static weight
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
            // Low Health -> Increase Heal/Retreat/Cover
            float hpPercent = ctx.Character != null ? ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth : 1f;
            if (hpPercent < 0.3f)
            {
                if (scores.ContainsKey("Heal")) scores["Heal"] *= 2.0f;
                if (scores.ContainsKey("Retreat")) scores["Retreat"] *= 1.5f;
                if (scores.ContainsKey("Cover")) scores["Cover"] *= 1.3f;
                if (scores.ContainsKey("Engage")) scores["Engage"] *= 0.5f;
            }

            // Low Ammo -> Decrease Engage, Increase Reload
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

            f[1] = Mathf.Clamp01(ctx.DistToTarget / 50f); // Normalize 0-50m
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
                f[9] = 1f; // Assume full health if unknown
            }

            return f;
        }
    }
}
