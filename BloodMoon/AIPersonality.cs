using System;
using UnityEngine;

namespace BloodMoon
{
    [Serializable]
    public class AIPersonality
    {
        // 特质（0.0 到 1.0）
        public float Aggression;  // 偏好攻击/冲锋
        public float Caution;     // 偏好掩护/撤退
        public float Teamwork;    // 偏好小队战术/支援
        public float Greed;       // 偏好掠夺/击杀 vs 战术

        /// <summary>
        /// 构造函数，创建默认平衡性格
        /// </summary>
        public AIPersonality()
        {
            // 默认平衡性格
            Aggression = 0.5f;
            Caution = 0.5f;
            Teamwork = 0.5f;
            Greed = 0.5f;
        }

        /// <summary>
        /// 生成随机性格
        /// </summary>
        /// <returns>随机生成的AIPersonality实例</returns>
        public static AIPersonality GenerateRandom()
        {
            var p = new AIPersonality();
            p.Aggression = UnityEngine.Random.Range(0.2f, 1.0f);
            p.Caution = UnityEngine.Random.Range(0.1f, 0.9f);
            // 谨慎和攻击性略微负相关
            if (p.Aggression > 0.7f) p.Caution *= 0.6f;
            
            p.Teamwork = UnityEngine.Random.Range(0.0f, 1.0f);
            p.Greed = UnityEngine.Random.Range(0.2f, 0.8f);
            return p;
        }

        /// <summary>
        /// 返回性格的字符串表示
        /// </summary>
        /// <returns>格式化的性格字符串</returns>
        public override string ToString()
        {
            return $"[Agg:{Aggression:F2} Caut:{Caution:F2} Team:{Teamwork:F2}]";
        }
    }
}
