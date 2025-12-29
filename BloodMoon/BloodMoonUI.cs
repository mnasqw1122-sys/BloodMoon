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

        public void Refresh(TimeSpan now)
        {
            if (_title == null) return;
            var eta = _event.GetETA(now);
            var active = _event.IsActive(now);
            var remain = active ? _event.GetOverETA(now) : eta;
            var label = active ? Localization.Get("Event_Active") : Localization.Get("Event_Incoming");
            var timeStr = $"{Mathf.FloorToInt((float)remain.TotalHours):000}:{remain.Minutes:00}";
            
            var fullStr = string.Format(Localization.Get("UI_Format"), label, timeStr);
            if (fullStr != _lastTimeStr)
            {
                _title.text = fullStr;
                _lastTimeStr = fullStr;
            }
        }

        public void Dispose()
        {
            if (_title != null) UnityEngine.Object.Destroy(_title.gameObject);
        }
    }
}
