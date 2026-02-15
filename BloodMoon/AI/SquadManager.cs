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
            
            foreach (var squad in _squads.ToList())
            {
                squad.RemoveMember(ai);
                if (squad.Members.Count == 0)
                {
                    _squads.Remove(squad);
                }
                else if (squad.Leader == ai && squad.Members.Count > 0)
                {
                    squad.Leader = squad.Members[0];
                }
            }
        }

        public void Update()
        {
            _updateTimer += Time.deltaTime;
            
            if (_updateTimer >= 0.5f)
            {
                _updateTimer = 0f;
                TryFormSquads();
                UpdateSquadCenters();
                AssignSquadOrders();
            }
        }

        private void TryFormSquads()
        {
            while (_unassigned.Count > 0)
            {
                var newSquad = new Squad { ID = _nextSquadId++ };
                newSquad.Leader = _unassigned[0];
                newSquad.AddMember(_unassigned[0]);
                _unassigned.RemoveAt(0);
                
                int desiredSize = Random.Range(2, 5);
                while (_unassigned.Count > 0 && newSquad.Members.Count < desiredSize)
                {
                    var candidate = _unassigned[0];
                    if (Vector3.Distance(candidate.transform.position, newSquad.Leader.transform.position) < 30f)
                    {
                        newSquad.AddMember(candidate);
                        _unassigned.RemoveAt(0);
                    }
                    else
                    {
                        break;
                    }
                }
                
                _squads.Add(newSquad);
            }
        }

        private void UpdateSquadCenters()
        {
            foreach (var squad in _squads)
            {
                if (!squad.IsValid()) continue;
                
                Vector3 center = Vector3.zero;
                int validCount = 0;
                
                foreach (var member in squad.Members)
                {
                    if (member != null && member.isActiveAndEnabled)
                    {
                        center += member.transform.position;
                        validCount++;
                    }
                }
                
                if (validCount > 0)
                {
                    squad.SquadCenter = center / validCount;
                }
            }
        }

        private void AssignSquadOrders()
        {
            foreach (var squad in _squads)
            {
                if (!squad.IsValid()) continue;
                
                _coordinator.CoordinateSquad(squad);
            }
        }

        public Squad? GetSquadForAI(BloodMoonAIController ai)
        {
            foreach (var squad in _squads)
            {
                if (squad.Members.Contains(ai))
                {
                    return squad;
                }
            }
            return null;
        }

        public List<Squad> GetAllSquads()
        {
            return new List<Squad>(_squads);
        }

        public string? GetOrder(BloodMoonAIController ai)
        {
            var squad = GetSquadForAI(ai);
            if (squad != null && squad.MemberOrders.ContainsKey(ai))
            {
                return squad.MemberOrders[ai];
            }
            return null;
        }
    }
}
