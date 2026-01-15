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
            
            // Clean up invalid squads
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
                CoordinateSquadActions(squadTactics);
                HandleSquadCommunication(squadTactics);
            }
        }
        
        private void UpdateSquadFormation(SquadTactics tactics)
        {
            var squad = tactics.Squad;
            if (squad.Members.Count == 0) return;
            
            // Calculate Squad Center
            Vector3 center = Vector3.zero;
            int activeMembers = 0;
            
            foreach (var member in squad.Members)
            {
                if (member != null && member.isActiveAndEnabled)
                {
                    center += member.transform.position;
                    activeMembers++;
                }
            }
            
            if (activeMembers > 0)
            {
                squad.SquadCenter = center / activeMembers;
            }
            
            // Update Formation Positions (Logic to be implemented if needed, for now just center calc)
            // UpdateFormationPositions(squad, tactics.CurrentFormation);
        }
        
        private void CoordinateSquadActions(SquadTactics tactics)
        {
            var squad = tactics.Squad;
            
            // Assess Situation
            var tacticalSituation = AssessTacticalSituation(squad);
            
            switch (tacticalSituation)
            {
                case TacticalSituation.Advancing:
                    ExecuteAdvancingTactics(squad);
                    break;
                    
                case TacticalSituation.Defending:
                    ExecuteDefendingTactics(squad);
                    break;
                    
                case TacticalSituation.Flanking:
                    ExecuteFlankingTactics(squad);
                    break;
                    
                case TacticalSituation.Retreating:
                    ExecuteRetreatingTactics(squad);
                    break;
                    
                default:
                    ExecuteStandardTactics(squad);
                    break;
            }
        }
        
        private void HandleSquadCommunication(SquadTactics tactics)
        {
            // Placeholder for voice lines or signals
        }
        
        private TacticalSituation AssessTacticalSituation(Squad squad)
        {
            if (squad.Members.Count == 0) return TacticalSituation.Standard;

            float avgHealth = squad.Members.Average(m => m.GetHealthPercentage());
            int membersWithWeapons = squad.Members.Count(m => m.HasWeapon);
            
            // Calculate average distance to target
            float totalDist = 0f;
            int count = 0;
            foreach(var m in squad.Members)
            {
                 if (m.CurrentTarget != null)
                 {
                     totalDist += Vector3.Distance(m.transform.position, m.CurrentTarget.transform.position);
                     count++;
                 }
            }
            float distanceToTarget = count > 0 ? totalDist / count : 100f;
            
            if (avgHealth < 0.3f)
                return TacticalSituation.Retreating;
            
            if (membersWithWeapons < squad.Members.Count / 2)
                return TacticalSituation.Defending;
            
            if (distanceToTarget < 15f && count > 0)
                return TacticalSituation.Advancing;
            
            if (distanceToTarget > 25f && count > 0)
                return TacticalSituation.Flanking;
            
            return TacticalSituation.Standard;
        }
        
        private void ExecuteAdvancingTactics(Squad squad)
        {
            // Assault members push, Support covers
            foreach (var member in squad.Members)
            {
                if (member.Role == AIRole.Assault || member.Role == AIRole.Standard)
                {
                    member.SetTacticalOrder("AdvanceAndEngage");
                }
                else if (member.Role == AIRole.Support || member.Role == AIRole.Sniper)
                {
                    member.SetTacticalOrder("ProvideCoveringFire");
                }
            }
        }
        
        private void ExecuteDefendingTactics(Squad squad)
        {
            foreach (var member in squad.Members)
            {
                member.SetTacticalOrder("HoldPosition");
            }
        }
        
        private void ExecuteFlankingTactics(Squad squad)
        {
            // Split squad: 2 flank, rest suppress
            int flankers = 0;
            foreach (var member in squad.Members)
            {
                if (flankers < 2 && (member.Role == AIRole.Assault || member.Role == AIRole.Standard))
                {
                    member.SetTacticalOrder("Flank");
                    flankers++;
                }
                else
                {
                    member.SetTacticalOrder("Suppress");
                }
            }
        }
        
        private void ExecuteRetreatingTactics(Squad squad)
        {
            foreach (var member in squad.Members)
            {
                member.SetTacticalOrder("Retreat");
            }
        }
        
        private void ExecuteStandardTactics(Squad squad)
        {
             foreach (var member in squad.Members)
            {
                member.SetTacticalOrder("Free");
            }
        }
    }
}
