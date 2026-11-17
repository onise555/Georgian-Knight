using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>
    /// Combat data rules: every AttackData's timing order and balance envelope, every MoveSet's required moves, action
    /// clip timings / dodge i-frames, and that each referenced state exists in the AnimatorController of the characters
    /// that use the MoveSet (falls back to all controllers when no prefab uses it).
    /// </summary>
    public sealed class CombatDataValidator : IValidator
    {
        public string Id { get { return "combat"; } }
        public string Category { get { return QaCategory.Data; } }

        public IEnumerable<QaIssue> Run(QaContext ctx)
        {
            var issues = new List<QaIssue>();
            foreach (var path in ctx.FindAssets("t:AttackData"))
            {
                var a = AssetDatabase.LoadAssetAtPath<AttackData>(path);
                if (a != null) Attack(path, a, ctx.Settings.combat, issues);
            }
            var users = ControllersByMoveSet(ctx);
            var allStates = new HashSet<string>();
            foreach (var p in ctx.FindAssets("t:AnimatorController")) CollectStates(AssetDatabase.LoadAssetAtPath<AnimatorController>(p), allStates);
            foreach (var path in ctx.FindAssets("t:MoveSet"))
            {
                var ms = AssetDatabase.LoadAssetAtPath<MoveSet>(path);
                if (ms == null) continue;
                HashSet<string> states;
                bool used = users.TryGetValue(ms, out states);
                MoveSet(path, ms, used ? states : allStates, used, issues);
            }
            return issues;
        }

        static void Attack(string path, AttackData a, QaSettings.CombatLimits lim, List<QaIssue> issues)
        {
            if (a.clip == null) issues.Add(QaRules.DataMoveSetClip.Issue(path, 0, "attack has no clip"));
            if (string.IsNullOrEmpty(a.state)) issues.Add(QaRules.DataMoveSetClip.Issue(path, 0, "attack has no Animator state"));
            var n = new AttackNumbers
            {
                Kind = a.kind.ToString(), HitStart = a.hitStart, HitEnd = a.hitEnd, ComboWindow = a.comboWindow, DodgeCancel = a.dodgeCancel,
                End = a.end, SteerUntil = a.steerUntil, Damage = a.damage, PoiseDamage = a.poiseDamage, StaminaCost = a.staminaCost,
                Unblockable = a.unblockable, ClipSeconds = a.clip != null ? a.clip.length / Mathf.Max(0.01f, a.speed) : 0f
            };
            foreach (var f in CombatRules.CheckAttack(n, lim)) issues.Add(f.Rule.Issue(path, 0, a.name, f.Message, f.Severity, null));
        }

        /// <summary>MoveSet → state names of the controllers on prefabs whose Combatant uses it.</summary>
        static Dictionary<MoveSet, HashSet<string>> ControllersByMoveSet(QaContext ctx)
        {
            var map = new Dictionary<MoveSet, HashSet<string>>();
            foreach (var p in ctx.FindAssets("t:Prefab"))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go == null) continue;
                foreach (var c in go.GetComponentsInChildren<Combatant>(true))
                {
                    if (c.moves == null) continue;
                    var anim = c.GetComponentInChildren<Animator>(true);
                    if (anim == null) anim = c.GetComponentInParent<Animator>();
                    if (anim == null || anim.runtimeAnimatorController == null) continue;
                    HashSet<string> set;
                    if (!map.TryGetValue(c.moves, out set)) { set = new HashSet<string>(); map.Add(c.moves, set); }
                    CollectStates(Resolve(anim.runtimeAnimatorController), set);
                }
            }
            return map;
        }

        static AnimatorController Resolve(RuntimeAnimatorController rc)
        {
            var oc = rc as AnimatorOverrideController;
            while (oc != null) { rc = oc.runtimeAnimatorController; oc = rc as AnimatorOverrideController; }
            return rc as AnimatorController;
        }

        static void CollectStates(AnimatorController c, HashSet<string> into)
        {
            if (c == null) return;
            foreach (var layer in c.layers) CollectStates(layer.stateMachine, into);
        }

        static void CollectStates(AnimatorStateMachine sm, HashSet<string> into)
        {
            if (sm == null) return;
            foreach (var s in sm.states) into.Add(s.state.name);
            foreach (var child in sm.stateMachines) CollectStates(child.stateMachine, into);
        }

        static void MoveSet(string path, MoveSet ms, HashSet<string> states, bool used, List<QaIssue> issues)
        {
            var sev = used ? QaSeverity.Critical : QaSeverity.Major;
            Required(path, ms, issues);
            if (states.Count > 0)
                foreach (var a in ReachableAttacks(ms))
                    if (!string.IsNullOrEmpty(a.state) && !states.Contains(a.state))
                        issues.Add(QaRules.DataMoveSetState.Issue(path, 0, a.name, "attack state '" + a.state + "' does not exist in the controller", sev, null));
            foreach (var kv in Actions(ms))
            {
                var clip = kv.Value;
                if (clip == null || !clip.IsValid) continue;
                foreach (var f in CombatRules.CheckAction(kv.Key, clip.end, clip.cancelFrom, clip.iFrameStart, clip.iFrameEnd, kv.Key.StartsWith("dodge.", System.StringComparison.Ordinal)))
                    issues.Add(f.Rule.Issue(path, 0, kv.Key, f.Message, f.Severity, null));
                if (states.Count > 0 && !states.Contains(clip.state))
                    issues.Add(QaRules.DataMoveSetState.Issue(path, 0, kv.Key, "state '" + clip.state + "' does not exist in the controller", sev, null));
            }
        }

        /// <summary>Moves every combatant needs: a light combo, a forward dodge, a light hit reaction and a death.</summary>
        static void Required(string path, MoveSet ms, List<QaIssue> issues)
        {
            if (ms.lightStart == null) issues.Add(QaRules.DataMoveSetClip.Issue(path, 0, "lightStart", "no light attack entry point", QaSeverity.Critical, null));
            Require(path, "dodge.front", ms.dodge != null ? ms.dodge.front : null, issues);
            Require(path, "hitLight.front", ms.hitLight != null ? ms.hitLight.front : null, issues);
            if (ms.deaths == null || ms.deaths.Length == 0) issues.Add(QaRules.DataMoveSetClip.Issue(path, 0, "deaths", "no death animation", QaSeverity.Critical, null));
        }

        /// <summary>Entry points, AI attacks and everything reachable through nextLight/nextHeavy.</summary>
        static List<AttackData> ReachableAttacks(MoveSet ms)
        {
            var attacks = new List<AttackData>();
            var queue = new List<AttackData> { ms.lightStart, ms.heavyStart, ms.sprintAttack, ms.riposte, ms.kick, ms.execution };
            if (ms.aiAttacks != null) queue.AddRange(ms.aiAttacks);
            if (ms.aiUnblockables != null) queue.AddRange(ms.aiUnblockables);
            for (int i = 0; i < queue.Count; i++)
            {
                var a = queue[i];
                if (a == null || attacks.Contains(a)) continue;
                attacks.Add(a);
                queue.Add(a.nextLight);
                queue.Add(a.nextHeavy);
            }
            return attacks;
        }

        static void Require(string path, string label, ActionClip clip, List<QaIssue> issues)
        {
            if (clip == null || !clip.IsValid) issues.Add(QaRules.DataMoveSetClip.Issue(path, 0, label, label + " has no clip/state", QaSeverity.Critical, null));
        }

        static List<KeyValuePair<string, ActionClip>> Actions(MoveSet ms)
        {
            var list = new List<KeyValuePair<string, ActionClip>>();
            Add(list, "dodge", ms.dodge);
            Add(list, "hitLight", ms.hitLight);
            Add(list, "blockHit", ms.blockHit); Add(list, "guardBreak", ms.guardBreak); Add(list, "parried", ms.parried);
            Add(list, "hitHeavy", ms.hitHeavy); Add(list, "stagger", ms.stagger); Add(list, "knockdown", ms.knockdown);
            Add(list, "getUp", ms.getUp); Add(list, "executionVictim", ms.executionVictim); Add(list, "warCry", ms.warCry);
            Add(list, "drawSword", ms.drawSword); Add(list, "sheathSword", ms.sheathSword); Add(list, "victory", ms.victory);
            if (ms.deaths != null) for (int i = 0; i < ms.deaths.Length; i++) Add(list, "deaths[" + i + "]", ms.deaths[i]);
            return list;
        }

        static void Add(List<KeyValuePair<string, ActionClip>> list, string label, ActionClip clip) { list.Add(new KeyValuePair<string, ActionClip>(label, clip)); }

        static void Add(List<KeyValuePair<string, ActionClip>> list, string label, DirectionalClips d)
        {
            if (d == null) return;
            Add(list, label + ".front", d.front); Add(list, label + ".back", d.back); Add(list, label + ".left", d.left); Add(list, label + ".right", d.right);
        }
    }
}
