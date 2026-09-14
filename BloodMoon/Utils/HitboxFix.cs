using UnityEngine;

namespace BloodMoon.Utils
{
    /// <summary>
    /// 受击体（hitbox）修复。
    ///
    /// 2026-09-10 玩家反馈：**风暴机器人打不动，子弹直接穿过去**。
    ///
    /// 从游戏代码看，"子弹打中并造成伤害"需要同时满足（<c>Projectile.cs:138,313-325</c>）：
    ///   1. 子弹的 SphereCast 命中一个碰撞体，且该碰撞体所在 Layer 属于
    ///      <c>damageReceiverLayerMask</c>；
    ///   2. 那个碰撞体所在 GameObject 上有 <c>DamageReceiver</c>；
    ///   3. <c>DamageReceiver.Team</c> 与子弹阵营不同（否则算友军，直接跳过）。
    /// 而 <c>DamageReceiver.Start()</c> 才会把自身 Layer 设成 "DamageReceiver"（<c>DamageReceiver.cs:59-66</c>），
    /// 并且大型 Boss 的受击胶囊是按"头盔插槽上的 HeadCollider"来定尺寸的
    /// （<c>CharacterMainControl.cs:1946-1958</c>）—— 没有头盔插槽 / 模型远大于胶囊时，
    /// 就会出现"看着打中了，其实子弹从碰撞体旁边穿过去"。
    ///
    /// 本类做三件保守的补救（只补不削）：
    ///   1. 把被接管的角色身上所有 <c>DamageReceiver</c> 的 Layer 校正为 "DamageReceiver"；
    ///   2. 若某个 DamageReceiver 所在对象处于**未激活**状态（子弹必然穿过），把它激活；
    ///   3. 若主受击体的胶囊明显小于模型渲染包围盒（>1.5 倍），把胶囊放大到与模型匹配。
    /// 每一步都会打日志，方便确认"打不动"的 Boss 到底卡在哪一环。
    /// </summary>
    public static class HitboxFix
    {
        private static int _damageReceiverLayer = -2;   // -2 = 未初始化；-1 = 游戏里没有这个 Layer
        private const float GrowThreshold = 1.5f;
        /// <summary>放大倍数上限（相对原胶囊高度）</summary>
        private const float MaxGrowFactor = 3.0f;
        /// <summary>放大后的世界高度上限（米）</summary>
        private const float MaxWorldHeight = 6.0f;
        /// <summary>模型包围盒超过这个高度就认为不可信（多半混进了特效/巨大子物体），放弃修复</summary>
        private const float ImplausibleModelHeight = 12.0f;

        /// <summary>已经体检过的角色（InstanceID），避免反复处理</summary>
        private static readonly System.Collections.Generic.HashSet<int> _scanned = new System.Collections.Generic.HashSet<int>();

        /// <summary>切图时清空扫描缓存（新场景的角色要重新体检）</summary>
        public static void ResetScanCache()
        {
            _scanned.Clear();
        }

        /// <summary>
        /// 全场扫描：给**所有**敌人做受击体体检（不限于 BloodMoon 自己刷的）。
        ///
        /// 为什么需要：实机日志里 <c>Enhanced existing boss</c> 一直是 0，说明 BloodMoon 从没接管过
        /// 游戏原生 Boss —— 而玩家反馈"打不动"的风暴机器人很可能就是原生 Boss。
        /// 只修自己刷的角色覆盖不到它，所以这里对场景里所有存活且非玩家阵营的角色做一次体检。
        /// 每个角色只做一次（实例 ID 记账），每 tick 限量处理，避免集中开销。
        /// </summary>
        public static int ScanAllCharacters(System.Collections.Generic.List<CharacterMainControl>? characters, int maxPerTick = 6)
        {
            if (characters == null || characters.Count == 0) return 0;
            var cfg = ModConfig.Instance;
            if (cfg != null && (!cfg.FixBossHitbox || !cfg.FixHitboxForAllEnemies)) return 0;

            int applied = 0;
            for (int i = 0; i < characters.Count && applied < maxPerTick; i++)
            {
                var c = characters[i];
                if (c == null) continue;
                if (c.IsMainCharacter) continue;                 // 绝不动玩家
                if (c.Team == Teams.player) continue;            // 宠物/友军也不动

                int id = c.GetInstanceID();
                if (_scanned.Contains(id)) continue;
                _scanned.Add(id);

                // 全场扫描时**不激活**处于未激活状态的受击体：那可能是脚本故意关闭的部位
                // （例如"护甲板未打开前打不到核心"）。只做 Layer/碰撞体/胶囊尺寸的保守修复。
                Apply(c, "scan", allowActivate: false);
                applied++;
            }
            return applied;
        }

        private static int DamageReceiverLayer
        {
            get
            {
                if (_damageReceiverLayer == -2)
                {
                    _damageReceiverLayer = LayerMask.NameToLayer("DamageReceiver");
                    if (_damageReceiverLayer < 0)
                    {
                        Logger.Warning("[HitboxFix] Layer 'DamageReceiver' not found, layer fix disabled");
                    }
                }
                return _damageReceiverLayer;
            }
        }

