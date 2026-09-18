using System;
using Microsoft.Xna.Framework;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    /// <summary>[Preview] World-target feedback using the native angular budget.
    /// Target selection is a preview setting; this does not emulate the game's AI.</summary>
    public static class TargetTrackingPreview
    {
        public static bool Enabled(Model model) => Main.Config.RootMotionPreview_Enabled && Main.Config.Event760_AutoTurnToTarget
            && Main.Config.SimEnabled_Event760RootMotionBoost && Main.Config.SimEnabled_Tracking
            && !Main.Config.Event760_UseManualTargetDist
            && Event760State.SupportsGame(model.ActionSimulation?.Document?.GameRoot?.GameType)
            && model.ActionSimulation.Event760TargetPosition.HasValue
            && !(BulletPreview.Enabled(model) && model.ActionSimulation.Bullets.HasEvents && (!Main.Config.BulletPreview_Locked || !Main.Config.BulletPreview_UnitPresent));

        public static float TurnDelta(Model model, float elapsedTime)
        {
            if (!float.IsFinite(elapsedTime) || !float.IsFinite(model.CurrentTrackingSpeed)) return 0;
            float input = float.IsFinite(model.TrackingTestInput) ? Math.Clamp(model.TrackingTestInput, -1, 1) : 0;
            if (!Enabled(model) || Math.Abs(input) > 0.0001f)
                return MathHelper.ToRadians(model.CurrentTrackingSpeed) * elapsedTime * input;
            // Reverse scrubs cannot reconstruct the past lock-on path. Replay instead.
            if (elapsedTime <= 0 || model.CurrentTrackingSpeed <= 0 || model.AnimContainer == null) return 0;
            var world = model.OriginOffsetMatrix * model.AnimContainer.RootMotionTransform.GetXnaMatrixFull();
            var position = Vector3.Transform(Vector3.Zero, world);
            var forward = Vector3.TransformNormal(Vector3.Forward, world); // DSA forward = -Z.
            var target = BulletPreview.Enabled(model) && model.ActionSimulation.Bullets.HasEvents
                ? BulletPreview.Target(model) : model.ActionSimulation.Event760TargetPosition.Value;
            var direction = target - position;
            float dot = forward.X * direction.X + forward.Z * direction.Z;
            float cross = forward.Z * direction.X - forward.X * direction.Z;
            if (!float.IsFinite(dot) || !float.IsFinite(cross)
                || direction.X * direction.X + direction.Z * direction.Z < 0.00000001f) return 0;
            float requested = MathF.Atan2(cross, dot);
            float budget = MathHelper.ToRadians(model.CurrentTrackingSpeed) * elapsedTime;
            return Math.Clamp(requested, -budget, budget);
        }
    }
}
