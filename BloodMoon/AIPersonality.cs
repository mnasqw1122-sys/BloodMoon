using System;
using UnityEngine;

namespace BloodMoon
{
    [Serializable]
    public class AIPersonality
    {
        // Traits (0.0 to 1.0)
        public float Aggression;  // Preference for attacking/rushing
        public float Caution;     // Preference for cover/retreat
        public float Teamwork;    // Preference for squad tactics/support
        public float Greed;       // Preference for looting/killing vs tactical

        public AIPersonality()
        {
            // Default balanced personality
            Aggression = 0.5f;
            Caution = 0.5f;
            Teamwork = 0.5f;
            Greed = 0.5f;
        }

        public static AIPersonality GenerateRandom()
        {
            var p = new AIPersonality();
            p.Aggression = UnityEngine.Random.Range(0.2f, 1.0f);
            p.Caution = UnityEngine.Random.Range(0.1f, 0.9f);
            // Inversely correlate Caution and Aggression slightly
            if (p.Aggression > 0.7f) p.Caution *= 0.6f;
            
            p.Teamwork = UnityEngine.Random.Range(0.0f, 1.0f);
            p.Greed = UnityEngine.Random.Range(0.2f, 0.8f);
            return p;
        }

        public override string ToString()
        {
            return $"[Agg:{Aggression:F2} Caut:{Caution:F2} Team:{Teamwork:F2}]";
        }
    }
}
