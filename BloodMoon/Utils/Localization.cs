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
                    { "Boss_Taunt_1", "你的死期到了" },
                    { "Boss_Taunt_2", "血月将吞噬一切" },
                    { "Boss_Taunt_3", "没人能活着离开" },
                    { "UI_Format", "{0} {1}" }
                }
            },
            {
                "en-US", new Dictionary<string, string>
                {
                    { "Event_Incoming", "Blood Moon In" },
                    { "Event_Active", "Blood Moon Ends" },
                    { "Boss_Revenge", "I will hunt you down!" },
                    { "Boss_Taunt_1", "Your time has come!" },
                    { "Boss_Taunt_2", "The Blood Moon devours all!" },
                    { "Boss_Taunt_3", "None shall escape!" },
                    { "UI_Format", "{0} {1}" }
                }
            }
        };

        /// <summary>
        /// 获取本地化文本
        /// </summary>
        /// <param name="key">文本键</param>
        /// <returns>本地化后的文本，如果找不到则返回键本身</returns>
        public static string Get(string key)
        {
            // 安全：处理配置未就绪
            string lang = "en-US";
            if (ModConfig.Instance != null) lang = ModConfig.Instance.Language;
            
            if (!_texts.ContainsKey(lang)) lang = "en-US";
            
            // 尝试目标语言
            if (_texts[lang].TryGetValue(key, out var val)) return val;
            
            // 回退：如果目标语言中缺少键，则尝试英语
            if (lang != "en-US" && _texts["en-US"].TryGetValue(key, out var enVal)) return enVal;
            
            return key;
        }
    }
}
