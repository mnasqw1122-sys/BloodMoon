using System.Collections.Generic;
using UnityEngine;

namespace BloodMoon.AI
{
    /// <summary>
    /// 行为历史记录类，存储动作的执行信息
    /// </summary>
    public class BehaviorHistory
    {
        public string CurrentAction { get; set; } = string.Empty;
        public float CurrentDuration { get; set; }
        public float LastExecutionTime { get; set; }
        public int ExecutionCount { get; set; }
    }

    /// <summary>
    /// 稳定行为系统，确保AI行为的稳定性和一致性
    /// </summary>
    public class StableBehaviorSystem
    {
        private Dictionary<string, BehaviorHistory> _behaviorHistory = null!;
        private float _minActionDuration = 2.0f;
        private float _cooldownBetweenActions = 0.5f;
        
        /// <summary>
        /// 初始化稳定行为系统
        /// </summary>
        public void Initialize()
        {
            _behaviorHistory = new Dictionary<string, BehaviorHistory>();
        }
        
        /// <summary>
        /// 获取稳定的动作，确保AI动作的稳定性
        /// </summary>
        /// <param name="proposedAction">提议的动作</param>
        /// <param name="actionScore">动作分数</param>
        /// <param name="ctx">AI上下文</param>
        /// <param name="availableActions">可用动作列表</param>
        /// <param name="allScores">所有动作的分数</param>
        /// <returns>稳定的动作</returns>
        public string GetStableAction(string proposedAction, float actionScore, AIContext ctx, List<string> availableActions, Dictionary<string, float> allScores)
        {
            if (_behaviorHistory.ContainsKey("Current"))
            {
                var current = _behaviorHistory["Current"];
                
                if (current.CurrentAction == proposedAction)
                {
                    UpdateBehaviorHistory(proposedAction);
                    return proposedAction;
                }
                
                if (current.CurrentDuration < _minActionDuration)
                {
                    if (allScores.ContainsKey(current.CurrentAction))
                    {
                        float currentScore = allScores[current.CurrentAction];
                        if (actionScore < currentScore + 0.3f)
                        {
                            UpdateBehaviorHistory(current.CurrentAction);
                            return current.CurrentAction;
                        }
                    }
                }
            }
            
            if (_behaviorHistory.ContainsKey(proposedAction))
            {
                var history = _behaviorHistory[proposedAction];
                if (history.LastExecutionTime + _cooldownBetweenActions > Time.time)
                {
                    return GetAlternativeAction(proposedAction, allScores);
                }
            }
            
            UpdateBehaviorHistory(proposedAction);
            return proposedAction;
        }
        
        /// <summary>
        /// 获取替代动作
        /// </summary>
        /// <param name="proposedAction">提议的动作</param>
        /// <param name="scores">动作分数</param>
        /// <returns>替代动作</returns>
        private string GetAlternativeAction(string proposedAction, Dictionary<string, float> scores)
        {
            string bestAlt = proposedAction;
            float bestScore = -1f;
            
            foreach (var kvp in scores)
            {
                if (kvp.Key == proposedAction) continue;
                
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
            
            if (bestScore > 0f) return bestAlt;
            
            if (_behaviorHistory.ContainsKey("Current"))
                return _behaviorHistory["Current"].CurrentAction;
                
            return proposedAction;
        }
        
        /// <summary>
        /// 更新行为历史
        /// </summary>
        /// <param name="action">动作</param>
        private void UpdateBehaviorHistory(string action)
        {
            if (!_behaviorHistory.ContainsKey(action))
            {
                _behaviorHistory[action] = new BehaviorHistory();
            }
            
            var history = _behaviorHistory[action];
            
            if (!_behaviorHistory.ContainsKey("Current"))
            {
                _behaviorHistory["Current"] = new BehaviorHistory { CurrentAction = action };
            }
            var current = _behaviorHistory["Current"];
            
            if (current.CurrentAction == action)
            {
                current.CurrentDuration += Time.deltaTime;
                history.CurrentDuration += Time.deltaTime;
            }
            else
            {
                current.CurrentAction = action;
                current.CurrentDuration = 0f;
                
                history.LastExecutionTime = Time.time;
                history.ExecutionCount++;
                history.CurrentDuration = 0f;
            }
        }
    }
}
