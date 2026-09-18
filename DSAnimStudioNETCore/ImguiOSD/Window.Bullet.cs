using System;
using ImGuiNET;
using System.Collections.Generic;
using System.Linq;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace DSAnimStudio.ImguiOSD
{
    public abstract partial class Window
    {
        public sealed class BulletTrajectory : Window
        {
            private BulletPreview playingTail;
            private string magicSearch = "", magicSlotKey, magicCandidateStatus = "";
            private int magicScope;
            private bool magicMatchMotion = true;
            private object magicTable;
            private object magicSelection;
            private long nextMagicSlotScan;
            private IReadOnlyList<MagicBulletResolver.Candidate> magicCandidates = Array.Empty<MagicBulletResolver.Candidate>();

            private bool DrawMagicPicker(Model model, ref bool anyFieldFocused)
            {
                var editor = Main.TAE_EDITOR;
                var manager = model?.Document?.ParamManager;
                bool canMapMotion = model?.Document?.GameRoot?.GameType is SoulsAssetPipeline.SoulsGames.ER or SoulsAssetPipeline.SoulsGames.ERNR;
                bool refresh = ImGui.Combo("Magic list scope", ref magicScope, "Selected animation\0Selected TAE category\0");
                if (canMapMotion) refresh |= ImGui.Checkbox("Match TAE: 400 + Magic refType", ref magicMatchMotion);
                refresh |= ImGui.Button("Refresh Magic list");
                object selection = magicScope == 0 ? (object)editor?.SelectedAnim : editor?.SelectedAnimCategory;
                var table = manager?.GetParam("Magic") ?? manager?.GetParam("MagicParam");
                bool sourceChanged = !ReferenceEquals(table, magicTable);
                if (refresh || sourceChanged || !ReferenceEquals(selection, magicSelection) || Environment.TickCount64 >= nextMagicSlotScan)
                {
                    magicSelection = selection;
                    nextMagicSlotScan = Environment.TickCount64 + 500;
                    var slots = new HashSet<int>();
                    void Collect(DSAProj.Animation animation) => animation?.SafeAccessActions(actions =>
                    {
                        if (model?.Document?.GameRoot != null)
                            slots.UnionWith(MagicBulletResolver.GetReferenceSlots(model.Document.GameRoot.GameType, actions));
                    });
                    if (magicScope == 0) Collect(editor?.SelectedAnim);
                    else editor?.SelectedAnimCategory?.SafeAccessAnimations(animations =>
                    {
                        foreach (var animation in animations) Collect(animation);
                    });
                    int? category = magicScope == 0 ? editor?.SelectedAnim?.SplitID.CategoryID : editor?.SelectedAnimCategory?.CategoryID;
                    int? filterCategory = magicMatchMotion && canMapMotion ? category : null;
                    string key = $"{filterCategory}:" + string.Join(",", slots.OrderBy(i => i));
                    if (refresh || sourceChanged || key != magicSlotKey)
                    {
                        magicTable = table; magicSlotKey = key;
                        magicCandidates = MagicBulletResolver.ListCandidates(manager, slots, out magicCandidateStatus, filterCategory);
                    }
                }
                ImGui.TextWrapped(magicCandidateStatus);
                ImGui.InputText("Search Magic / Bullet ID or name", ref magicSearch, 128);
                anyFieldFocused |= ImGui.IsItemActive();
                bool changed = false;
                int selected = Main.Config.BulletPreview_MagicID;
                string label = selected < 0 ? "Choose a spell" : $"Magic {selected}";
                if (ImGui.BeginCombo("Compatible Magic IDs", label, ImGuiComboFlags.HeightLarge))
                {
                    int shown = 0;
                    foreach (var candidate in magicCandidates)
                    {
                        string motion = $"refType {candidate.MotionType}" + (canMapMotion ? $" / TAE {candidate.TaeCategory}" : "");
                        string text = $"{candidate.MagicId} {candidate.Name} | {motion} | {candidate.References}";
                        if (!string.IsNullOrWhiteSpace(magicSearch) && !text.Contains(magicSearch.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        shown++;
                        if (ImGui.Selectable(text + $"##MagicChoice{candidate.MagicId}", candidate.MagicId == selected))
                        { Main.Config.BulletPreview_MagicID = candidate.MagicId; changed = true; }
                    }
                    if (shown == 0) ImGui.TextUnformatted("No matching Bullet spells.");
                    ImGui.EndCombo();
                }
                var chosen = magicCandidates.FirstOrDefault(c => c.MagicId == Main.Config.BulletPreview_MagicID);
                if (chosen != null) ImGui.TextWrapped(chosen.References);
                else if (selected >= 0) ImGui.TextWrapped("Selected ID is outside this candidate list; manual selection is retained.");
                ImGui.TextWrapped(canMapMotion
                    ? "Magic refType (MAGIC_MOTION_TYPE) selects TAE category 400 + refType. Event 64 selects the Bullet reference slot. Multiple spells can share a motion. Select a spell, then Replay from start. Disable the motion filter to inspect other spells."
                    : "This game's motion-to-TAE mapping is not enabled. Showing spells compatible with the selected Event 64 slots. Select a spell, then Replay from start.");
                return changed;
            }
            public override SaveOpenStateTypes GetSaveOpenStateType() => SaveOpenStateTypes.SaveAlways;
            public override string NewImguiWindowTitle => "Bullet Trajectory";
            protected override bool AutoCloseWhenFloatingAndUnfocused => false;
            protected override void Init() { }
            protected override void BuildContents(ref bool anyFieldFocused)
            {
                ImGui.BeginGroup();
                var cfg = Main.Config;
                var model = RootMotionReadout.GetMainModel();
                var preview = model?.ActionSimulation?.Bullets;
                bool changed = ImGui.Checkbox("Preview Bullet trajectories", ref cfg.BulletPreview_Enabled);
                changed |= ImGui.Checkbox("Simulate Bullet spawns", ref cfg.SimEnabled_Bullets);
                if(ImGui.CollapsingHeader("FXR effect preview",ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if(ImGui.Checkbox("Preview FXR effects",ref cfg.BulletPreview_FxrEnabled) && !cfg.BulletPreview_FxrEnabled)
                    {preview?.Effects.Dispose();model?.ActionSimulation?.AnimationEffects.Reset();FxrOpaqueDepth.Dispose();}
                    ImGui.Checkbox("Animation effects (TAE)",ref cfg.FxrPreview_AnimationEvents);
                    if(cfg.FxrPreview_AnimationEvents && ImGui.CollapsingHeader("Floor effects / Event 112"))
                    {
                        var materials=FloorFxrResolver.Materials(model?.Document?.ParamManager);
                        var selected=materials.FirstOrDefault(m=>m.Id==cfg.FxrPreview_FloorMaterial);
                        string Label(FloorFxrResolver.Material m)=>string.IsNullOrWhiteSpace(m.Name)?$"{m.Id}":$"{m.Id}: {m.Name}";
                        if(ImGui.BeginCombo("Floor material (FootSfxParam)",selected==null?$"{cfg.FxrPreview_FloorMaterial} (unavailable)":Label(selected)))
                        {
                            foreach(var material in materials)
                                if(ImGui.Selectable(Label(material),material.Id==cfg.FxrPreview_FloorMaterial))
                                {cfg.FxrPreview_FloorMaterial=material.Id;model?.ActionSimulation?.AnimationEffects.Reset();}
                            ImGui.EndCombo();
                        }
                        ImGui.TextWrapped("Event 112 selects a foot-effect index in the chosen floor material row. Empty entries emit nothing. Uses the dummy spawn pose; game terrain, water height and contact orientation are not sampled.");
                    }
                    ImGui.Checkbox("Bullet flight / hit effects",ref cfg.FxrPreview_BulletEffects);
                    ImGui.Checkbox("FXR particle collisions with preview target / ground",ref cfg.FxrPreview_ParticleCollision);
                    if(cfg.FxrPreview_ParticleCollision)ImGui.TextWrapped("Uses the target and ground collision settings below for effects that request collision. Game terrain and native screen-depth collisions are not loaded.");
                    if(preview!=null)
                    {
                        ImGui.TextWrapped(model.ActionSimulation.AnimationEffects.Status);
                        if(!string.IsNullOrEmpty(model.ActionSimulation.AnimationEffects.Warning))ImGui.TextWrapped(model.ActionSimulation.AnimationEffects.Warning);
                        ImGui.TextWrapped(preview.Effects.Status);
                        if(cfg.BulletPreview_FxrEnabled&&FxrOpaqueDepth.GpuBytes>0)ImGui.Text($"Shared scene depth: {FxrOpaqueDepth.GpuBytes/1048576.0:0.0} MiB GPU");
                        if(ImGui.CollapsingHeader("Referenced FXR chains"))
                            ImGui.TextWrapped(string.IsNullOrEmpty(preview.Effects.ReferenceSummary)?"No referenced FXR loaded yet.":preview.Effects.ReferenceSummary);
                        if(!string.IsNullOrEmpty(preview.Effects.Warning))ImGui.TextWrapped(preview.Effects.Warning);
                        if(ImGui.Button("Reload FXR resources"))preview.Effects.Dispose();
                    }
                    ImGui.TextWrapped("Off by default. Reads real FXR / textures / models for animation notifications and Bullet flight / hits. Animation effects work with Bullet trajectories disabled. Casting notifications use the selected Magic ID below. Formats: ER, NR, DS3, Sekiro, AC6. First load is asynchronous; pause or replay to inspect. Referenced FXR load automatically with separate clocks, prewarm and time / termination states. Atlas frames / interpolation, attachment and lifetime are evaluated from data. Tracers use recorded positions and rotations, authored segment spacing / lifetime / orientation, and partial-follow factors. Native materials, special motion, other external state controls, terrain variants, persistent SpEffect / Goods triggers, distortion, lighting and audio remain incomplete. Replay from start for moving attachment trails.");
                }
                if (preview != null)
                {
                    ImGui.TextWrapped(preview.Diagnostics(model));
                    ImGui.TextWrapped(preview.Status);
                    if (!string.IsNullOrEmpty(preview.Warning))
                    {
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(new NVector4(1, 0.65f, 0.25f, 1), preview.Warning);
                        ImGui.PopTextWrapPos();
                    }
                }
                if (ImGui.CollapsingHeader("Supported games / limitations"))
                    ImGui.TextWrapped("Elden Ring, Nightreign, Sekiro, Dark Souls III, Dark Souls Remastered, Bloodborne, Armored Core VI.");
                if (model?.ActionSimulation?.Document?.GameRoot != null)
                {
                    var game = model.ActionSimulation.Document.GameRoot.GameType;
                    ImGui.TextWrapped($"Current game: {BulletGameSupport.Name(game)} | {(BulletGameSupport.Supports(game) ? "parameter preview available" : "unsupported layout")}");
                    if (BulletGameSupport.Supports(game)) ImGui.TextWrapped(BulletGameSupport.Notes(game));
                }
                if (ImGui.CollapsingHeader("Player magic / Event 64", model?.IS_PLAYER == true ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                {
                    changed |= DrawMagicPicker(model, ref anyFieldFocused);
                    changed |= ImGui.InputInt("Highlighted Magic ID", ref cfg.BulletPreview_MagicID);
                    anyFieldFocused |= ImGui.IsItemActive();
                    changed |= ImGui.Combo("Casting dummy source", ref cfg.BulletPreview_MagicSource, "Player model\0Right weapon\0Left weapon\0");
                    ImGui.TextWrapped("Choose a Magic / MagicParam row ID, then Replay from start. Event 64 selects its refId slot and uses its DummyPolyID. Only Bullet references fire. ER / NR continuous casting uses shootInterval. Costs, charges, equipment overrides and SpEffect casting are not simulated.");
                }
                int mode = cfg.BulletPreview_Locked ? 0 : 1;
                if (ImGui.Combo("Aim mode", ref mode, "Locked world target\0Unlocked\0"))
                { cfg.BulletPreview_Locked = mode == 0; changed = true; }
                if (cfg.BulletPreview_Locked || cfg.BulletPreview_UnitPresent)
                {
                    changed |= ImGui.Checkbox("Use Root Motion world target", ref cfg.BulletPreview_Use760Target);
                    if(cfg.BulletPreview_Use760Target && !cfg.RootMotionPreview_Enabled)
                        ImGui.TextWrapped("Shared target OFF: Root Motion master switch is disabled. No target lock or unit hits.");
                    if (!cfg.BulletPreview_Use760Target)
                    {
                        var target = new NVector3(cfg.BulletPreview_TargetX, cfg.BulletPreview_TargetY, cfg.BulletPreview_TargetZ);
                        if (ImGui.InputFloat3("Target base XYZ (m)", ref target) && float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z))
                        { cfg.BulletPreview_TargetX = target.X; cfg.BulletPreview_TargetY = target.Y; cfg.BulletPreview_TargetZ = target.Z; }
                    }
                    ImGui.InputFloat("Aim height above base (m)", ref cfg.BulletPreview_AimHeight);
                    changed |= ImGui.Checkbox("Apply Bullet lockShootLimitAng", ref cfg.BulletPreview_RespectLockCone);
                    if (model != null) ImGui.TextWrapped($"Aim point: {BulletPreview.Target(model)}");
                    ImGui.TextWrapped("Cyan: target aim at spawn, then parameter-driven homing. Moving the target also affects bullets in flight.");
                }
                if (!cfg.BulletPreview_Locked)
                {
                    changed |= ImGui.Combo("Unlocked direction", ref cfg.BulletPreview_UnlockedSource, "Camera forward\0Character forward\0Animation muzzle\0");
                    ImGui.TextWrapped("Orange: direction captured at spawn; no target acquisition. Camera aim uses parallel rays. Automatic body target tracking is bypassed for this Bullet animation; A/D still works.");
                }
                ImGui.TextWrapped("Attachment 0 uses target / unlocked aim. In all supported games, 1 uses the muzzle and 2 allows target correction within the Bullet lock cone. Native evidence is from Elden Ring; other games reuse this preview policy. Full native transforms and special branches are not reproduced.");
                ImGui.TextWrapped("Elden Ring / Nightreign: FireContinuously repeats numShoot volleys during the notification at shootInterval; zero interval emits once. Preview samples exact cadence; native updates may quantize firing times.");
                if (ImGui.CollapsingHeader("Derivation / hit scene", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    changed |= ImGui.Checkbox("Simulate interval and terminal children", ref cfg.BulletPreview_Derived);
                    changed |= ImGui.Checkbox("World target exists (independent of lock)", ref cfg.BulletPreview_UnitPresent);
                    changed |= ImGui.Checkbox("Hit world target capsule", ref cfg.BulletPreview_UnitCollision);
                    changed |= ImGui.Checkbox("Force stop on unit hit (override penetration)", ref cfg.BulletPreview_ForceStopUnit);
                    changed |= ImGui.InputFloat("Target radius (m)##Bullet", ref cfg.Event760_TargetCollisionRadius);
                    changed |= ImGui.InputFloat("Target height (m)##Bullet", ref cfg.Event760_TargetCollisionHeight);
                    changed |= ImGui.Checkbox("Hit preview ground plane", ref cfg.BulletPreview_GroundCollision);
                    changed |= ImGui.InputFloat("Ground Y (m)", ref cfg.BulletPreview_GroundY);
                    changed |= ImGui.Checkbox("Condition 5 = ground hit (approximation)", ref cfg.BulletPreview_Condition5Ground);
                    ImGui.TextWrapped("Yellow capsule: hit target in both aim modes. Dimensions shared with Root Motion. Character Collision flag 39 does not disable Bullet hit tests. Green: child; magenta: deeper descendants.");
                }
                if (changed) { playingTail = null; preview?.ClearTrails(); }
                if (ImGui.Button("Replay from start##Bullet"))
                {
                    playingTail = null;
                    var graph = Main.TAE_EDITOR?.Graph;
                    if (graph != null && model?.AnimContainer != null)
                    {
                        graph.PlaybackCursor.RestartFromBeginning();
                        graph.ViewportInteractor.NewScrub(absolute: true, time: 0, ignoreRootMotion: true);
                        model.AnimContainer.ResetRootMotion();
                        graph.ViewportInteractor.NewScrub();
                        graph.PlaybackCursor.IsPlaying = true;
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear trails##Bullet")) { playingTail = null; preview?.ClearTrails(); }
                var cursor = Main.TAE_EDITOR?.Graph?.PlaybackCursor;
                if (playingTail != preview || cursor?.IsPlaying == true || preview?.HasUnfinishedBullets != true)
                    playingTail = null;
                ImGui.BeginDisabled(preview?.HasUnfinishedBullets != true || cursor == null);
                if (ImGui.Button(playingTail != null ? "Pause bullets##Tail" : "Continue bullets##Tail"))
                {
                    if (playingTail != null) playingTail = null;
                    else { cursor.IsPlaying = false; playingTail = preview; }
                }
                ImGui.SameLine();
                if (ImGui.Button("Bullets +0.5 s##Tail"))
                {
                    cursor.IsPlaying = false; playingTail = null;
                    preview.AdvanceTail(model, .5f);
                }
                ImGui.EndDisabled();
                if (playingTail != null) preview.AdvanceTail(model, Math.Min(ImGui.GetIO().DeltaTime, .1f));
                ImGui.TextWrapped("Continue bullets freezes the animation and plays existing bullets and their descendants beyond the animation end. Keep this panel visible; Replay from start restores animation timing.");
                ImGui.TextWrapped("Play an animation containing a supported Bullet behavior event. Pause to inspect. Arrow keys step forward / restore recorded Bullet states backward. Mouse seeking clears history; replay from start after changing parameters. Wire spheres follow the current hit radius with no upper size limit (minimum display size 2.5 cm).");
                if (ImGui.CollapsingHeader("Preview limits / random seed"))
                {
                    ImGui.SliderFloat("Maximum lifetime (s)", ref cfg.BulletPreview_MaxLifetime, 0.05f, 30);
                    ImGui.SliderFloat("Retain ended trails (s)", ref cfg.BulletPreview_TrailRetention, 0, 30);
                    ImGui.InputInt("Repeatable spread seed", ref cfg.BulletPreview_Seed);
                    if (ImGui.InputInt("Fallback muzzle dummy (-1 = origin proxy)", ref cfg.BulletPreview_FallbackDummy)) preview?.ClearTrails();
                    if (ImGui.InputFloat("Type 2 preview spawn lift (m)", ref cfg.BulletPreview_Type2SpawnLift)) preview?.ClearTrails();
                    ImGui.TextWrapped("EmittePosType 2 uses the animation dummy plus this Y offset as a preview proxy. Native elevated placement has not been reproduced.");
                    ImGui.TextWrapped("256 visible/pending bullets, 4096 births/replay, depth 8; 120 Hz integration; old trails expire only as simulation time advances. Rewind history: up to 30 s / 1200 updates / about 64 MiB. Negative lifetime is capped by the preview limit.");
                }
                ImGui.Separator();
                if (preview != null)
                {
                    ImGui.TextWrapped(preview.Summary());
                    if (ImGui.CollapsingHeader("Bullet chain / hit history", ImGuiTreeNodeFlags.DefaultOpen))
                        foreach (var entry in preview.ChainHistory()) ImGui.TextWrapped(entry);
                    if (ImGui.CollapsingHeader("Preview approximation notices"))
                        foreach (var entry in preview.ApproximationNotices()) ImGui.TextWrapped(entry);
                    var d = preview.LastDefinition;
                    if (d != null && ImGui.CollapsingHeader("Last emitted Bullet parameters", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.TextWrapped($"{d.ID}: {d.Name}");
                        ImGui.TextWrapped($"Life {d.Life:0.###} s | initial speed {d.Speed:0.###} m/s | min/max {d.MinSpeed:0.###} / {d.MaxSpeed:0.###}");
                        ImGui.TextWrapped($"dist {d.Range:0.###} m (coefficient boundary, not death distance) | accel delay {d.AccelDelay:0.###} s");
                        ImGui.TextWrapped($"Accel in/out {d.AccelIn:0.###} / {d.AccelOut:0.###} m/s2 | gravity in/out {d.GravityIn:0.###} / {d.GravityOut:0.###} m/s2");
                        ImGui.TextWrapped($"Homing yaw/pitch {d.Homing:0.###} / {d.HomingPitch:0.###} deg/s (-1 inherits yaw) | begin after {d.HomingBegin:0.###} m | stop within {d.StopHomingRange:0.###} m | lock cone {d.LockCone} deg");
                        ImGui.TextWrapped($"Interval child {d.ChildID} | wait {d.ChildWait:0.###} s | interval {d.ChildIntervalMin:0.###}..{d.ChildInterval:0.###} s");
                        ImGui.TextWrapped($"Terminal child {d.HitBulletID} | condition {d.LaunchCondition} | penetrate unit/map {d.PenetrateCharacter}/{d.PenetrateMap} | posture {d.Posture}");
                        ImGui.TextWrapped($"Count {d.Count} | bursts {d.ContinueCount} | interval {d.Interval:0.###} s | yaw/pitch {d.Yaw} / {d.Pitch} deg | steps {d.YawInterval} / {d.PitchInterval} deg");
                        ImGui.TextWrapped($"Radius {d.Radius:0.###} -> {d.MaxRadius:0.###} m (-1 keeps initial) over {d.SpreadTime:0.###} s");
                    }
                }
                else ImGui.TextWrapped("Load a supported model and animation to begin.");
                ImGui.Separator();
                ImGui.TextWrapped("Parameter-based preview, not the native controller. Supports interval/terminal chains and swept capsule/flat-ground hits. No damage, real map geometry, water, absorption or FXR rendering. Placement 1-6, follow 1-5, ballistic 1 and derivation timing/orientation use preview approximations. Condition 5 meaning is unverified. TAE 238 and automatic acquisition are not reproduced.");
                ImGui.EndGroup();
                // [Preview] The dock host can own an active ID during a viewport left-drag.
                // Only our own control group may block camera/transport input.
                anyFieldFocused |= ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && ImGui.IsItemActive();
            }
        }
    }
}
