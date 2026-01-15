using System.Collections.Generic;
using UnityEngine;
using BloodMoon.Utils;
using Duckov;

namespace BloodMoon.AI
{
    public class AdaptiveDifficulty
    {
        private static AdaptiveDifficulty _instance = null!;
        public static AdaptiveDifficulty Instance => _instance;

        private float _sessionStartTime;
        private int _playerKills;
        private int _playerDamageTaken;
        private float _difficultyScore = 1.0f;
        public float DifficultyScore => _difficultyScore;

        // Difficulty Multipliers
        public float AggressionMultiplier { get; private set; } = 1.0f;
        public float ReactionTimeMultiplier { get; private set; } = 1.0f;
        public float AccuracyMultiplier { get; private set; } = 1.0f;
        public float DamageMultiplier { get; private set; } = 1.0f;

        public void Initialize()
        {
            _instance = this;
            _sessionStartTime = Time.time;
            BloodMoon.Utils.Logger.Log("AdaptiveDifficulty Initialized");
            
            // Hook into game events if possible, or rely on AI reporting
        }

        public void ReportPlayerKill()
        {
            _playerKills++;
            UpdateDifficulty();
        }

        public void ReportPlayerDamage(float amount)
        {
            _playerDamageTaken += (int)amount;
            UpdateDifficulty();
        }

        public void UpdateDifficulty()
        {
            float sessionDuration = Time.time - _sessionStartTime;
            if (sessionDuration < 60f) return; // Don't adjust too early

            // Calculate Kills Per Minute
            float kpm = _playerKills / (sessionDuration / 60f);
            
            // Calculate Damage Taken Per Minute
            float dpm = _playerDamageTaken / (sessionDuration / 60f);

            // Base Score
            float score = 1.0f;

            // If player is killing fast (> 5 KPM), increase difficulty
            if (kpm > 5f) score += 0.5f;
            if (kpm > 10f) score += 0.5f;

            // If player is taking little damage (< 50 DPM), increase difficulty
            if (dpm < 50f) score += 0.3f;
            
            // If player is struggling (Low KPM, High DPM), decrease difficulty
            if (kpm < 2f && dpm > 200f) score -= 0.4f;

            _difficultyScore = Mathf.Clamp(score, 0.5f, 2.5f);

            // Apply to Multipliers
            // Harder = Higher Aggression, Faster Reaction (Lower Multiplier), Higher Accuracy (Lower Spread), Higher Damage
            
            AggressionMultiplier = Mathf.Lerp(0.8f, 1.5f, (_difficultyScore - 0.5f) / 2f);
            ReactionTimeMultiplier = Mathf.Lerp(1.2f, 0.7f, (_difficultyScore - 0.5f) / 2f); // Lower is faster
            AccuracyMultiplier = Mathf.Lerp(1.2f, 0.8f, (_difficultyScore - 0.5f) / 2f); // Lower spread is better
            DamageMultiplier = Mathf.Lerp(0.9f, 1.3f, (_difficultyScore - 0.5f) / 2f);

            #if DEBUG
            // Logger.Debug($"Adaptive Difficulty Updated: Score={_difficultyScore:F2} (KPM={kpm:F1}, DPM={dpm:F1})");
            #endif
        }
    }
}
