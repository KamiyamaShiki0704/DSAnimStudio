using System;
using System.Linq;
using ImGuiNET;
using DSAnimStudio.TaeEditor;
using NVector3 = System.Numerics.Vector3;
using XVector3 = Microsoft.Xna.Framework.Vector3;

namespace DSAnimStudio.ImguiOSD
{
    public abstract partial class Window
    {
        public partial class RootMotionInfo
        {
            private void DrawEvent760(Model mdl)
            {
                SectionHeader("Event 760 - Root Motion Scale Preview");
                var cfg = Main.Config;
                if (cfg == null) return;
                if (!cfg.RootMotionPreview_Enabled)
                {
                    ImGui.TextWrapped("Root Motion simulation is disabled by the master switch. Individual settings are preserved.");
                    return;
                }
                ImGui.TextWrapped("Compatible Event 760: Dark Souls III, Sekiro, Elden Ring, Nightreign, Armored Core VI. Shared scaling formula.");
                if (!Event760State.SupportsGame(mdl?.ActionSimulation?.Document?.GameRoot?.GameType))
                {
                    ImGui.TextColored(ColWarn, "No compatible Event 760 template for the current game. Raw HKX Root Motion preview remains available.");
                    return;
                }
                ImGui.Checkbox("Simulate 760", ref cfg.SimEnabled_Event760RootMotionBoost);
                ImGui.SameLine();
                ImGui.Checkbox("Enable Root Motion", ref cfg.EnableAnimRootMotion);
                ImGui.Checkbox("Show target", ref cfg.Event760_ShowTarget);
                ImGui.SameLine();
                if (ImGui.Button("Use event formula + auto HKX##760"))
                {
                    cfg.Event760_UseManualMultiplier = false;
                    cfg.Event760_AnimOwnDist = 0;
                }
                ImGui.SameLine();
                if (ImGui.Button("Replay from start##760"))
                {
                    var graph = Main.TAE_EDITOR?.Graph;
                    if (graph != null && mdl?.AnimContainer != null)
                    {
                        graph.PlaybackCursor.RestartFromBeginning();
                        graph.ViewportInteractor.NewScrub(absolute: true, time: 0, ignoreRootMotion: true);
                        mdl.AnimContainer.ResetRootMotion();
                        graph.ViewportInteractor.NewScrub();
                        graph.PlaybackCursor.IsPlaying = true;
                    }
                }
                int mode = cfg.Event760_UseManualTargetDist ? 1 : cfg.Event760_UseFixedTarget ? 0 : 2;
                if (ImGui.Combo("Target source##760", ref mode, "Fixed world target\0Manual distance (constant)\0Live camera position\0"))
                {
                    cfg.Event760_UseManualTargetDist = mode == 1;
                    cfg.Event760_UseFixedTarget = mode == 0;
                }
                if (mode == 0)
                {
                    var target = new NVector3(cfg.Event760_TargetX, cfg.Event760_TargetY, cfg.Event760_TargetZ);
                    if (ImGui.InputFloat3("Target XYZ (m)##760", ref target) && Finite(target))
                        SetTarget(target);
                    if (ImGui.Button("Target at raw endpoint##760") && mdl != null)
                    {
                        var raw = RootMotionReadout.Get(mdl).Total;
                        // Replay resets the root to identity. The debug start marker includes
                        // the previous loop's translation and a 0.5 display scale, so use the
                        // same model-origin/root composition as Model.NewForceSyncUpdate.
                        var pos = XVector3.Transform(XVector3.Zero, mdl.OriginOffsetMatrix
                            * Microsoft.Xna.Framework.Matrix.CreateTranslation(raw.X, 0, raw.Z));
                        SetTarget(new NVector3(pos.X, pos.Y, pos.Z));
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Capture camera##760"))
                    {
                        var view = zzz_DocumentManager.CurrentDocument?.WorldViewManager?.CurrentView;
                        if (view != null)
                        {
                            var pos = XVector3.Transform(XVector3.Zero, view.CameraLocationInWorld.WorldMatrix);
                            SetTarget(new NVector3(pos.X, pos.Y, pos.Z));
                        }
                    }
                }
                else if (mode == 1)
                {
                    ImGui.InputFloat("Target distance (m)##760", ref cfg.Event760_ManualTargetDist);
                    ImGui.TextWrapped("Manual distance stays constant while moving. Use a fixed target to preview changing scale and contact.");
                }
                ImGui.Checkbox("Turn toward world target##760", ref cfg.Event760_AutoTurnToTarget);
                if (mdl != null)
                    Row("Turn speed", $"{mdl.CurrentTrackingSpeed:0.##} deg/s | {(TargetTrackingPreview.Enabled(mdl) ? "target tracking; A/D overrides" : "manual input")}");
                ImGui.Checkbox("Target character collision##760", ref cfg.Event760_EnableTargetCollision);
                var collision = mdl?.ActionSimulation?.CharacterCollision;
                if (collision != null && cfg.Event760_EnableTargetCollision)
                {
                    ImGui.TextColored(collision.DisabledByCharacterFlag ? ColWarn : ColAccent,
                        collision.DisabledByCharacterFlag ? $"Collision: BYPASSED - {collision.BypassReason} (active)" : $"Collision last step: {collision.Status}");
                    Row("Last XZ step", $"{collision.RequestedDistance:0.####} requested -> {collision.AppliedDistance:0.####} applied m");
                    Row("Blocked XZ (last step)", $"{collision.BlockedDistance:0.####} m");
                }
                if (ImGui.CollapsingHeader("Collision dimensions / behavior##760"))
                {
                    ImGui.InputFloat("Target radius (m)##760", ref cfg.Event760_TargetCollisionRadius);
                    ImGui.InputFloat("Target height (m)##760", ref cfg.Event760_TargetCollisionHeight);
                    ImGui.InputFloat("Actor radius (0 = auto)##760", ref cfg.Event760_ActorCollisionRadius);
                    if (mdl != null)
                        Row("Contact distance", $"{CharacterCollisionPreview.ActorRadius(mdl):0.###} + {cfg.Event760_TargetCollisionRadius:0.###} = {CharacterCollisionPreview.ActorRadius(mdl) + cfg.Event760_TargetCollisionRadius:0.###} m");
                    ImGui.TextWrapped("Orange: target body and actor-center contact boundary. Cyan: Event 760 arrival radius. ChrActionFlag 39 or 50 disables this capsule preview collision. Manual distance has no physical target.");
                    ImGui.TextWrapped("Stationary target preview. Turning follows the current TAE turn-speed budget; A/D overrides target tracking. Replay from start after edits.");
                }
                ImGui.Checkbox("Override multiplier##760", ref cfg.Event760_UseManualMultiplier);
                if (cfg.Event760_UseManualMultiplier)
                    ImGui.InputFloat("Manual multiplier##760", ref cfg.Event760_ManualMultiplier);
                if (ImGui.CollapsingHeader("Advanced limits / measurement override##760"))
                {
                    ImGui.InputFloat("Max multiplier (0 = unlimited)##760", ref cfg.Event760_MaxMultiplier);
                    ImGui.InputFloat("Raw window distance (0 = auto HKX)##760", ref cfg.Event760_AnimOwnDist);
                }

                var sim = mdl?.ActionSimulation;
                var states = sim?.Event760States;
                if (states == null || states.Count == 0)
                {
                    ImGui.TextColored(ColDim, "No Event 760 in the current simulated animation.");
                    return;
                }
                var selected = Main.TAE_EDITOR?.InspectorAction;
                var st = states.FirstOrDefault(x => x.Action == selected)
                    ?? states.FirstOrDefault(x => x.IsActive)
                    ?? states.FirstOrDefault(x => x.InTimeWindow) ?? states[0];
                ImGui.TextWrapped($"{st.AnimationName} | Event [{st.StartTime:0.####}, {st.EndTime:0.####}) s | t = {st.Time:0.####} s");
                if (states.Count > 1)
                    ImGui.TextWrapped($"{states.Count} events; select one in the timeline to inspect its formula. Active events multiply together.");
                Row("Scale lower / upper bounds", $"{st.MinScale:0.####}x / {st.MaxNativeScale:0.####}x");
                Row("Target source", st.DistSource);
                Row("Arrival offset angle", $"{st.ArriveAngleFromTarget:0.##} deg");
                ImGui.TextColored(ColAccent, "Live formula");
                ImGui.TextWrapped(st.Formula);
                ImGui.TextColored(st.IsActive ? ColAccent : ColWarn,
                    $"{(st.IsActive ? "ACTIVE" : "BYPASSED")} {st.Note}");
                Row("All TAE scales", $"{sim.TaeRootMotionScaleXZ:0.####}x");
                Row("XZ scale before collision", $"{sim.TaeRootMotionScaleXZ:0.####} x {cfg.RootMotionTranslationMultiplierXZ:0.####} = {sim.TaeRootMotionScaleXZ * cfg.RootMotionTranslationMultiplierXZ:0.####}x");
                var world = mdl.AnimContainer.RootMotionTransform.Translation;
                Row("Scaled world XYZ", $"{world.X:0.####}, {world.Y:0.####}, {world.Z:0.####} m", ColAccent);
                if (st.HasWindowMeasurement)
                {
                    Row("Raw window XZ", $"{st.WindowRawDistance:0.####} m ({st.MeasurementSource})");
                    Row("At current scale", $"{st.WindowRawDistance:0.####} x {st.AppliedScale:0.####} = {st.ConstantScaleTravel:0.####} m");
                }
                else ImGui.TextColored(ColDim, "Raw window displacement unavailable (no HKX root motion).");
                ImGui.TextWrapped("At current scale is a constant-scale estimate before collision. Replay from start after edits or scrubbing to measure full travel with changing scales and collision.");
                if (ImGui.Button("Copy formula##760")) ImGui.SetClipboardText(st.Summary);
                if (ImGui.CollapsingHeader("Formula details / assumptions##760"))
                {
                    ImGui.TextWrapped($"For each X/Z component: delta' = sign(delta) * abs(delta)^{cfg.RootMotionTranslationPowerXZ:0.####} * {sim.TaeRootMotionScaleXZ:0.####} * {cfg.RootMotionTranslationMultiplierXZ:0.####}. Event 760 does not scale Y or yaw.");
                    ImGui.TextWrapped("Horizontal scaling formula: when arrival angle is zero, scale = clamp(D - R, Min, Max) / ReferenceDist. Nonzero angles change the arrival point, not a facing limit. Window distance x current scale is not a predicted endpoint.");
                }
            }

            private static bool Finite(NVector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
            private static void SetTarget(NVector3 v)
            {
                if (!Finite(v)) return;
                Main.Config.Event760_TargetX = v.X;
                Main.Config.Event760_TargetY = v.Y;
                Main.Config.Event760_TargetZ = v.Z;
            }
        }
    }
}
