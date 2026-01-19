using UnityEngine;
using Duckov;
using Duckov.Utilities;
using Duckov.ItemUsage;
using System.Collections.Generic;
using ItemStatsSystem;

namespace BloodMoon
{
    // 上下文对象，用于在Controller和Actions之间传递数据
    public class AIContext
    {
        public CharacterMainControl? Character;
        public BloodMoonAIController? Controller;
        public AIDataStore? Store;
        public CharacterMainControl? Target;
        public AIPersonality Personality = new AIPersonality(); // 默认
        public AIRole Role = AIRole.Standard;
        
        // 目标持久性
        private float _targetSwitchCooldown;
        private const float MIN_TARGET_SWITCH_TIME = 3.0f; // 坚持目标的最小时间
        private float _timeWithCurrentTarget;
        
        // 感官数据
        public float DistToTarget;
        public Vector3 DirToTarget;
        public bool HasLoS;
        public float LastSeenTime;
        public Vector3 LastKnownPos;
        public float Pressure; // 0 到 10+
        
        // 状态标志
        public bool IsHurt;
        public bool IsLowAmmo;
        public bool IsReloading;
        public bool IsStuck;
        public bool CanChase;
        
        // 武器状态
        public Item? PrimaryWeapon;
        public Item? SecondaryWeapon;
        public Item? MeleeWeapon;
        public Item? ThrowableWeapon;
        public int AmmoCount;
        public float HealthPercentage;
        public bool IsInCombat => Pressure > 0 || HasLoS;
        
        // 目标状态
        public bool TargetIsReloading;
        public bool TargetIsHealing;

        public bool IsBoss => Controller != null && Controller.IsBoss;
        public bool IsRaged => Controller != null && Controller.IsRaged;
        public string SquadOrder = string.Empty; // 来自SquadManager的命令（例如"Flank", "Suppress"）

        // 目标获取的静态缓存（已弃用，改用Store.AllCharacters）
        private static List<CharacterMainControl> _cachedCharacters = new List<CharacterMainControl>();
        private static float _lastCacheTime;

        private static void CacheAllCharacters()
        {
            if (Time.time - _lastCacheTime < 2.0f) return; 
            _lastCacheTime = Time.time;
            _cachedCharacters.Clear();
            var allCharacters = UnityEngine.Object.FindObjectsOfType<CharacterMainControl>();
            foreach (var c in allCharacters)
            {
                if (c != null && c.gameObject.activeInHierarchy) _cachedCharacters.Add(c);
            }
        }

        public void UpdateSensors()
        {
            if (Character == null) return;

            // 更新目标持久性计时器
            if (_targetSwitchCooldown > 0f)
            {
                _targetSwitchCooldown -= Time.deltaTime;
            }

            if (Target != null)
            {
                _timeWithCurrentTarget += Time.deltaTime;
            }
            else
            {
                _timeWithCurrentTarget = 0f;
            }

            // 使用存储的全局缓存
            List<CharacterMainControl> targets;
            if (Store != null)
            {
                targets = Store.AllCharacters;
            }
            else
            {
                CacheAllCharacters();
                targets = _cachedCharacters;
            }

            // 2. 选择最佳目标
            CharacterMainControl? bestTarget = null;
            float bestScore = -99999f;
            Vector3 myPos = Character.transform.position;
            CharacterMainControl? currentTarget = Target;

            // 扫描其他敌对目标
            if (targets != null)
            {
                var myTeam = Character.Team;
                int count = targets.Count;
                
                // 确保玩家被考虑
                if (CharacterMainControl.Main != null && !targets.Contains(CharacterMainControl.Main))
                {
                    // 如果玩家尚未在缓存中（罕见），只需手动检查他
                    // 我们不能添加到存储列表，但可以在循环中检查他
                    // 只需强制单独检查玩家
                }

                for(int i=0; i<count; i++)
                {
                    var c = targets[i];
                    if (c == null || c == Character) continue;
                    if (c.Health.CurrentHealth <= 0) continue;
                    
                    // 敌对逻辑：攻击不同队伍
                    if (c.Team != myTeam) 
                    {
                        // 额外检查：确保目标不是BloodMoon AI（同一模组）
                        // 这防止了我们模组敌人之间的友军火力
                        bool isBloodMoonAI = c.GetComponent<BloodMoonAIController>() != null;
                        if (isBloodMoonAI) continue;
                        
                        float dist = Vector3.Distance(c.transform.position, myPos);
                        float score = 1000f - dist; // 基础分数：越近越好

                        // --- 优先级规则 ---
                        
                        // 1. 玩家优先级（巨大加成）
                        // 如果玩家活着，我们几乎总是想攻击他们，除非他们非常远
                        if (c == CharacterMainControl.Main)
                        {
                            score += 500f; // 相当于距离近了500米
                        }

                        // 2. 目标粘性（增强的滞后）
                        // 防止在相似价值的目标之间快速切换
                        if (c == currentTarget)
                        {
                            // 基础粘性加成
                            float stickinessBonus = 25f; // 从15f增加
                            
                            // 基于与目标相处时间的额外加成
                            stickinessBonus += Mathf.Min(30f, _timeWithCurrentTarget * 2f); // 与目标相处15秒最多30f加成
                            
                            score += stickinessBonus;
                        }
                        // 切换目标太快的惩罚
                        else if (currentTarget != null && _targetSwitchCooldown > 0f)
                        {
                            // 如果我们仍在冷却中，显著降低分数
                            score -= 100f;
                        }
                        
                        // 3. "忽略"玩家的距离上限
                        // 如果玩家非常远（>150米）而这里有一个原生敌人（<10米），
                        // 原生敌人的分数可能更高：
                        // 玩家分数：1000 - 150 + 500 = 1350
                        // 原生分数：1000 - 10 = 990
                        // 结果：仍然是玩家。
                        // 让我们调整一下。如果我们真的想在玩家不在或非常远时杀死原生敌人，
                        // 玩家加成处理了大多数情况。
                        // 但如果玩家在场，我们想要忽略原生敌人。
                        
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTarget = c;
                        }
                    }
                }
            }

