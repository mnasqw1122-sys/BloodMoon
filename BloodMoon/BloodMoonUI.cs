using System;
using TMPro;
using UnityEngine;
using Duckov.Weathers;
using Duckov.Utilities;
using BloodMoon.Utils;

namespace BloodMoon
{
    public class BloodMoonUI : IDisposable
    {
        private readonly BloodMoonEvent _event;
        private TextMeshProUGUI _title = null!;

        public BloodMoonUI(BloodMoonEvent e)
        {
            _event = e;
        }

        public bool IsAttached => _title != null;

        public bool TryAttachToTimeOfDayDisplay()
        {
            if (IsAttached) return true;
            
            var tod = UnityEngine.Object.FindObjectOfType<TimeOfDayDisplay>();
            if (tod == null) return false;
            
            if (_title != null) UnityEngine.Object.Destroy(_title.gameObject);
            _title = UnityEngine.Object.Instantiate(GameplayDataSettings.UIStyle.TemplateTextUGUI);
            _title.text = string.Format(Localization.Get("UI_Format"), Localization.Get("Event_Incoming"), "000:00");
            var parent = tod.stormText.transform.parent;
            _title.transform.SetParent(parent);
            _title.transform.localScale = Vector3.one;
            _title.fontSize = tod.stormTitleText.fontSize;
            _title.rectTransform.anchoredPosition = tod.stormTitleText.rectTransform.anchoredPosition + new Vector2(0, -75);
            return true;
        }

        private string _lastTimeStr = string.Empty;
        private Color _baseColor = new Color(1f, 0.2f, 0.2f); // Red
        private Color _activeColor = new Color(1f, 0f, 0f); // Deep Red

        public void Refresh(TimeSpan now)
        {
            if (_title == null) return;
            var eta = _event.GetETA(now);
            bool active = _event.IsActive(now);
            var remain = active ? _event.GetOverETA(now) : eta;
            var label = active ? Localization.Get("Event_Active") : Localization.Get("Event_Incoming");
            var timeStr = $"{Mathf.FloorToInt((float)remain.TotalHours):000}:{remain.Minutes:00}";
            
            // 1. Text Content
            string difficultyStr = "";
            if (BloodMoon.AI.AdaptiveDifficulty.Instance != null)
            {
                float diff = BloodMoon.AI.AdaptiveDifficulty.Instance.DifficultyScore;
                string diffLabel = diff > 1.5f ? "EXTREME" : (diff > 1.1f ? "HARD" : (diff < 0.8f ? "EASY" : "NORMAL"));
                difficultyStr = $"\nTHREAT: {diffLabel} ({diff:F1})";
            }

            var fullStr = string.Format(Localization.Get("UI_Format"), label, timeStr) + difficultyStr;
            if (fullStr != _lastTimeStr)
            {
                _title.text = fullStr;
                _lastTimeStr = fullStr;
            }

            // 2. Visual Effects (Pulse)
            float pulseSpeed = active ? 4.0f : 1.0f;
            float alpha = Mathf.PingPong(Time.time * pulseSpeed, 0.5f) + 0.5f; // 0.5 to 1.0
            
            if (active)
            {
                _title.color = new Color(_activeColor.r, _activeColor.g, _activeColor.b, alpha);
                // Shake effect if very active
                _title.rectTransform.anchoredPosition += UnityEngine.Random.insideUnitCircle * 0.5f;
            }
            else
            {
                // Orange/Yellow warning
                _title.color = new Color(1f, 0.6f, 0f, alpha);
            }
        }

        public void Dispose()
        {
            if (_title != null) UnityEngine.Object.Destroy(_title.gameObject);
        }
    }
}
