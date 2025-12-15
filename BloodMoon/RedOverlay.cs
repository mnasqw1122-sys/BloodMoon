using UnityEngine;
using UnityEngine.SceneManagement;

namespace BloodMoon
{
    public class RedOverlay
    {
        private bool _isActive;
        private float _transitionProgress; // 0 to 1
        private const float TRANSITION_SPEED = 0.5f;
        
        // Original Settings Backup
        private Color _origFogColor;
        private float _origFogDensity;
        private FogMode _origFogMode;
        private bool _origFogEnabled;
        private Color _origAmbient;
        
        // Target Settings
        // Deep crimson red
        private Color _targetFogColor = new Color(0.6f, 0.02f, 0.02f, 1f); 
        // Thick enough to obscure vision beyond ~40-50m
        private float _targetDensity = 0.025f; 
        
        private bool _captured;

        public void Show()
        {
            if (!_isActive)
            {
                if (!_captured) CaptureOriginals();
                _isActive = true;
            }
        }

        public void Hide()
        {
            if (_isActive)
            {
                _isActive = false;
                // Transition will handle the rest in Tick
            }
        }

        private Scene _capturedScene;

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

        public void Tick(float dt)
        {
            // Safety: Check scene validity
            var currentScene = SceneManager.GetActiveScene();
            if (_captured && currentScene != _capturedScene)
            {
                // Scene changed! Our captured data is invalid for this new scene.
                // Reset capture state so we capture the NEW scene's defaults next frame
                _captured = false; 
                // Don't restore old scene's settings to new scene
            }

            // Calculate Transition
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
                    // Restore exact originals when fully faded out
                    RestoreOriginals();
                }
                return;
            }

            // If we haven't captured yet (safety)
            if (!_captured) CaptureOriginals();

            // Apply Effect
            ApplyBloodMoonAtmosphere();
        }

        private void ApplyBloodMoonAtmosphere()
        {
            RenderSettings.fog = true;
            // Force Exp2 for best volumetric feel
            RenderSettings.fogMode = FogMode.ExponentialSquared; 

            // Pulse effect for "breathing" atmosphere
            float pulse = Mathf.Sin(Time.time * 1.2f) * 0.15f + 1.0f; // 0.85 to 1.15
            
            float t = _transitionProgress;
            
            // Color Gradient: Start normal, fade to Red
            Color bloodColor = _targetFogColor * pulse;
            // Clamp brightness to avoid neon fog
            bloodColor.r = Mathf.Clamp01(bloodColor.r); 
            
            RenderSettings.fogColor = Color.Lerp(_origFogColor, bloodColor, t);
            
            // Density
            float bloodDensity = _targetDensity * (pulse * 0.8f + 0.4f); // Vary density slightly
            RenderSettings.fogDensity = Mathf.Lerp(_origFogDensity, bloodDensity, t);
            
            // Ambient Light: Darken the world to make the fog glow more prominent
            Color bloodAmbient = new Color(0.25f, 0.05f, 0.05f);
            RenderSettings.ambientLight = Color.Lerp(_origAmbient, bloodAmbient, t);
        }

        private void RestoreOriginals()
        {
            RenderSettings.fog = _origFogEnabled;
            RenderSettings.fogColor = _origFogColor;
            RenderSettings.fogDensity = _origFogDensity;
            RenderSettings.fogMode = _origFogMode;
            RenderSettings.ambientLight = _origAmbient;
            _captured = false;
        }

        public void Dispose()
        {
            if (_captured) RestoreOriginals();
        }
    }
}
