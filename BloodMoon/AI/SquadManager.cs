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
                // 旧版：第一个候选距离 >30m 就 break，外层继续 → 大量 1-2 人小队。
                // 修复：遍历全部未编组 AI，把 30m 内的都收进来
                for (int i = 0; i < _unassigned.Count && newSquad.Members.Count < desiredSize; i++)
                {
                    var candidate = _unassigned[i];
                    if (Vector3.Distance(candidate.transform.position, newSquad.Leader.transform.position) < 30f)
                    {
                        newSquad.AddMember(candidate);
                        _unassigned.RemoveAt(i);
                        i--;
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

                // 小队目标取自队长当前目标（旧版 squad.Target 从未被设置 → 战术判定恒为 Standard）
                if (squad.Leader != null) squad.Target = squad.Leader.CurrentTarget;

                _coordinator.CoordinateSquad(squad);

                // 把订单真正推送给成员（旧版写了 MemberOrders 但无人读取）
                foreach (var kv in squad.MemberOrders)
                {
                    if (kv.Key != null) kv.Key.SetTacticalOrder(kv.Value);
                }
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
    }
}