        /// <summary>
        /// 对被 BloodMoon 接管的角色做一次受击体体检与修复。
        /// </summary>
        /// <param name="c">目标角色</param>
        /// <param name="context">调用来源，写进日志</param>
        /// <param name="allowActivate">是否允许激活"未激活"的受击体。接管角色时为 true；
        /// 全场扫描时为 false（未激活可能是脚本故意为之，比如护甲板未打开的核心）。</param>
        public static void Apply(CharacterMainControl? c, string context, bool allowActivate = true)
        {
            if (c == null) return;
            var cfg = ModConfig.Instance;
            if (cfg != null && !cfg.FixBossHitbox) return;

            try
            {
                var receivers = c.GetComponentsInChildren<DamageReceiver>(true);
                if (receivers == null || receivers.Length == 0)
                {
                    Logger.Warning($"[HitboxFix] {context} {c.name}: no DamageReceiver found in hierarchy — bullets will pass through");
                    return;
                }

                int layerFixed = 0, activated = 0, colliderEnabled = 0;
                for (int i = 0; i < receivers.Length; i++)
                {
                    var dr = receivers[i];
                    if (dr == null) continue;
                    var go = dr.gameObject;

                    // 2) 未激活的受击体 = 子弹必然穿过
                    if (allowActivate && !go.activeSelf)
                    {
                        go.SetActive(true);
                        activated++;
                    }

                    // 1) Layer 校正（DamageReceiver.Start 才会设，可能没跑到）
                    if (DamageReceiverLayer >= 0 && go.layer != DamageReceiverLayer)
                    {
                        go.layer = DamageReceiverLayer;
                        layerFixed++;
                    }

                    // 碰撞体必须是启用的，否则 SphereCast 命中不到
                    var colliders = dr.GetComponents<Collider>();
                    for (int k = 0; k < colliders.Length; k++)
                    {
                        var col = colliders[k];
                        if (col != null && !col.enabled)
                        {
                            col.enabled = true;
                            colliderEnabled++;
                        }
                    }
                }

                int resized = GrowMainCapsuleToModel(c);

                if (layerFixed > 0 || activated > 0 || colliderEnabled > 0 || resized > 0)
                {
                    Logger.Log($"[HitboxFix] {context} {c.name}: receivers={receivers.Length} layerFixed={layerFixed} " +
                               $"activated={activated} collidersEnabled={colliderEnabled} capsuleGrown={resized}");
                }
            }
            catch (System.Exception e)
            {
                Logger.Warning($"[HitboxFix] Apply failed on {c.name}: {e.Message}");
            }
        }

        /// <summary>
        /// 大模型 Boss：把主受击体的胶囊放大到与模型渲染包围盒匹配（只放大，不缩小）。
        /// </summary>
        private static int GrowMainCapsuleToModel(CharacterMainControl c)
        {
            var dr = c.mainDamageReceiver;
            if (dr == null) return 0;

            var capsule = dr.GetComponent<CapsuleCollider>();
            if (capsule == null) return 0;

            var modelTransform = c.characterModel != null ? c.characterModel.transform : c.transform;
            var renderers = modelTransform.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return 0;

            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer) continue;      // 特效不算模型体积
                if (r is TrailRenderer) continue;
                if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!hasBounds) return 0;

            float scaleY = Mathf.Abs(dr.transform.lossyScale.y);
            if (scaleY <= 0.0001f) scaleY = 1f;

            float currentWorldHeight = capsule.height * scaleY;
            float modelWorldHeight = bounds.size.y;
            if (modelWorldHeight <= currentWorldHeight * GrowThreshold) return 0;   // 尺寸正常，不动

            // 修复记录：2026-09-10 实机里一个"风暴机器人" Boss 的包围盒算出 15.64m，
            // 直接把受击胶囊撑成 15 米（等于隐身巨墙），这是**不可接受**的 ——
            // 说明包围盒里混进了特效 Renderer 或巨大子物体。这里加两道保险：
            if (modelWorldHeight > ImplausibleModelHeight)
            {
                Logger.Warning($"[HitboxFix] {c.name}: model bounds {modelWorldHeight:F2}m looks inflated " +
                               $"(receivers={dr.name}), skip capsule grow");
                return 0;
            }

            float targetWorldHeight = Mathf.Min(modelWorldHeight,
                                                Mathf.Min(currentWorldHeight * MaxGrowFactor, MaxWorldHeight));
            if (targetWorldHeight <= currentWorldHeight) return 0;

            float newHeight = targetWorldHeight / scaleY;
            Vector3 localCenter = dr.transform.InverseTransformPoint(bounds.center);

            capsule.height = newHeight;
            capsule.center = new Vector3(capsule.center.x, localCenter.y, capsule.center.z);

            // 半径也按模型水平尺寸适度放大（限制在合理范围，避免变成一堵墙）
            float scaleX = Mathf.Abs(dr.transform.lossyScale.x);
            if (scaleX <= 0.0001f) scaleX = 1f;
            float horizontal = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f / scaleX;
            if (horizontal > capsule.radius)
            {
                capsule.radius = Mathf.Min(horizontal, capsule.radius * 2f);
                capsule.radius = Mathf.Min(capsule.radius, 2.5f);
            }

            Logger.Log($"[HitboxFix] {c.name}: capsule grown {currentWorldHeight:F2}m -> {targetWorldHeight:F2}m " +
                       $"(model {modelWorldHeight:F2}m, r={capsule.radius:F2})");
            return 1;
        }

    }
}