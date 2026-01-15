using System.Collections.Generic;
using UnityEngine;
using System.Text;
using System.Linq;
using BloodMoon.Utils;

namespace BloodMoon.AI
{
    public class SystemMetrics
    {
        public string SystemName { get; private set; }
        private Dictionary<string, object> _metrics;
        private List<string> _alerts;
        
        public SystemMetrics(string systemName)
        {
            SystemName = systemName;
            _metrics = new Dictionary<string, object>();
            _alerts = new List<string>();
        }
        
        public void SetMetric(string name, object value)
        {
            _metrics[name] = value;
        }
        
        public void AddAlert(string alert)
        {
            if (!_alerts.Contains(alert))
                _alerts.Add(alert);
        }
        
        public void ClearAlerts()
        {
            _alerts.Clear();
        }
        
        public Dictionary<string, object> GetCriticalMetrics()
        {
            return _metrics;
        }
        
        public List<string> GetAlerts()
        {
            return _alerts;
        }
    }

    public class RuntimeMonitor
    {
        private static RuntimeMonitor? _instance;
        public static RuntimeMonitor Instance => _instance ??= new RuntimeMonitor();
        
        private Dictionary<string, SystemMetrics> _systemMetrics = null!;
        private float _metricsUpdateInterval = 5.0f;
        private float _lastMetricsUpdate;
        
        // Concurrent pathfinding tracking for performance optimization
        private int _concurrentPathfindingCount = 0;
        private object _pathfindingLock = new object();
        
        public int ConcurrentPathfindingCount 
        { 
            get 
            { 
                lock (_pathfindingLock) 
                { 
                    return _concurrentPathfindingCount; 
                } 
            } 
        }
        
        public void IncrementPathfindingCount()
        {
            lock (_pathfindingLock)
            {
                _concurrentPathfindingCount++;
                UpdatePathfindingMetrics();
            }
        }
        
        public void DecrementPathfindingCount()
        {
            lock (_pathfindingLock)
            {
                if (_concurrentPathfindingCount > 0)
                    _concurrentPathfindingCount--;
                UpdatePathfindingMetrics();
            }
        }
        
        private void UpdatePathfindingMetrics()
        {
            var metrics = _systemMetrics["BehaviorSystem"];
            metrics.SetMetric("ConcurrentPathfinding", _concurrentPathfindingCount);
            
            // Alert if too many concurrent pathfinding operations
            if (_concurrentPathfindingCount > 10)
            {
                metrics.AddAlert($"High concurrent pathfinding: {_concurrentPathfindingCount}");
            }
        }
        
        public RuntimeMonitor()
        {
            Initialize();
        }
        
        public void Initialize()
        {
            _systemMetrics = new Dictionary<string, SystemMetrics>
            {
                { "WeaponSystem", new SystemMetrics("WeaponSystem") },
                { "DecisionSystem", new SystemMetrics("DecisionSystem") },
                { "BehaviorSystem", new SystemMetrics("BehaviorSystem") },
                { "SquadSystem", new SystemMetrics("SquadSystem") },
                { "NeuralNetwork", new SystemMetrics("NeuralNetwork") }
            };
        }
        
        public void Update()
        {
            if (Time.time - _lastMetricsUpdate >= _metricsUpdateInterval)
            {
                UpdateAllMetrics();
                _lastMetricsUpdate = Time.time;
                
                // Output critical metrics
                LogCriticalMetrics();
            }
        }
        
        private void UpdateAllMetrics()
        {
            foreach(var m in _systemMetrics.Values) m.ClearAlerts();
            
            // Update Weapon System Metrics
            UpdateWeaponSystemMetrics();
            
            // Other systems updates could go here
        }
        
        private void UpdateWeaponSystemMetrics()
        {
            var metrics = _systemMetrics["WeaponSystem"];
            
            // Count AI weapon status
            int totalAI = BloodMoonAIController.AllControllers.Count;
            int aiWithWeapons = BloodMoonAIController.AllControllers.Count(a => a.HasWeapon);
            int aiWithoutWeapons = totalAI - aiWithWeapons;
            
            metrics.SetMetric("TotalAI", totalAI);
            metrics.SetMetric("AIWithWeapons", aiWithWeapons);
            metrics.SetMetric("AIWithoutWeapons", aiWithoutWeapons);
            metrics.SetMetric("WeaponSuccessRate", totalAI > 0 ? (float)aiWithWeapons / totalAI : 0f);
            
            // Detect issues
            if (totalAI > 0 && aiWithoutWeapons > totalAI * 0.5f)
            {
                metrics.AddAlert("High percentage of AI without weapons");
            }
        }
        
        private void LogCriticalMetrics()
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("[RuntimeMonitor] System Metrics:");
            
            bool hasAlerts = false;
            
            foreach (var metrics in _systemMetrics.Values)
            {
                // Only log if interesting or has alerts
                var critical = metrics.GetCriticalMetrics();
                var alerts = metrics.GetAlerts();
                
                if (critical.Count == 0 && alerts.Count == 0) continue;
                
                log.AppendLine($"  {metrics.SystemName}:");
                
                foreach (var metric in critical)
                {
                    log.AppendLine($"    {metric.Key}: {metric.Value}");
                }
                
                foreach (var alert in alerts)
                {
                    log.AppendLine($"    [ALERT] {alert}");
                    hasAlerts = true;
                }
            }
            
            // Only log to console if there are alerts or periodically (to avoid spam)
            if (hasAlerts || Time.frameCount % 1000 == 0)
            {
                if (hasAlerts) BloodMoon.Utils.Logger.Error(log.ToString());
                else BloodMoon.Utils.Logger.Log(log.ToString());
            }
        }
    }
}
