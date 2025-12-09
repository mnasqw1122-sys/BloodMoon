using System;
using TMPro;
using UnityEngine;
using Duckov.Weathers;
using Duckov.Utilities;

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

        public void AttachToTimeOfDayDisplay()
        {
            var tod = UnityEngine.Object.FindObjectOfType<TimeOfDayDisplay>();
            if (tod == null) return;
            if (_title != null) UnityEngine.Object.Destroy(_title.gameObject);
            _title = UnityEngine.Object.Instantiate(GameplayDataSettings.UIStyle.TemplateTextUGUI);
            _title.text = "血月来临时间 000:00";
            var parent = tod.stormText.transform.parent;
            _title.transform.SetParent(parent);
            _title.transform.localScale = Vector3.one;
            _title.fontSize = tod.stormTitleText.fontSize;
            _title.rectTransform.anchoredPosition = tod.stormTitleText.rectTransform.anchoredPosition + new Vector2(0, -75);
        }

        public void Refresh(TimeSpan now)
        {
            if (_title == null) return;
            var eta = _event.GetETA(now);
            var active = _event.IsActive(now);
            var remain = active ? _event.GetOverETA(now) : eta;
            var label = active ? "血月持续时间" : "血月来临时间";
            var timeStr = $"{Mathf.FloorToInt((float)remain.TotalHours):000}:{remain.Minutes:00}";
            _title.text = $"{label} {timeStr}";
        }

        public void Dispose()
        {
            if (_title != null) UnityEngine.Object.Destroy(_title.gameObject);
        }
    }
}
