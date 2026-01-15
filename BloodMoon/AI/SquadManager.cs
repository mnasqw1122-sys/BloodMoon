using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BloodMoon.Utils;
using Duckov;

namespace BloodMoon.AI
{
    public class Squad
    {
        public int ID;
        public BloodMoonAIController? Leader;
        public List<BloodMoonAIController> Members = new List<BloodMoonAIController>();
        public CharacterMainControl? Target;
        public Vector3 SquadCenter;
        
        // Orders
        public Dictionary<BloodMoonAIController, string> MemberOrders = new Dictionary<BloodMoonAIController, string>();

        public void AddMember(BloodMoonAIController member)
        {
            if (!Members.Contains(member))
            {
                Members.Add(member);
                member.SetSquad(this);
            }
        }

        public void RemoveMember(BloodMoonAIController member)
        {
            if (Members.Contains(member))
            {
                Members.Remove(member);
                MemberOrders.Remove(member);
                member.SetSquad(null);
            }
        }
        
        public bool IsValid()
        {
            return Members.Count > 0 && Leader != null && Leader.isActiveAndEnabled;
        }
    }

    public class SquadManager
    {
        private static SquadManager _instance = null!;
        public static SquadManager Instance => _instance;

        private List<Squad> _squads = new List<Squad>();
        private List<BloodMoonAIController> _unassigned = new List<BloodMoonAIController>();
        private float _updateTimer;
        private int _nextSquadId = 1;
        
        private IntelligentSquadCoordinator _coordinator = null!;

        public void Initialize()
        {
            _instance = this;
            _coordinator = new IntelligentSquadCoordinator();
            _coordinator.Initialize();
            BloodMoon.Utils.Logger.Log("SquadManager Initialized");
        }

        public void RegisterAI(BloodMoonAIController ai)
        {
            if (!_unassigned.Contains(ai)) _unassigned.Add(ai);
        }

        public void UnregisterAI(BloodMoonAIController ai)
        {
            if (_unassigned.Contains(ai)) _unassigned.Remove(ai);
            
            // Remove from squads
            foreach (var squad in _squads)
            {
                if (squad.Members.Contains(ai))
                {
                    squad.RemoveMember(ai);
                    if (squad.Leader == ai)
                    {
                        // Elect new leader or disband
                        if (squad.Members.Count > 0) squad.Leader = squad.Members[0];
                    }
                }
            }
        }

        public void Update()
        {
            _updateTimer += Time.deltaTime;
            if (_updateTimer < 1.0f) return;
            _updateTimer = 0f;

            // 1. Manage Squads
            ManageSquads();

            // 2. Issue Orders
            _coordinator.UpdateSquadCoordination();

            // Cleanup empty squads
            _squads.RemoveAll(s => !s.IsValid() || s.Members.Count == 0);
        }

        private void ManageSquads()
        {
            // Try to form squads from unassigned
            if (_unassigned.Count < 2) return;

            // Simple clustering
            // Pick a random unassigned, find neighbors
            for (int i = _unassigned.Count - 1; i >= 0; i--)
            {
                var candidate = _unassigned[i];
                if (candidate == null) 
                {
                    _unassigned.RemoveAt(i);
                    continue;
                }

                var nearby = _unassigned.Where(other => other != candidate && other != null && Vector3.Distance(other.transform.position, candidate.transform.position) < 15f).ToList();
                
                if (nearby.Count >= 2) // Found a potential trio
                {
                    var newSquad = new Squad { ID = _nextSquadId++ };
                    newSquad.Leader = candidate; // Default leader
                    
                    // Add candidate
                    newSquad.AddMember(candidate);
                    _unassigned.RemoveAt(i);

                    // Add neighbors
                    foreach (var n in nearby)
                    {
                        newSquad.AddMember(n);
                        _unassigned.Remove(n);
                    }

                    // Elect best leader (Boss > High HP > Random)
                    var boss = newSquad.Members.FirstOrDefault(m => m.IsBoss);
                    if (boss != null) newSquad.Leader = boss;
                    
                    _squads.Add(newSquad);
                    _coordinator.RegisterSquad(newSquad);
                    BloodMoon.Utils.Logger.Debug($"Formed Squad {newSquad.ID} with {newSquad.Members.Count} members");
                    
                    // Break to avoid modifying list while iterating too much (though we iterate backwards, neighbors removal might affect indices, but simple approach is fine for now)
                    break; 
                }
            }
        }

        private void UpdateSquadTactics(Squad squad)
        {
            // Update Center
            Vector3 center = Vector3.zero;
            foreach (var m in squad.Members) center += m.transform.position;
            squad.SquadCenter = center / squad.Members.Count;

            // Determine Target (Leader's target)
            // We need access to AIContext, but that's internal. 
            // We'll rely on a public property or method we will add to Controller.
            // For now, assume we can get it or just pick one.
            
            // Let's assume we add `public CharacterMainControl CurrentTarget` to controller
            if (squad.Leader != null)
            {
                squad.Target = squad.Leader.CurrentTarget;
            }

            if (squad.Target == null) return;

            // Assign Roles
            // 1 Flanker, 1 Suppressor, Rest Assault
            
            int flankers = 0;
            int suppressors = 0;
            
            foreach (var member in squad.Members)
            {
                if (member == squad.Leader) continue; // Leader does what they want

                string order = "Assault";

                // Logic:
                // If we need flanker and member is Assault/Sniper role -> Flank
                // If we need suppressor and member is Support/Standard -> Suppress
                
                if (flankers < 1)
                {
                    order = "Flank";
                    flankers++;
                }
                else if (suppressors < 1)
                {
                    order = "Suppress";
                    suppressors++;
                }
                
                squad.MemberOrders[member] = order;
            }
        }
        
        public string? GetOrder(BloodMoonAIController ai)
        {
            foreach (var s in _squads)
            {
                if (s.Members.Contains(ai))
                {
                    if (s.MemberOrders.TryGetValue(ai, out var order)) return order;
                    if (ai == s.Leader) return "Lead";
                }
            }
            return null;
        }
    }
}
