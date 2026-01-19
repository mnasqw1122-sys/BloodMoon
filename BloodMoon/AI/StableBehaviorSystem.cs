using System.Collections.Generic;
using UnityEngine;

namespace BloodMoon.AI
{
    public class BehaviorHistory
    {
        public string CurrentAction { get; set; } = string.Empty;
        public float CurrentDuration { get; set; }
        public float LastExecutionTime { get; set; }
        public int ExecutionCount { get; set; }
    }

    public class StableBehaviorSystem
    {
        private Dictionary<string, BehaviorHistory> _behaviorHistory = null!;
        private float _minActionDuration = 2.0f;
        private float _cooldownBetweenActions = 0.5f;
        
        public void Initialize()
        {
            _behaviorHistory = new Dictionary<string, BehaviorHistory>();
        }
        
        public string GetStableAction(string proposedAction, float actionScore, AIContext ctx, List<string> availableActions, Dictionary<string, float> allScores)
        {
            // 如果我们已经在运行一个动作，检查是否应该坚持它
            if (_behaviorHistory.ContainsKey("Current"))
            {
                var current = _behaviorHistory["Current"];
                
                // 如果当前动作仍然有效（分数 > 0）并且我们运行时间不够长
                if (current.CurrentAction == proposedAction)
                {
                     UpdateBehaviorHistory(proposedAction);
                     return proposedAction;
                }
                
                // 如果我们试图切换
                if (current.CurrentDuration < _minActionDuration)
                {
                    // 如果新动作好得多（例如分数差 > 0.3），允许切换
                    // 否则坚持当前动作
                    if (allScores.ContainsKey(current.CurrentAction))
                    {
                        float currentScore = allScores[current.CurrentAction];
                        if (actionScore < currentScore + 0.3f)
                        {
                            // 坚持当前动作
                            UpdateBehaviorHistory(current.CurrentAction);
                            return current.CurrentAction;
                        }
                    }
                }
            }
            
            // 检查特定动作的冷却时间
            if (_behaviorHistory.ContainsKey(proposedAction))
            {
                var history = _behaviorHistory[proposedAction];
                if (history.LastExecutionTime + _cooldownBetweenActions > Time.time)
                {
                    // 此操作处于冷却状态，请寻找替代方案
                    return GetAlternativeAction(proposedAction, allScores);
                }
            }
            
            UpdateBehaviorHistory(proposedAction);
            return proposedAction;
        }
        
        private string GetAlternativeAction(string proposedAction, Dictionary<string, float> scores)
        {
            string bestAlt = proposedAction; // 如果没有更好的选择，则默认返回
            float bestScore = -1f;
            
            foreach (var kvp in scores)
            {
                if (kvp.Key == proposedAction) continue;
                
                // 检查此替代方案是否处于冷却状态
                if (_behaviorHistory.ContainsKey(kvp.Key))
                {
                    if (_behaviorHistory[kvp.Key].LastExecutionTime + _cooldownBetweenActions > Time.time)
                        continue;
                }
                
                if (kvp.Value > bestScore)
                {
                    bestScore = kvp.Value;
                    bestAlt = kvp.Key;
                }
            }
            
            // 如果我们找到一个有效的替代方案
            if (bestScore > 0f) return bestAlt;
            
            // 如果没有有效的替代方案，我们可能不得不坚持现有的或提议的
             if (_behaviorHistory.ContainsKey("Current"))
                 return _behaviorHistory["Current"].CurrentAction;
                 
            return proposedAction;
        }
        
        private void UpdateBehaviorHistory(string action)
        {
            if (!_behaviorHistory.ContainsKey(action))
            {
                _behaviorHistory[action] = new BehaviorHistory();
            }
            
            var history = _behaviorHistory[action];
            
            // 检查全球当前情况
            if (!_behaviorHistory.ContainsKey("Current"))
            {
                 _behaviorHistory["Current"] = new BehaviorHistory { CurrentAction = action };
            }
            var current = _behaviorHistory["Current"];
            
            if (current.CurrentAction == action)
            {
                current.CurrentDuration += Time.deltaTime;
                history.CurrentDuration += Time.deltaTime; // 同时更新特定历史记录
            }
            else
            {
                // 已启动新操作
                current.CurrentAction = action;
                current.CurrentDuration = 0f;
                
                history.LastExecutionTime = Time.time;
                history.ExecutionCount++;
                history.CurrentDuration = 0f;
            }
        }
    }
}