            // 回退：如果未找到目标，但玩家活着，则目标为玩家
            if (bestTarget == null && CharacterMainControl.Main != null && CharacterMainControl.Main.Health.CurrentHealth > 0)
            {
                 // 仅当玩家不是盟友时
                 if (CharacterMainControl.Main.Team != Character.Team)
                    bestTarget = CharacterMainControl.Main;
            }

            // 检查目标是否正在改变
            if (bestTarget != currentTarget)
            {
                // 仅当从有效目标切换时才设置冷却
                if (currentTarget != null)
                {
                    _targetSwitchCooldown = MIN_TARGET_SWITCH_TIME;
                }
                _timeWithCurrentTarget = 0f;
            }

            Target = bestTarget;
            if (Target == null) return;
            
            var diff = Target.transform.position - Character.transform.position;
            diff.y = 0f;
            DistToTarget = diff.magnitude;
            DirToTarget = diff.normalized;
            
            // 基本视线检查（Controller应实现详细检查）
            if (Controller != null)
            {
                HasLoS = Controller.CheckLineOfSight(Target);
            }
            else
            {
                HasLoS = false;
            }
            
            if (HasLoS)
            {
                LastSeenTime = Time.time;
                LastKnownPos = Target.transform.position;
            }
            
            // 更新压力
            Pressure = Mathf.Max(0f, Pressure - Time.deltaTime * 0.5f);
            
            // 检查生命值
            HealthPercentage = Character.Health.CurrentHealth / Character.Health.MaxHealth;
            IsHurt = HealthPercentage < 0.4f;
            
            // 检查弹药和武器
            var gun = Character.GetGun();
            AmmoCount = gun != null ? gun.BulletCount : 0;
            IsLowAmmo = gun != null && gun.BulletCount < (gun.Capacity * 0.2f);
            IsReloading = gun != null && gun.IsReloading();
            
            PrimaryWeapon = Character.PrimWeaponSlot()?.Content;
            SecondaryWeapon = Character.SecWeaponSlot()?.Content;
            MeleeWeapon = Character.MeleeWeaponSlot()?.Content;
            
            // 检查可投掷/技能物品
            ThrowableWeapon = null;
            var inv = Character.CharacterItem?.Inventory;
            if (inv != null)
            {
                foreach(var item in inv) 
                {
                    if(item == null) continue;
                    var ss = item.GetComponent<ItemSetting_Skill>();
                    if(ss != null && ss.Skill != null && !item.GetComponent<Drug>()) 
                    {
                        ThrowableWeapon = item;
                        break;
                    }
                }
            }
            
            // 检查目标状态
            var tGun = Target.GetGun();
            TargetIsReloading = tGun != null && tGun.IsReloading();
            TargetIsHealing = Target.CurrentHoldItemAgent != null && Target.CurrentHoldItemAgent.Item.GetComponent<Drug>() != null; // 简化检查

