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
        
        // 命令
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
            
            // 从队伍中移除
            foreach (var squad in _squads)
            {
                if (squad.Members.Contains(ai))
                {
                    squad.RemoveMember(ai);
                    if (squad.Leader == ai)
                    {
                        // 选择新王或解散
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

            // 1. 管理小队
            ManageSquads();

            // 2. 发布命令
            _coordinator.UpdateSquadCoordination();

            // 清理空小队
            _squads.RemoveAll(s => !s.IsValid() || s.Members.Count == 0);
        }

        private void ManageSquads()
        {
            // 尽量从未分配人员中组建小队
            if (_unassigned.Count < 2) return;

            // 简单聚类
            // 随机选择一个未分配的节点，找到其邻居
            // 使用临时列表来避免在遍历过程中修改原列表
            List<BloodMoonAIController> tempUnassigned = new List<BloodMoonAIController>(_unassigned);
            
            for (int i = tempUnassigned.Count - 1; i >= 0; i--)
            {
                var candidate = tempUnassigned[i];
                if (candidate == null) 
                {
                    continue;
                }

                var nearby = tempUnassigned.Where(other => other != candidate && other != null && Vector3.Distance(other.transform.position, candidate.transform.position) < 15f).ToList();
                
                if (nearby.Count >= 2) // 发现了一个潜在的三人组合
                {
                    var newSquad = new Squad { ID = _nextSquadId++ };
                    newSquad.Leader = candidate; // 默认王
                    
                    // 添加候选人
                    newSquad.AddMember(candidate);
                    _unassigned.Remove(candidate);

                    // 添加邻居
                    foreach (var n in nearby)
                    {
                        newSquad.AddMember(n);
                        _unassigned.Remove(n);
                    }

                    // 选择最佳领袖（王 > 高生命值 > 随机）
                    var boss = newSquad.Members.FirstOrDefault(m => m.IsBoss);
                    if (boss != null) newSquad.Leader = boss;
                    
                    _squads.Add(newSquad);
                    _coordinator.RegisterSquad(newSquad);
                    BloodMoon.Utils.Logger.Debug($"Formed Squad {newSquad.ID} with {newSquad.Members.Count} members");
                    
                    // 拆分以避免在迭代过程中过多修改列表
                    break; 
                }
            }
        }

        private void UpdateSquadTactics(Squad squad)
        {
            // 更新中心
            Vector3 center = Vector3.zero;
            foreach (var m in squad.Members) center += m.transform.position;
            squad.SquadCenter = center / squad.Members.Count;

            // 确定目标（王的目标）
            // 我们需要访问AIContext，但这是内部的。
            // 我们将依赖于我们将添加到Controller的公共属性或方法。
            // 目前，假设我们可以获取它或只是选择一个。
            
            // 假设我们添加 `public CharacterMainControl CurrentTarget` 到控制器
            if (squad.Leader != null)
            {
                squad.Target = squad.Leader.CurrentTarget;
            }

            if (squad.Target == null) return;

            // 分配角色
            // 1名侧翼支援兵，1名压制兵，其余为突击兵
            
            int flankers = 0;
            int suppressors = 0;
            
            foreach (var member in squad.Members)
            {
                if (member == squad.Leader) continue; // 王想做什么就做什么

                string order = "Assault";

                // 逻辑：
                // 如果我们需要侧翼支援兵且成员是突击/狙击角色 ->  flank
                // 如果我们需要压制兵且成员是支援/标准角色 -> suppress
                
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
