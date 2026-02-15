using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BloodMoon.Utils;

namespace BloodMoon.AI
{
    public enum TacticalSituation
    {
        Standard,
        Advancing,
        Defending,
        Flanking,
        Retreating
    }

    public class SquadTactics
    {
        public Squad Squad { get; set; }
        public string CurrentFormation { get; set; } = "Line";
        
        public SquadTactics(Squad squad)
        {
            Squad = squad;
        }
    }

    public class IntelligentSquadCoordinator
    {
        private Dictionary<int, SquadTactics> _squadTactics = null!;
        private float _coordinationUpdateInterval = 1.0f;
        private float _lastUpdateTime;
        
        public void Initialize()
        {
            _squadTactics = new Dictionary<int, SquadTactics>();
        }
        
        public void RegisterSquad(Squad squad)
        {
            if (!_squadTactics.ContainsKey(squad.ID))
            {
                _squadTactics[squad.ID] = new SquadTactics(squad);
            }
        }
        
        public void UnregisterSquad(int squadId)
        {
            if (_squadTactics.ContainsKey(squadId))
            {
                _squadTactics.Remove(squadId);
            }
        }
        
        public void UpdateSquadCoordination()
        {
            if (Time.time - _lastUpdateTime < _coordinationUpdateInterval)
                return;
            
            _lastUpdateTime = Time.time;
            
            var keys = _squadTactics.Keys.ToList();
            foreach(var k in keys)
            {
                if (!_squadTactics[k].Squad.IsValid())
                {
                    _squadTactics.Remove(k);
                }
            }
            
            foreach (var squadTactics in _squadTactics.Values)
            {
                UpdateSquadFormation(squadTactics);
            }
        }
        
        public void CoordinateSquad(Squad squad)
        {
            if (!_squadTactics.ContainsKey(squad.ID))
            {
                _squadTactics[squad.ID] = new SquadTactics(squad);
            }
            
            var squadTactics = _squadTactics[squad.ID];
            TacticalSituation situation = EvaluateTacticalSituation(squad);
            
            AssignOrdersBySituation(squad, situation, squadTactics);
        }
        
        private TacticalSituation EvaluateTacticalSituation(Squad squad)
        {
            if (squad.Target == null)
            {
                return TacticalSituation.Standard;
            }
            
            int aliveMembers = squad.Members.Count(m => m != null && m.isActiveAndEnabled);
            float totalHealthPercent = 0f;
            foreach (var member in squad.Members)
            {
                if (member != null && member.Character != null)
                {
                    totalHealthPercent += member.Character.Health.CurrentHealth / member.Character.Health.MaxHealth;
                }
            }
            
            float avgHealth = aliveMembers > 0 ? totalHealthPercent / aliveMembers : 1f;
            float distToTarget = Vector3.Distance(squad.SquadCenter, squad.Target.transform.position);
            
            if (avgHealth < 0.3f)
            {
                return TacticalSituation.Retreating;
            }
            
            if (avgHealth > 0.7f && aliveMembers >= 3)
            {
                if (distToTarget > 20f)
                {
                    return TacticalSituation.Advancing;
                }
                else
                {
                    return TacticalSituation.Flanking;
                }
            }
            
            if (distToTarget < 15f)
            {
                return TacticalSituation.Defending;
            }
            
            return TacticalSituation.Standard;
        }
        
        private void AssignOrdersBySituation(Squad squad, TacticalSituation situation, SquadTactics squadTactics)
        {
            if (squad.Members.Count == 0) return;
            
            for (int i = 0; i < squad.Members.Count; i++)
            {
                var member = squad.Members[i];
                if (member == null) continue;
                
                string order = GetOrderForMember(i, squad.Members.Count, situation);
                squad.MemberOrders[member] = order;
            }
        }
        
        private string GetOrderForMember(int memberIndex, int totalMembers, TacticalSituation situation)
        {
            switch (situation)
            {
                case TacticalSituation.Advancing:
                    if (memberIndex == 0) return "Engage";
                    if (memberIndex == 1) return "Flank";
                    return "SuppressingFire";
                    
                case TacticalSituation.Defending:
                    return "TakeCover";
                    
                case TacticalSituation.Flanking:
                    if (memberIndex == 0) return "Engage";
                    if (memberIndex % 2 == 0) return "FlankLeft";
                    return "FlankRight";
                    
                case TacticalSituation.Retreating:
                    return "Retreat";
                    
                default:
                    if (memberIndex == 0) return "Engage";
                    if (memberIndex == 1) return "Cover";
                    return "Support";
            }
        }
        
        private void UpdateSquadFormation(SquadTactics squadTactics)
        {
            var squad = squadTactics.Squad;
            if (!squad.IsValid()) return;
            
            TacticalSituation situation = EvaluateTacticalSituation(squad);
            
            switch (situation)
            {
                case TacticalSituation.Advancing:
                    squadTactics.CurrentFormation = "Wedge";
                    break;
                case TacticalSituation.Defending:
                    squadTactics.CurrentFormation = "Circle";
                    break;
                case TacticalSituation.Flanking:
                    squadTactics.CurrentFormation = "Line";
                    break;
                case TacticalSituation.Retreating:
                    squadTactics.CurrentFormation = "Column";
                    break;
                default:
                    squadTactics.CurrentFormation = "Loose";
                    break;
            }
        }
    }
}
