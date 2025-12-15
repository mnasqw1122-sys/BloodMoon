using System.Collections.Generic;

namespace BloodMoon.Utils
{
    public static class Localization
    {
        private static Dictionary<string, Dictionary<string, string>> _texts = new Dictionary<string, Dictionary<string, string>>()
        {
            {
                "zh-CN", new Dictionary<string, string>
                {
                    { "Event_Incoming", "血月来临时间" },
                    { "Event_Active", "血月持续时间" },
                    { "Boss_Revenge", "我要你死无葬身之地" },
                    { "UI_Format", "{0} {1}" }
                }
            },
            {
                "en-US", new Dictionary<string, string>
                {
                    { "Event_Incoming", "Blood Moon In" },
                    { "Event_Active", "Blood Moon Ends" },
                    { "Boss_Revenge", "I will hunt you down!" },
                    { "UI_Format", "{0} {1}" }
                }
            }
        };

        public static string Get(string key)
        {
            // Safety: Handle config not ready
            string lang = "en-US";
            if (ModConfig.Instance != null) lang = ModConfig.Instance.Language;
            
            if (!_texts.ContainsKey(lang)) lang = "en-US";
            
            // Try target language
            if (_texts[lang].TryGetValue(key, out var val)) return val;
            
            // Fallback: Try English if key is missing in target language
            if (lang != "en-US" && _texts["en-US"].TryGetValue(key, out var enVal)) return enVal;
            
            return key;
        }
    }
}