            // 检查状态
            if (Controller != null)
            {
                CanChase = Controller.CanChase;
                Role = Controller.Role;
            }
        }
    }

    public abstract class AIAction
    {
        public string Name = string.Empty;
        protected float _score;
        protected float _cooldown;
        
        // 行为持续性
        protected float _timeInAction;
        protected float _minActionDuration;
        protected float _maxActionDuration;
        protected bool _shouldExit;
        
        public abstract float Evaluate(AIContext ctx);
        public abstract void Execute(AIContext ctx);
        public virtual void OnEnter(AIContext ctx) 
        { 
            _timeInAction = 0f;
            _shouldExit = false;
            // 根据动作类型设置默认最小/最大持续时间
            _minActionDuration = 1.0f;
            _maxActionDuration = 10.0f;
        }
        public virtual void OnExit(AIContext ctx) 
        { 
            _timeInAction = 0f;
            _shouldExit = false;
        }
        
        // 更新动作时间并检查是否应该继续
        public void UpdateActionTime(float dt)
        {
            _timeInAction += dt;
        }
        
        // 检查动作是否可以被中断
        public bool CanBeInterrupted()
        {
            return _timeInAction > _minActionDuration;
        }
        
        // 检查动作是否应该自动退出
        public bool ShouldExit()
        {
            return _shouldExit || _timeInAction > _maxActionDuration;
        }
        
        public bool IsCoolingDown() 
        { 
            return _cooldown > 0f; 
        }
        
        public void UpdateCooldown(float dt)
        {
            if (_cooldown > 0f) _cooldown -= dt;
        }
    }

    // --- 具体动作实现 ---

    public class Action_BossCommand : AIAction
    {
        public Action_BossCommand() { Name = "BossCommand"; }
        public override float Evaluate(AIContext ctx)
        {
            if (!ctx.IsBoss || IsCoolingDown()) return 0f;
            
            // 若附近有盟友则触发（由Controller处理检查）
            // 或在战斗中定期触发
            if (ctx.HasLoS || ctx.Pressure > 1f)
            {
                return 0.65f;
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller != null && ctx.Controller.PerformBossCommand())
            {
                _cooldown = 20f;
            }
            else
            {
                _cooldown = 5f; // 若失败则更快重试（无盟友）
            }
        }
    }

    public class Action_Heal : AIAction
    {
        public Action_Heal() { Name = "Heal"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Controller == null) return 0f;
            
            // 若已开始治疗但正在被攻击，则降低优先级
            if (ctx.Controller.IsHealing)
            {
                // 若正在被攻击（高压力或低生命值），允许中断
                if (ctx.Pressure > 3.0f || ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth < 0.3f)
                {
                    return 0.5f; // 降低优先级以允许攻击动作
                }
                return 0.8f; // 从0.98f降低以允许中断
            }

            // 优化：若无治疗物品则提前退出
            if (!ctx.Controller.HasHealingItem()) return 0f;

            float healthPct = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // 随着生命值下降线性增加
            float urgency = (1.0f - healthPct); // 例如，60%生命值时为0.4
            
            // 1. 危急生命值提升：若濒死则强制治疗
            if (healthPct < 0.5f) urgency += 0.2f;
            if (healthPct < 0.25f) urgency += 0.4f;

            // 2. 安全奖励：若隐藏，则抓住机会治疗
            if (!ctx.HasLoS) urgency += 0.2f;
            
            // 3. 压力惩罚：被攻击时增加惩罚
            // Boss受压力影响较小
            float pressurePenalty = ctx.Controller.IsBoss ? 0.1f : 0.3f;
            if (ctx.Pressure > 1.0f) urgency -= pressurePenalty;
            if (ctx.Pressure > 3.0f) urgency -= pressurePenalty * 2f; // 遭受猛烈火力时惩罚加倍
            
            // 4. 人格差异
            float personalityBonus = ctx.Personality.Caution * 0.2f; 
            urgency += personalityBonus;
            
            return Mathf.Clamp01(urgency);
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformHeal();
        }
    }

    public class Action_Rush : AIAction
    {
        public Action_Rush() { Name = "Rush"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Target == null || ctx.Character == null) return 0f;
            
            // 若生命值低则不冲锋（除非是狂暴的Boss）
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            if (hp < 0.4f && !ctx.IsRaged) return 0f;

            // 仅当目标脆弱时才冲锋
            if (ctx.TargetIsReloading || ctx.TargetIsHealing)
            {
                // 且我们相对较近
                if (ctx.DistToTarget < 20f && ctx.HasLoS)
                {
                    // Boss喜欢惩罚敌人
                    if (ctx.IsBoss) 
                    {
                         if (ctx.IsRaged) return 0.98f; // 狂暴的Boss几乎总是冲向脆弱的目标
                         return 0.9f;
                    }
                    return 0.75f;
                }
            }
            
            // 狂暴模式冲锋（即使目标不脆弱）
            if (ctx.IsRaged && ctx.HasLoS && ctx.DistToTarget < 15f)
            {
                return 0.85f;
            }

            // 人格：攻击性强的AI喜欢冲锋
            if (ctx.Personality.Aggression > 0.8f && ctx.HasLoS && ctx.DistToTarget < 25f)
            {
                return 0.5f * ctx.Personality.Aggression;
            }

            // 角色奖励：突击
            if (ctx.Role == AIRole.Assault && ctx.HasLoS && ctx.DistToTarget < 20f)
            {
                return 0.6f;
            }

            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformRush();
        }
    }

    public class Action_Reload : AIAction
    {
        public Action_Reload() { Name = "Reload"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.IsReloading) return 0.9f; // 若已开始则继续装填
            if (ctx.Character == null) return 0f;
            var gun = ctx.Character.GetGun();
            if (gun == null) return 0f;
            
            if (gun.BulletCount == 0) return 0.92f; // 空弹夹 -> 必须装填
            
            // 战术装填
            if (ctx.IsLowAmmo && !ctx.HasLoS) return 0.7f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            // 若有掩护且正在装填，是否移动到掩护处？
            // 由PerformReload处理，可能触发掩护逻辑
            ctx.Controller?.PerformReload();
        }
    }

    public class Action_ThrowGrenade : AIAction
    {
        public Action_ThrowGrenade() { Name = "ThrowGrenade"; }
        public override float Evaluate(AIContext ctx)
        {
            if (IsCoolingDown()) return 0f;
            if (ctx.Character == null || ctx.Target == null || ctx.Controller == null) return 0f;
            
            // 仅当目标在掩护中或我们没看到他们移动时才投掷
            // 目前，简单检查：距离8-25米，且我们有手榴弹
            if (ctx.DistToTarget < 8f || ctx.DistToTarget > 25f) return 0f;
            
            if (!ctx.Controller.HasGrenade()) return 0f;
            
            // 若目标静止不动（蹲点）或我们最近失去了视线但知道位置
            bool isCamping = Vector3.Distance(ctx.Target.transform.position, ctx.LastKnownPos) < 2.0f && (Time.time - ctx.LastSeenTime < 5f);
            
            // 若压力大或目标在隐藏，提高分数
            if ((!ctx.HasLoS || isCamping) && ctx.Pressure > 1f) return 0.82f;
            
            return 0.4f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller == null) return;
            if (ctx.Controller.PerformThrowGrenade())
            {
                _cooldown = 15f;
            }
            else
            {
                _cooldown = 2f;
            }
        }
    }

    public class Action_Retreat : AIAction
    {
        public Action_Retreat() { Name = "Retreat"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null) return 0f;
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // Boss不会轻易撤退
            if (ctx.Controller != null && ctx.Controller.IsBoss)
            {
                if (hp < 0.15f) return 0.5f; // 仅当危急时
                return 0f;
            }

            // 若濒死且承受压力
            if (hp < 0.3f && ctx.Pressure > 2.0f) return 0.95f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformRetreat();
        }
    }

    public class Action_TakeCover : AIAction
    {
        public Action_TakeCover() { Name = "TakeCover"; }
        public override float Evaluate(AIContext ctx)
        {
            if (IsCoolingDown()) return 0f;
            
            // 高压力 -> 掩护
            if (ctx.Pressure > 2.0f) return 0.85f;
            
            // 在开阔地带装填或治疗 -> 掩护
            if ((ctx.IsReloading || ctx.IsHurt) && ctx.HasLoS) return 0.88f;
            
            // 一般性谨慎
            if (ctx.HasLoS && ctx.DistToTarget < 10f) return 0.6f;
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Controller == null) return;
            if (ctx.Controller.MoveToCover())
            {
                // 若成功，设置冷却时间以避免频繁搜索掩护
                _cooldown = 2.0f;
            }
            else
            {
                // 未能找到掩护？恐慌/降低分数
                _cooldown = 1.0f; 
            }
        }
    }

    public class Action_Suppress : AIAction
    {
        public Action_Suppress() { Name = "Suppress"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Target == null) return 0f;
            
            // 没有弹药无法压制
            if (ctx.IsLowAmmo || ctx.IsReloading) return 0f;

            // 1. 小队战术：支援侧翼
            // 若盟友正在侧翼（我们可以推断或检查存储），我们应该压制
            if (ctx.Store != null)
            {
                // 检查是否有其他人正在交战？
                int engaging = ctx.Store.GetEngagementCount(ctx.Target);
                // 若我们是团队的一部分（2+人），应有一人压制
                // 使用人格：高团队合作的AI偏好压制
                if (engaging > 0 && ctx.DistToTarget > 20f && ctx.HasLoS)
                {
                    float score = 0.6f + ctx.Personality.Teamwork * 0.3f; // 高团队合作可达0.9
                    
                    // 角色奖励：支援
                    if (ctx.Role == AIRole.Support) score += 0.2f;
                    
                    return score;
                }
            }

            // 2. 盲目射击：若无视线但最近有接触
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f && (Time.time - ctx.LastSeenTime < 4f))
            {
                // 仅当有充足弹药时
                var gun = ctx.Character.GetGun();
                if (gun != null && gun.BulletCount > gun.Capacity * 0.5f)
                {
                     return 0.65f;
                }
            }
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformSuppression();
        }
    }

    public class Action_Engage : AIAction
    {
        public Action_Engage() { Name = "Engage"; }
        public override float Evaluate(AIContext ctx)
        {
            float engageDist = 30f;
            if (ctx.Character != null)
            {
                var gun = ctx.Character.GetGun();
                if (gun != null) engageDist = Mathf.Max(30f, gun.BulletDistance * 0.8f);
            }

            if (ctx.HasLoS && ctx.DistToTarget < engageDist)
            {
                // 基于距离的平滑交战分数
                float distFactor = Mathf.Clamp01((engageDist - ctx.DistToTarget) / engageDist);
                
                // 基于距离加权的基础分数
                float score = 0.3f + (distFactor * 0.4f); // 范围：0.3 到 0.7
                
                // 若安全（低压力）则加分 - 平滑过渡
                float pressureFactor = Mathf.Clamp01((3.0f - ctx.Pressure) / 3.0f);
                score += pressureFactor * 0.2f;
                
                // 若目标接近则加分 - 平滑过渡
                float closeRangeBonus = Mathf.Clamp01((15f - ctx.DistToTarget) / 15f);
                score += closeRangeBonus * 0.1f;
                
                // Boss更具攻击性 - 持续加分
                if (ctx.Controller != null && ctx.Controller.IsBoss) score += 0.15f;
                
                // Health Logic Update:
                float hp = ctx.Character != null ? ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth : 1f;
                
                // 若生命值危急且敌人接近 -> 殊死搏斗（困兽犹斗）
                if (hp < 0.3f && ctx.DistToTarget < 10f)
                {
                    // 基于生命值和距离的平滑殊死搏斗加分
                    float desperateFactor = Mathf.Clamp01((0.3f - hp) / 0.3f) * Mathf.Clamp01((10f - ctx.DistToTarget) / 10f);
                    score += desperateFactor * 0.4f; // 为生存而战！
                }
                // 仅当受伤但距离较远时才惩罚交战（因此可能撤退/治疗）
                else if (hp < 0.4f && ctx.DistToTarget > 15f) 
                {
                    float retreatFactor = Mathf.Clamp01((0.4f - hp) / 0.4f) * Mathf.Clamp01((ctx.DistToTarget - 15f) / 15f);
                    score -= retreatFactor * 0.15f;
                }

                return Mathf.Clamp01(score);
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Store != null && ctx.Target != null && ctx.Character != null)
                ctx.Store.RegisterEngagement(ctx.Target, ctx.Character);
            ctx.Controller?.EngageTarget();
        }
        public override void OnExit(AIContext ctx)
        {
            if (ctx.Store != null && ctx.Target != null && ctx.Character != null)
                ctx.Store.UnregisterEngagement(ctx.Target, ctx.Character);
        }
    }

    public class Action_Chase : AIAction
    {
        public Action_Chase() { Name = "Chase"; }
        public override float Evaluate(AIContext ctx)
        {
            if (!ctx.CanChase) return 0f;

            if (ctx.HasLoS)
            {
                 // 若太远则追逐
                 if (ctx.DistToTarget > 20f) return 0.6f;
                 return 0.1f; // 若有视线则优先Engage
            }
            
            // 无视线，但我们有最后已知位置
            if (ctx.LastSeenTime > 0f && Time.time - ctx.LastSeenTime < 20f)
            {
                return 0.55f;
            }
            
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.MoveTo(ctx.LastKnownPos);
        }
    }

    public class Action_Patrol : AIAction
    {
        public Action_Patrol() { Name = "Patrol"; }
        public override float Evaluate(AIContext ctx)
        {
            // 若我们没有更好的事情要做（无目标，未卡住）
            if (ctx.Target == null && !ctx.IsStuck)
            {
                // Patrol是默认状态
                return 0.2f;
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformPatrol();
        }
    }

    public class Action_Search : AIAction
    {
        public Action_Search() { Name = "Search"; }
        public override float Evaluate(AIContext ctx)
        {
            // 若全局情报显示玩家在其他地方则提升优先级
             if (ctx.Store != null && ctx.Store.LastKnownPlayerPos != Vector3.zero && ctx.Character != null)
             {
                  float d = Vector3.Distance(ctx.Store.LastKnownPlayerPos, ctx.Character.transform.position);
                  if (d < 40f && !ctx.HasLoS) return 0.45f;
             }

            // 若我们失去目标一段时间
            if (!ctx.HasLoS && (Time.time - ctx.LastSeenTime > 5f))
            {
                return 0.4f;
            }
            return 0.05f; // 低优先级默认值
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformSearch();
        }
    }

    public class Action_Panic : AIAction
    {
        public Action_Panic() { Name = "Panic"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null) return 0f;
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            
            // 若生命值低且压力大则恐慌
            if (hp < 0.25f && ctx.Pressure > 3.0f)
            {
                 // Boss不会轻易恐慌
                 if (ctx.IsBoss) return 0f;
                 return 1.0f; // 覆盖所有其他动作
            }
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformPanic();
        }
    }

    public class Action_Flank : AIAction
    {
        public Action_Flank() { Name = "Flank"; }
        public override float Evaluate(AIContext ctx)
        {
            if (ctx.Character == null || ctx.Target == null) return 0f;

            // 小队命令覆盖
            if (ctx.SquadOrder == "Flank") return 0.95f;

            // 1. 基本侧翼条件：良好的生命值，适当的距离
            float hp = ctx.Character.Health.CurrentHealth / ctx.Character.Health.MaxHealth;
            if (hp < 0.4f) return 0f; // 受伤时不侧翼

            // 2. 小队战术：若目标被压制或正在与他人交战
            if (ctx.Store != null)
            {
                int engaging = ctx.Store.GetEngagementCount(ctx.Target);
                // 若2+盟友正在交战，或1个盟友正在压制，我们应该侧翼
                if (engaging >= 2) return 0.75f;
                
                // 角色奖励：突击兵喜欢在有人交战时侧翼
                if (ctx.Role == AIRole.Assault && engaging >= 1) return 0.8f;
            }
            
            // 3. 情况：若我们有视线但处于次优范围/角度
            // 检查我们是否在目标的"前方"（危险）
            Vector3 toMe = (ctx.Character.transform.position - ctx.Target.transform.position).normalized;
            float angle = Vector3.Angle(ctx.Target.transform.forward, toMe);
            if (angle < 45f && ctx.HasLoS && ctx.Pressure < 2f)
            {
                // 我们在他们面前，尝试从侧面侧翼
                return 0.6f;
            }

            // 4. 若无视线但我们知道位置，侧翼而非直接追逐
            if (!ctx.HasLoS && ctx.LastSeenTime > 0f && Time.time - ctx.LastSeenTime < 10f)
            {
                return 0.5f;
            }

            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            if (ctx.Character == null) return;
            // 使用控制器中的战术侧翼逻辑
            ctx.Controller?.PerformTacticalFlank();
        }
    }

    public class Action_Unstuck : AIAction
    {
        public Action_Unstuck() { Name = "Unstuck"; }
        public override float Evaluate(AIContext ctx)
        {
            // 若控制器报告卡住
            if (ctx.IsStuck) return 1.0f; // 最高优先级
            return 0f;
        }
        public override void Execute(AIContext ctx)
        {
            ctx.Controller?.PerformUnstuck();
        }
    }
}
