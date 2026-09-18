using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SoulsAssetPipeline;
using SoulsAssetPipeline.Animation;
using SoulsFormats;
using XVector3 = Microsoft.Xna.Framework.Vector3;

namespace DSAnimStudio
{
    // A stationary target cylinder and a swept actor cylinder. This is a preview
    // constraint, not the game's character controller or dynamic body solver.
    public sealed class CharacterCollisionPreview
    {
        private readonly List<(DSAProj.Action Action, NewHavokAnimation Anim, int Flag)> disableWindows = new();
        private bool DisabledBy(int flag) => disableWindows.Any(x => x.Flag == flag && x.Action.IsActive
            && x.Anim.CurrentTime >= x.Action.StartTime && x.Anim.CurrentTime < x.Action.EndTime);
        public bool DisabledByFlag39 => DisabledBy(39);
        public bool DisabledByFlag50 => DisabledBy(50);
        public bool DisabledByCharacterFlag => DisabledByFlag39 || DisabledByFlag50;
        public string BypassReason => DisabledByFlag39 ? "ChrActionFlag 39" : "ChrActionFlag 50 (capsule)";
        public string Status { get; private set; } = "No world target";
        public float BlockedDistance { get; private set; }
        public float RequestedDistance { get; private set; }
        public float AppliedDistance { get; private set; }

        public void BeginSimulationFrame() => disableWindows.Clear();
        public void Reset()
        {
            BeginSimulationFrame();
            Status = "No world target";
            BlockedDistance = RequestedDistance = AppliedDistance = 0;
        }

        public void RegisterAction(DSAProj.Action action, NewHavokAnimation anim, BinaryReaderEx reader)
        {
            if (action.Type == 0 && action.IsActive && action.ParameterBytes?.Length >= 4
                && reader.GetInt32(0) is 39 or 50)
                disableWindows.Add((action, anim, reader.GetInt32(0)));
        }

        public static float ActorRadius(Model model) => Main.Config.Event760_ActorCollisionRadius > 0
            ? Main.Config.Event760_ActorCollisionRadius : model.ChrHitCapsuleRadius;

        public Vector4 Resolve(Model model, NewBlendableTransform root, Vector4 delta)
        {
            // DSA also calls the root update for zero-time pose/turn refreshes.
            // Keep the most recent movement result readable between these calls.
            if (delta == Vector4.Zero) return delta;
            var cfg = Main.Config;
            var sim = model.ActionSimulation;
            BlockedDistance = 0;
            RequestedDistance = AppliedDistance = new Vector2(delta.X, delta.Z).Length();
            if (!cfg.RootMotionPreview_Enabled || !cfg.Event760_EnableTargetCollision || !cfg.SimEnabled_Event760RootMotionBoost)
            { Status = "Off"; return delta; }
            if (!Event760State.SupportsGame(sim?.Document?.GameRoot?.GameType)
                || cfg.Event760_UseManualTargetDist || sim.Event760TargetPosition is not XVector3 target)
            { Status = "No world target"; return delta; }
            // Evaluate the animation's updated time here: TAE collection happens
            // before the animation scrub. Do not carry flag 39 one frame past its end.
            if (DisabledByCharacterFlag)
            { Status = $"BYPASSED - {BypassReason}"; return delta; }

            float radius = ActorRadius(model), targetRadius = cfg.Event760_TargetCollisionRadius;
            if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(targetRadius) || targetRadius <= 0
                || !float.IsFinite(model.ChrHitCapsuleHeight) || model.ChrHitCapsuleHeight <= 0
                || !float.IsFinite(model.ChrHitCapsuleYOffset)
                || !float.IsFinite(cfg.Event760_TargetCollisionHeight) || cfg.Event760_TargetCollisionHeight <= 0)
            { Status = "Invalid collision dimensions"; return delta; }

