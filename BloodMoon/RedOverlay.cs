using UnityEngine;
using UnityEngine.UI;

namespace BloodMoon
{
    public class RedOverlay
    {
        private Image? _img;
        private Color _target = new Color(1f, 0.12f, 0.12f, 0.18f);
        private float _fadeSpeed = 2.0f;
        private HUDManager? _cachedHud;

        public void Show()
        {
            if (_img == null)
            {
                if (_cachedHud == null) _cachedHud = Object.FindObjectOfType<HUDManager>();
                if (_cachedHud == null) return;
                var hud = _cachedHud;
                var exist = hud.transform.Find("BloodMoonOverlay");
                GameObject go;
                if (exist != null)
                {
                    go = exist.gameObject;
                    _img = go.GetComponent<Image>();
                    if (_img == null) _img = go.AddComponent<Image>();
                }
                else
                {
                    go = new GameObject("BloodMoonOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    go.transform.SetParent(hud.transform, false);
                    _img = go.GetComponent<Image>();
                }
                go.transform.SetSiblingIndex(0);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                _img.color = new Color(_target.r, _target.g, _target.b, 0f);
                _img.raycastTarget = false;
            }
            if (!_img.gameObject.activeSelf) _img.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_img != null) _img.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            if (_img != null) Object.Destroy(_img.gameObject);
        }

        public void Tick(float dt)
        {
            if (_img == null) return;
            var cur = _img.color;
            
            // Optimization: Stop updating if color is already close to target
            // This prevents unnecessary Canvas rebuilds once the fade is complete
            if (Mathf.Abs(cur.a - _target.a) < 0.002f && 
                Mathf.Abs(cur.r - _target.r) < 0.002f &&
                Mathf.Abs(cur.g - _target.g) < 0.002f)
            {
                 return;
            }

            float t = Mathf.Clamp01(dt * _fadeSpeed);
            _img.color = new Color(Mathf.Lerp(cur.r, _target.r, t), Mathf.Lerp(cur.g, _target.g, t), Mathf.Lerp(cur.b, _target.b, t), Mathf.Lerp(cur.a, _target.a, t));
        }

        public void SetTarget(Color c)
        {
            _target = c;
        }
    }
}
