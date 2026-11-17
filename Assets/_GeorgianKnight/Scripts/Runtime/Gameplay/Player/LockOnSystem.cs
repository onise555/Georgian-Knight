using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Soft/hard target lock with flick switching, auto re-targeting when the target dies.</summary>
    [RequireComponent(typeof(Combatant))]
    public class LockOnSystem : MonoBehaviour
    {
        public float acquireDistance = 18f;
        public float breakDistance = 26f;
        public float acquireAngle = 70f;
        public bool autoRetarget = true;
        public Transform cameraTransform;

        public Combatant Target { get; private set; }
        public event Action<Combatant> TargetChanged;

        Combatant self;
        GKInput input;
        readonly List<Combatant> candidates = new List<Combatant>();

        void Awake()
        {
            self = GetComponent<Combatant>();
            input = GetComponent<GKInput>();
        }

        void Start()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
        }

        void Update()
        {
            if (self.IsDead) { SetTarget(null); return; }

            if (input != null && input.LockOnPressed)
            {
                if (Target != null) SetTarget(null);
                else SetTarget(FindBest(null, 0));
            }

            if (Target != null)
            {
                bool lost = Target.IsDead || GK.Flat(Target.transform.position - transform.position).magnitude > breakDistance;
                if (lost) SetTarget(autoRetarget ? FindBest(null, 0) : null);
                else if (input != null && input.SwitchTarget != 0)
                {
                    Combatant next = FindBest(Target, input.SwitchTarget);
                    if (next != null) SetTarget(next);
                }
            }
        }

        /// <summary>Locks on to <paramref name="c"/> (null releases): tests and scripted moments.</summary>
        public void LockOn(Combatant c) { SetTarget(c); }

        void SetTarget(Combatant c)
        {
            if (c == Target) return;
            Target = c;
            if (TargetChanged != null) TargetChanged(c);
        }

        /// <summary>
        /// side == 0: best target near the camera centre. side = ±1: nearest target to the left/right
        /// (in screen space) of <paramref name="from"/>.
        /// </summary>
        Combatant FindBest(Combatant from, int side)
        {
            Transform cam = cameraTransform != null ? cameraTransform : transform;
            candidates.Clear();
            List<Combatant> all = CombatDirector.I.all;
            for (int i = 0; i < all.Count; i++)
            {
                Combatant c = all[i];
                if (c == null || c == self || c.team == self.team || c.IsDead) continue;
                float d = Vector3.Distance(c.transform.position, transform.position);
                if (d > acquireDistance) continue;
                candidates.Add(c);
            }
            if (candidates.Count == 0) return null;

            Combatant best = null;
            float bestScore = float.MaxValue;
            Vector3 camFwd = GK.Flat(cam.forward);
            float fromAngle = from != null ? GK.FlatAngle(camFwd, from.transform.position - cam.position) : 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Combatant c = candidates[i];
                if (c == from) continue;
                float ang = GK.FlatAngle(camFwd, c.transform.position - cam.position);
                float dist = Vector3.Distance(c.transform.position, transform.position);
                float score;
                if (side == 0)
                {
                    if (Mathf.Abs(ang) > acquireAngle && dist > 4f) continue;
                    score = Mathf.Abs(ang) * 0.05f + dist * 0.25f;
                }
                else
                {
                    float rel = Mathf.DeltaAngle(fromAngle, ang);
                    if (Mathf.Sign(rel) != side || Mathf.Abs(rel) < 2f) continue;
                    score = Mathf.Abs(rel) + dist * 0.5f;
                }
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }
    }
}
