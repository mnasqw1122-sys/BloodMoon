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
        
        public SquadTactics(Squad squad)
        {
            Squad = squad;
        }
    }

    public class IntelligentSquadCoordinator
    {
        private Dictionary<int, SquadTactics> _squadTactics = null!;
        
        public void Initialize()
        {
            _squadTactics = new Dictionary<int, SquadTactics>();
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
            // 命令字符串必须与 BloodMoonAIDecision 的动作名完全一致
            // （旧版 "SuppressingFire"/"FlankLeft"/"FlankRight"/"Cover"/"Support" 全部错配，无人消费）
            switch (situation)
            {
                case TacticalSituation.Advancing:
                    if (memberIndex == 0) return "Engage";
                    if (memberIndex == 1) return "Flank";
                    return "Suppress";
                    
                case TacticalSituation.Defending:
                    return "TakeCover";
                    
                case TacticalSituation.Flanking:
                    if (memberIndex == 0) return "Engage";
                    return "Flank";
                    
                case TacticalSituation.Retreating:
                    return "Retreat";
                    
                default:
                    if (memberIndex == 0) return "Engage";
                    if (memberIndex == 1) return "TakeCover";
                    return "Suppress";
            }
        }
    }
}
