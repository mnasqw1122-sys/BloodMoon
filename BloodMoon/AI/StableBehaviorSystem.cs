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
            // If we are already running an action, check if we should stick with it
            if (_behaviorHistory.ContainsKey("Current"))
            {
                var current = _behaviorHistory["Current"];
                
                // If the current action is still valid (score > 0) AND we haven't run it long enough
                if (current.CurrentAction == proposedAction)
                {
                     UpdateBehaviorHistory(proposedAction);
                     return proposedAction;
                }
                
                // If we are trying to switch
                if (current.CurrentDuration < _minActionDuration)
                {
                    // If the new action is MUCH better (e.g. score diff > 0.3), allow switch
                    // Otherwise stick to current
                    if (allScores.ContainsKey(current.CurrentAction))
                    {
                        float currentScore = allScores[current.CurrentAction];
                        if (actionScore < currentScore + 0.3f)
                        {
                            // Stick to current
                            UpdateBehaviorHistory(current.CurrentAction);
                            return current.CurrentAction;
                        }
                    }
                }
            }
            
            // Check specific action cooldowns
            if (_behaviorHistory.ContainsKey(proposedAction))
            {
                var history = _behaviorHistory[proposedAction];
                if (history.LastExecutionTime + _cooldownBetweenActions > Time.time)
                {
                    // This action is on cooldown, find alternative
                    return GetAlternativeAction(proposedAction, allScores);
                }
            }
            
            UpdateBehaviorHistory(proposedAction);
            return proposedAction;
        }
        
        private string GetAlternativeAction(string proposedAction, Dictionary<string, float> scores)
        {
            string bestAlt = proposedAction; // Default back if no better found
            float bestScore = -1f;
            
            foreach (var kvp in scores)
            {
                if (kvp.Key == proposedAction) continue;
                
                // Check if this alt is on cooldown
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
            
            // If we found a valid alternative
            if (bestScore > 0f) return bestAlt;
            
            // If no valid alternative, we might have to stick with proposed or current
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
            
            // Check global current
            if (!_behaviorHistory.ContainsKey("Current"))
            {
                 _behaviorHistory["Current"] = new BehaviorHistory { CurrentAction = action };
            }
            var current = _behaviorHistory["Current"];
            
            if (current.CurrentAction == action)
            {
                current.CurrentDuration += Time.deltaTime;
                history.CurrentDuration += Time.deltaTime; // Also update specific history
            }
            else
            {
                // New action started
                current.CurrentAction = action;
                current.CurrentDuration = 0f;
                
                history.LastExecutionTime = Time.time;
                history.ExecutionCount++;
                history.CurrentDuration = 0f;
            }
        }
    }
}
