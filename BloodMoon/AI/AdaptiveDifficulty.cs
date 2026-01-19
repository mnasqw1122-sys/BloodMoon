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

        // 难度乘数
        public float AggressionMultiplier { get; private set; } = 1.0f;
        public float ReactionTimeMultiplier { get; private set; } = 1.0f;
        public float AccuracyMultiplier { get; private set; } = 1.0f;
        public float DamageMultiplier { get; private set; } = 1.0f;

        public void Initialize()
        {
            _instance = this;
            _sessionStartTime = Time.time;
            BloodMoon.Utils.Logger.Log("AdaptiveDifficulty Initialized");
            
            // 尽可能挂接到游戏事件，或依赖AI报告
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
            if (sessionDuration < 60f) return; // 不要过早调整

            // 计算每分钟击杀数
            float kpm = _playerKills / (sessionDuration / 60f);
            
            // 计算每分钟承受伤害
            float dpm = _playerDamageTaken / (sessionDuration / 60f);

            // 基础分数
            float score = 1.0f;

            // 如果玩家击杀速度快（> 5 KPM），增加难度
            if (kpm > 5f) score += 0.5f;
            if (kpm > 10f) score += 0.5f;

            // 如果玩家承受伤害少（< 50 DPM），增加难度
            if (dpm < 50f) score += 0.3f;
            
            // 如果玩家陷入困境（低KPM，高DPM），降低难度
            if (kpm < 2f && dpm > 200f) score -= 0.4f;

            _difficultyScore = Mathf.Clamp(score, 0.5f, 2.5f);

            // 应用到乘数
            // 难度越高 = 更高的攻击性，更快的反应（更低的乘数），更高的精度（更低的散布），更高的伤害
            
            AggressionMultiplier = Mathf.Lerp(0.8f, 1.5f, (_difficultyScore - 0.5f) / 2f);
            ReactionTimeMultiplier = Mathf.Lerp(1.2f, 0.7f, (_difficultyScore - 0.5f) / 2f); // 越低越快
            AccuracyMultiplier = Mathf.Lerp(1.2f, 0.8f, (_difficultyScore - 0.5f) / 2f); // 散布越低越好
            DamageMultiplier = Mathf.Lerp(0.9f, 1.3f, (_difficultyScore - 0.5f) / 2f);

            #if DEBUG
            // Logger.Debug($"自适应难度已更新: 分数={_difficultyScore:F2} (KPM={kpm:F1}, DPM={dpm:F1})");
            #endif
        }
    }
}
