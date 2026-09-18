using System;
using System.Linq;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    internal static class Event760TargetDraw
    {
        public static void Draw(Model model)
        {
            var sim = model.ActionSimulation;
            if (!Main.Config.RootMotionPreview_Enabled || !Main.Config.Event760_ShowTarget || !Main.Config.SimEnabled_Event760RootMotionBoost
                || !Event760State.SupportsGame(sim?.Document?.GameRoot?.GameType)
                || sim?.Event760TargetPosition is not Vector3 target)
                return;
            if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z)) return;
            var color = Color.Cyan;
            var p = target + new Vector3(0, 0.025f, 0);
            ImGuiDebugDrawer.DrawLine3D(p - Vector3.UnitX * 0.2f, p + Vector3.UnitX * 0.2f, color, thickness: 2);
            ImGuiDebugDrawer.DrawLine3D(p - Vector3.UnitZ * 0.2f, p + Vector3.UnitZ * 0.2f, color, thickness: 2);
            ImGuiDebugDrawer.DrawLine3D(p, p + Vector3.UnitY, color, thickness: 2);
            ImGuiDebugDrawer.DrawLine3D(model.CurrentTransformPosition, p, color * 0.6f, thickness: 1);
            var st = sim.Event760States.FirstOrDefault(x => x.Action == Main.TAE_EDITOR?.InspectorAction)
                ?? sim.Event760States.FirstOrDefault(x => x.IsActive) ?? sim.Event760States.FirstOrDefault();
            float radius = st?.ArriveDistFromTarget ?? 0;
            if (float.IsFinite(radius) && radius > 0 && radius < 10000)
            {
                for (int i = 0; i < 64; i++)
                {
                    float a = MathHelper.TwoPi * i / 64, b = MathHelper.TwoPi * (i + 1) / 64;
                    ImGuiDebugDrawer.DrawLine3D(p + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * radius,
                        p + new Vector3(MathF.Cos(b), 0, MathF.Sin(b)) * radius, color * 0.65f, thickness: 1);
                }
            }
            if (Main.Config.Event760_EnableTargetCollision)
            {
                var bodyColor = sim.CharacterCollision.DisabledByCharacterFlag ? Color.Gray : Color.Orange;
                float bodyRadius = Main.Config.Event760_TargetCollisionRadius;
                float height = Main.Config.Event760_TargetCollisionHeight;
                DrawCircle(p, bodyRadius, bodyColor, 2);
                DrawCircle(p, bodyRadius + CharacterCollisionPreview.ActorRadius(model), bodyColor * 0.4f, 1);
                DrawCircle(model.CurrentTransformPosition + Vector3.UnitY * 0.025f,
                    CharacterCollisionPreview.ActorRadius(model), bodyColor, 1);
                if (float.IsFinite(height) && height > 0 && height < 10000
                    && float.IsFinite(bodyRadius) && bodyRadius > 0 && bodyRadius < 10000)
                {
                    DrawCircle(p + Vector3.UnitY * height, bodyRadius, bodyColor, 1);
                    for (int i = 0; i < 4; i++)
                    {
                        float a = MathHelper.TwoPi * i / 4;
                        var edge = p + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * bodyRadius;
                        ImGuiDebugDrawer.DrawLine3D(edge, edge + Vector3.UnitY * height, bodyColor, thickness: 1);
                    }
                }
            }
            var difference = target - model.CurrentTransformPosition;
            float distance = new Vector2(difference.X, difference.Z).Length();
            ImGuiDebugDrawer.DrawText3D($"760 target | D {distance:0.###} m | {sim.TaeRootMotionScaleXZ:0.###}x", p + Vector3.UnitY,
                color, Color.Black, includeCardinalShadows: true);
        }

        private static void DrawCircle(Vector3 center, float radius, Color color, float thickness)
        {
            if (!float.IsFinite(radius) || radius <= 0 || radius >= 10000) return;
            for (int i = 0; i < 64; i++)
            {
                float a = MathHelper.TwoPi * i / 64, b = MathHelper.TwoPi * (i + 1) / 64;
                ImGuiDebugDrawer.DrawLine3D(center + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * radius,
                    center + new Vector3(MathF.Cos(b), 0, MathF.Sin(b)) * radius, color, thickness: thickness);
            }
        }
    }
}