            // Use the same origin/root composition as Model.NewScrubSimTime,
            // including the displacement of a nonzero origin when yaw changes.
            var next = root * NewBlendableTransform.FromRootMotionSample(delta);
            var startX = XVector3.Transform(XVector3.Zero, model.OriginOffsetMatrix * root.GetXnaMatrixFull());
            var endX = XVector3.Transform(XVector3.Zero, model.OriginOffsetMatrix * next.GetXnaMatrixFull());
            var start = new Vector3(startX.X, startX.Y, startX.Z);
            var movement = new Vector3(endX.X - startX.X, endX.Y - startX.Y, endX.Z - startX.Z);
            var center = new Vector3(target.X, target.Y, target.Z);
            if (!Finite(start) || !Finite(movement) || !Finite(center))
            { Status = "Invalid world position"; return delta; }
            var resolved = Sweep(start, movement, center, radius + targetRadius,
                model.ChrHitCapsuleHeight, model.ChrHitCapsuleYOffset, cfg.Event760_TargetCollisionHeight);
            var correction = resolved - movement;
            delta.X += correction.X;
            delta.Z += correction.Z;
            BlockedDistance = new Vector2(correction.X, correction.Z).Length();
            RequestedDistance = new Vector2(movement.X, movement.Z).Length();
            AppliedDistance = new Vector2(resolved.X, resolved.Z).Length();
            Status = BlockedDistance > 0.00001f ? "BLOCKED / sliding" : "Enabled - clear";
            return delta;
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        public static Vector3 Sweep(Vector3 start, Vector3 move, Vector3 target, float combinedRadius,
            float actorHeight, float actorYOffset, float targetHeight)
        {
            // Restrict collision to the part of the step where the vertical
            // intervals overlap; jumping above the target does not hit a tall wall.
            float from = 0, to = 1;
            float bottom = start.Y + actorYOffset;
            if (MathF.Abs(move.Y) < 0.000001f)
            {
                if (bottom >= target.Y + targetHeight || bottom + actorHeight <= target.Y) return move;
            }
            else
            {
                float a = (target.Y - actorHeight - bottom) / move.Y;
                float b = (target.Y + targetHeight - bottom) / move.Y;
                from = MathF.Max(0, MathF.Min(a, b));
                to = MathF.Min(1, MathF.Max(a, b));
                if (from >= to) return move;
            }

            var full = new Vector2(move.X, move.Z);
            var offset = new Vector2(start.X - target.X, start.Z - target.Z) + full * from;
            var step = full * (to - from);
            double distanceSquared = (double)offset.X * offset.X + (double)offset.Y * offset.Y;
            double radiusSquared = (double)combinedRadius * combinedRadius;
            Vector2 resolved;
            if (distanceSquared <= radiusSquared + 0.000001)
            {
                // Existing overlap (e.g. flag 39 just ended): permit escape and
                // tangential motion, but never travel deeper through the target.
                if (distanceSquared < 0.00000001) return move;
                var normal = offset / (float)Math.Sqrt(distanceSquared);
                resolved = step - normal * MathF.Min(0, Vector2.Dot(step, normal));
            }
            else
            {
                double a = (double)step.X * step.X + (double)step.Y * step.Y;
                double b = (double)offset.X * step.X + (double)offset.Y * step.Y;
                if (a < 0.000000000001 || b >= 0) return move;
                double discriminant = b * b - a * (distanceSquared - radiusSquared);
                if (discriminant <= 0) return move;
                double hit = (-b - Math.Sqrt(discriminant)) / a;
                if (hit < 0 || hit > 1) return move;
                var contact = step * (float)hit;
                var normal = Vector2.Normalize(offset + contact);
                var remaining = step - contact;
                // A tangent from a convex circle stays outside, even for a very
                // large remainder, so this cannot tunnel on a high-scale frame.
                resolved = contact + remaining - normal * MathF.Min(0, Vector2.Dot(remaining, normal));
            }
            var result = full + resolved - step;
            return new Vector3(result.X, move.Y, result.Y);
        }
    }
}
