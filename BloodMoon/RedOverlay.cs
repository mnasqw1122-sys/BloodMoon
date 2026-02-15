using UnityEngine;
using UnityEngine.SceneManagement;

namespace BloodMoon
{
    public class RedOverlay
    {
        private bool _isActive;
        private float _transitionProgress; // 0 to 1
        private const float TRANSITION_SPEED = 0.5f;
        
        // 原始设置备份
        private Color _origFogColor;
        private float _origFogDensity;
        private FogMode _origFogMode;
        private bool _origFogEnabled;
        private Color _origAmbient;
        
        // 目标设置
        // 深红色
        private Color _targetFogColor = new Color(0.6f, 0.02f, 0.02f, 1f); 
        // 足够厚以在约40-50米外遮挡视线
        private float _targetDensity = 0.025f; 
        
        private bool _captured;

        /// <summary>
        /// 显示红色覆盖效果
        /// </summary>
        public void Show()
        {
            if (!_isActive)
            {
                if (!_captured) CaptureOriginals();
                _isActive = true;
            }
        }

        /// <summary>
        /// 隐藏红色覆盖效果
        /// </summary>
        public void Hide()
        {
            if (_isActive)
            {
                _isActive = false;
                // 过渡将在Tick中处理其余部分
            }
        }

        private Scene _capturedScene;

        /// <summary>
        /// 捕获原始的渲染设置
        /// </summary>
        private void CaptureOriginals()
        {
            _capturedScene = SceneManager.GetActiveScene();
            _origFogEnabled = RenderSettings.fog;
            _origFogColor = RenderSettings.fogColor;
            _origFogDensity = RenderSettings.fogDensity;
            _origFogMode = RenderSettings.fogMode;
            _origAmbient = RenderSettings.ambientLight;
            _captured = true;
        }

        /// <summary>
        /// 更新红色覆盖效果的每一帧
        /// </summary>
        /// <param name="dt">增量时间</param>
        public void Tick(float dt)
        {
            // 安全性：检查场景有效性
            var currentScene = SceneManager.GetActiveScene();
            if (_captured && currentScene != _capturedScene)
            {
                // 场景已更改！我们捕获的数据对此新场景无效
                // 重置捕获状态，以便我们在下一帧捕获新场景的默认值
                _captured = false; 
                // 不要将旧场景的设置恢复到新场景
            }

            // 计算过渡
            if (_isActive)
            {
                _transitionProgress = Mathf.MoveTowards(_transitionProgress, 1f, dt * TRANSITION_SPEED);
            }
            else
            {
                _transitionProgress = Mathf.MoveTowards(_transitionProgress, 0f, dt * TRANSITION_SPEED);
            }

            if (_transitionProgress <= 0f)
            {
                if (_captured)
                {
                    // 完全淡出时恢复确切的原始值
                    RestoreOriginals();
                }
                return;
            }

            // 如果我们尚未捕获（安全性）
            if (!_captured) CaptureOriginals();

            // 应用效果
            ApplyBloodMoonAtmosphere();
        }

        /// <summary>
        /// 应用血月的大气效果
        /// </summary>
        private void ApplyBloodMoonAtmosphere()
        {
            RenderSettings.fog = true;
            // 强制使用Exp2以获得最佳体积感
            RenderSettings.fogMode = FogMode.ExponentialSquared; 

            // "呼吸"大气效果的脉冲效果
            float pulse = Mathf.Sin(Time.unscaledTime * 0.8f) * 0.2f + 1.0f; // 0.8 到 1.2，更慢更深
            
            float t = _transitionProgress;
            
            // 颜色渐变：从正常开始，淡入红色
            Color bloodColor = _targetFogColor * pulse;
            // 限制亮度以避免霓虹雾，但允许一些过亮用于泛光效果
            bloodColor.r = Mathf.Clamp(bloodColor.r, 0f, 1.2f); 
            
            RenderSettings.fogColor = Color.Lerp(_origFogColor, bloodColor, t);
            
            // 密度
            float bloodDensity = _targetDensity * (pulse * 0.5f + 0.5f); // 更多变化密度
            RenderSettings.fogDensity = Mathf.Lerp(_origFogDensity, bloodDensity, t);
            
            // 环境光：使世界变暗以使雾光更突出
            // 与环境光脉冲相反（更多雾 = 更暗的环境光）
            Color bloodAmbient = new Color(0.2f, 0.02f, 0.02f) * (2.0f - pulse);
            RenderSettings.ambientLight = Color.Lerp(_origAmbient, bloodAmbient, t);
        }

        /// <summary>
        /// 恢复原始的渲染设置
        /// </summary>
        private void RestoreOriginals()
        {
            RenderSettings.fog = _origFogEnabled;
            RenderSettings.fogColor = _origFogColor;
            RenderSettings.fogDensity = _origFogDensity;
            RenderSettings.fogMode = _origFogMode;
            RenderSettings.ambientLight = _origAmbient;
            _captured = false;
        }

        /// <summary>
        /// 销毁资源并清理
        /// </summary>
        public void Dispose()
        {
            if (_captured) RestoreOriginals();
        }
    }
}
