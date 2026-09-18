using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SoulsAssetPipeline;
using SoulsFormats;
using DSAnimStudio.TaeEditor;

namespace DSAnimStudio
{
    // [Preview] Per-model TAE emission scheduler. Only relative playback emits;
    // absolute scrubs clear history rather than inventing a past emitter pose.
    public sealed class BulletPreview
    {
        private sealed record Emission(DSAProj.Action Action, NewHavokAnimation Animation, int Bullet,
            Func<List<Vector3>> Positions, List<Vector3> BeforePositions, float BeforeTime, int BeforeLoop, string Resolution,
            Func<List<Vector3>> Directions, List<Vector3> BeforeDirections, int AttachmentType, bool Continuous);
        private readonly List<Emission> events = new();
        private readonly HashSet<(object Action, object Animation, int Loop, long Shot)> fired = new();
        private readonly object sync = new();
        private sealed record HistoryFrame(BulletWorld Before, double End, Vector3 Target, float Lifetime, float Retention,
            HashSet<(object Action, object Animation, int Loop, long Shot)> Fired, Dictionary<Func<Vector3>, Vector3> Anchors,
            string Status, string Warning, BulletDefinition Definition)
        {
            public long Size => Before.HistorySize + Fired.Count * 48L + Anchors.Count * 64L + 512;
        }
        private readonly List<HistoryFrame> history = new();
        private long historyBytes;
        private void ClearHistory() { history.Clear(); historyBytes = 0; }
        private void RecordFrame(float delta, Vector3 target, float lifetime, float retention)
        {
            var frame = new HistoryFrame(World.CopyForHistory(), World.Time + delta, target, lifetime, retention,
                new(fired), World.SampleFollowAnchors(), Status, Warning, LastDefinition);
            history.Add(frame); historyBytes += frame.Size;
            // Bound both retained state and the count of tiny/empty frames.
            while (history.Count > 1 && (historyBytes > 64L * 1024 * 1024 || history.Count > 1200 || World.Time - history[0].Before.Time > 30))
            { historyBytes -= history[0].Size; history.RemoveAt(0); }
        }
        private void Rewind(float delta)
        {
            double target = Math.Max(0, World.Time + delta);
            int index = history.FindLastIndex(f => f.Before.Time <= target + 1e-6 && target <= f.End + 1e-6);
            if (index < 0)
            { ClearTrails(); Status = "No recorded Bullet state at this time. Replay from start to rebuild history."; return; }
            var frame = history[index];
            World.RestoreFrom(frame.Before);
            World.AdvanceRecorded((float)Math.Max(0, target - frame.Before.Time), frame.Target, frame.Lifetime, frame.Retention, frame.Anchors);
            fired.Clear(); fired.UnionWith(frame.Fired); LastDefinition = frame.Definition; Warning = frame.Warning;
            Status = $"Frame back: restored Bullet state at {target:0.###} s of this replay.";
            // Continue from the restored state, never keep particles/children from its discarded future.
            for (int i = history.Count - 1; i > index; i--) { historyBytes -= history[i].Size; history.RemoveAt(i); }
            history[index] = frame with { End = target };
        }
        public readonly BulletWorld World = new();
        public readonly FxrPreviewRenderer Effects = new();
        public string Status { get; private set; } = "Replay an animation with Event 2 to emit bullets.";
        public string Warning { get; private set; } = "";
        public BulletDefinition LastDefinition { get; private set; }
        public bool HasEvents { get { lock (sync) return events.Count > 0; } }
        public string Diagnostics(Model model)
        {
            lock (sync) return $"{model?.Name} | NpcParam {model?.NpcParam?.ID.ToString() ?? "missing"} | variation {model?.NpcParam?.BehaviorVariationID.ToString() ?? "missing"}\n"
                + $"Collected events {events.Count} | emitted {World.TotalBorn} | visible {World.Particles.Count}";
        }
        public static bool Enabled(Model m) => Main.Config.BulletPreview_Enabled && Main.Config.SimEnabled_Bullets
            && m?.ActionSimulation?.Document?.GameRoot != null
            && BulletGameSupport.Supports(m.ActionSimulation.Document.GameRoot.GameType);

        public static float Type2SpawnLift => float.IsFinite(Main.Config.BulletPreview_Type2SpawnLift)
            ? Math.Clamp(Main.Config.BulletPreview_Type2SpawnLift, -100, 100) : 0;

        public static Vector3 Target(Model m)
        {
            var c = Main.Config;
            var basePos = c.BulletPreview_Use760Target
                ? m?.ActionSimulation?.Event760TargetPosition ?? new Vector3(c.Event760_TargetX, c.Event760_TargetY, c.Event760_TargetZ)
                : new Vector3(c.BulletPreview_TargetX, c.BulletPreview_TargetY, c.BulletPreview_TargetZ);
            return basePos + Vector3.Up * c.BulletPreview_AimHeight;
        }
        public static bool TargetPresent => Main.Config.BulletPreview_UnitPresent
            && (!Main.Config.BulletPreview_Use760Target || Main.Config.RootMotionPreview_Enabled);

        public void BeginFrame() { lock (sync) events.Clear(); }
        public void Reset()
        {
            lock (sync) { events.Clear(); ClearTrails(); LastDefinition = null; Warning = ""; }
        }
        public void ClearTrails()
        {
            lock (sync) { World.Reset(); fired.Clear(); ClearHistory(); Status = "Cleared; replay from start for a complete path."; }
        }
        public void Report(string message) { lock (sync) Warning = message; }
        public bool HasUnfinishedBullets { get { lock (sync) return World.HasUnfinishedBullets
            || Main.Config.BulletPreview_FxrEnabled && Effects.HasTail(World.Particles,World.Time); } }
        public void AdvanceTail(Model model, float seconds)
        {
            if (!float.IsFinite(seconds) || seconds <= 0) return;
            lock (sync)
            {
                // Freeze animation events while allowing already emitted bullets,
                // interval children and terminal children to finish normally.
                events.Clear();
                Advance(model, Math.Min(seconds, 1), false);
            }
        }
        public void Register(DSAProj.Action action, NewHavokAnimation animation, int bullet, Func<List<Vector3>> positions,
            string resolution = null, Func<List<Vector3>> directions = null, int attachmentType = 0, bool continuous = false)
        {
            if (!action.IsActive || !float.IsFinite(action.StartTime) || !float.IsFinite(action.EndTime) || action.EndTime < action.StartTime) return;
            lock (sync) events.Add(new Emission(action, animation, bullet, positions, positions(), animation.CurrentTime, animation.LoopCount,
                resolution, directions, directions?.Invoke(), attachmentType, continuous));
        }

        public static BulletDefinition LoadDefinition(zzz_ParamManagerIns manager, int id)
        {
            var param = manager?.GetParam("Bullet") ?? manager?.GetParam("BulletParam");
            if (param == null) return null;
            lock (param)
            {
                var row = param.Rows.FirstOrDefault(r => r.ID == id);
                return row == null ? null : BulletDefinition.Read(param.GetRowReader(row), row.ID, row.Name, manager.ParentDocument.GameRoot.GameType, param.DetectedSize);
            }
        }

        public static bool Crossed(float before, float after, float start, bool looped)
            => looped ? start >= before || start < after : start >= before && start < after;

        // Notification-local cadence, independent of the size of playback updates.
        // Keep the occurrence index in rewind history along with pending particles.
        private IEnumerable<(float Offset, int Loop, long Shot)> Shots(Emission e, BulletDefinition definition, bool looped)
        {
            double interval = definition.Interval;
            if (e.Continuous && interval <= 0)
            {
                World.Notice("Continuous shootInterval=0: initial volley only; native emitter disables further repetition.");
            }
            var windows = looped
                ? new[] { (e.BeforeTime, e.Animation.Duration, e.BeforeLoop, 0f),
                    (0f, e.Animation.CurrentTime, e.Animation.LoopCount, e.Animation.Duration - e.BeforeTime) }
                : new[] { (e.BeforeTime, e.Animation.CurrentTime, e.Animation.LoopCount, 0f) };
            int count = 0;
            foreach (var (before, after, loop, offset) in windows)
            {
                double start = e.Action.StartTime;
                if (!e.Continuous || interval <= 0)
                {
                    if (Crossed(before, after, e.Action.StartTime, false)) yield return ((float)(offset + start - before), loop, 0);
                    continue;
                }
                if (after <= start || before >= e.Action.EndTime || e.Action.EndTime <= start) continue;
                // Snap float animation boundaries to the mathematical cadence (sub-microsecond tolerance).
                double first = Math.Max(0, Math.Ceiling((before - start - 1e-7) / interval));
                if (first > long.MaxValue) { World.Notice("Continuous cadence exceeds preview precision; omitted."); continue; }
                for (long shot = (long)first; ; shot++)
                {
                    double at = start + shot * interval;
                    if (at >= after - 1e-7 || at >= e.Action.EndTime - 1e-7) break;
                    if (++count > 256) { World.Notice("Continuous emission limited to 256 volleys per update."); yield break; }
                    yield return ((float)(offset + at - before), loop, shot);
                    if (shot == long.MaxValue) yield break;
                }
            }
        }

        // Shared viewport/cursor entry point, also exercised without a graphics device.
        public void AdvanceFromCursor(Model model, float delta, bool absolute, TaeEditor.TaePlaybackCursor cursor)
            => Advance(model, delta, absolute || (cursor.Scrubbing && !cursor.IsFrameStepScrub), cursor.IsFrameStepScrub);

        public void Advance(Model model, float delta, bool absolute, bool frameStep = false)
        {
            lock (sync)
            {
                if (!Enabled(model)) { World.Reset(); fired.Clear(); ClearHistory(); Status = "Bullet trajectory simulation off / unsupported game."; return; }
                if (!absolute && frameStep && float.IsFinite(delta) && delta < 0 && delta >= -1)
                { Rewind(delta); return; }
                if (absolute || !float.IsFinite(delta) || delta < 0 || delta > 1)
                { ClearTrails(); Status = "Scrub / large time jump: cleared. Replay from start."; return; }
                if (delta == 0) return;
                var cfg = Main.Config;
                Vector3 target = Target(model);
                World.EnableDerivation = cfg.BulletPreview_Derived;
                World.ResolveDefinition = id => LoadDefinition(model.ActionSimulation.Document.ParamManager, id);
                World.Scene = new BulletScene
                {
                    HasTarget = TargetPresent && BulletMath.Finite(target),
                    TargetAim = target, RespectLockCone = cfg.BulletPreview_RespectLockCone,
                    TargetBase = target - Vector3.Up * cfg.BulletPreview_AimHeight,
                    TargetRadius = float.IsFinite(cfg.Event760_TargetCollisionRadius) ? Math.Clamp(cfg.Event760_TargetCollisionRadius, .01f, 100) : .4f,
                    TargetHeight = float.IsFinite(cfg.Event760_TargetCollisionHeight) ? Math.Clamp(cfg.Event760_TargetCollisionHeight, .01f, 100) : 1.5f,
                    OwnerPosition = model.CurrentTransform.WorldMatrix.Translation,
                    OwnerForward = BulletMath.Unit(Vector3.TransformNormal(Vector3.Forward, model.CurrentTransform.WorldMatrix), Vector3.Forward),
                    UnitCollision = cfg.BulletPreview_UnitCollision, GroundCollision = cfg.BulletPreview_GroundCollision,
                    ForceStopUnit = cfg.BulletPreview_ForceStopUnit, Condition5Ground = cfg.BulletPreview_Condition5Ground,
                    GroundY = float.IsFinite(cfg.BulletPreview_GroundY) ? cfg.BulletPreview_GroundY : 0, Type2Lift = Type2SpawnLift,
                };
                bool looped = events.Any(e => e.Animation.LoopCount != e.BeforeLoop || e.Animation.CurrentTime < e.BeforeTime);
                if (looped) { World.Reset(); fired.Clear(); ClearHistory(); }
                foreach (var e in events)
                {
                    if (!e.Action.IsActive || e.Bullet < 0) continue;
                    bool eventLooped = e.Animation.LoopCount != e.BeforeLoop || e.Animation.CurrentTime < e.BeforeTime;
                    if (!e.Continuous && !Crossed(e.BeforeTime, e.Animation.CurrentTime, e.Action.StartTime, eventLooped)) continue;
                    if (e.Continuous && !eventLooped && (e.Animation.CurrentTime <= e.Action.StartTime || e.BeforeTime >= e.Action.EndTime)) continue;
                    try
                    {
                        var definition = LoadDefinition(model.ActionSimulation.Document.ParamManager, e.Bullet);
                        LastDefinition = definition;
                        if (definition == null) { Warning = $"Bullet {e.Bullet} is missing from the loaded Bullet table."; continue; }
                        if (!definition.Valid) { Warning = $"Bullet {e.Bullet}: invalid parameters; omitted."; continue; }
                        if (!definition.Supported)
                        { Warning = $"Bullet {e.Bullet}: FollowType={definition.Follow}, EmittePosType={definition.Emit}, ballisticCalcType={definition.BallisticType} needs a separate spawn/follow model; omitted."; continue; }
                        var positions = e.Positions();
                        var directions = e.Directions?.Invoke();
                        if (positions.Count == 0) { Warning = $"Bullet {e.Bullet}: emission dummy not found; omitted."; continue; }
                        var forward = BulletMath.Unit(Vector3.TransformNormal(Vector3.Forward, model.CurrentTransform.WorldMatrix), Vector3.Forward);
                        var camera = model.ActionSimulation.Document.WorldViewManager?.CurrentView?.GetCameraForward();
                        var unlocked = cfg.BulletPreview_UnlockedSource == 0 ? camera ?? forward : forward;
                        if (cfg.BulletPreview_Locked && !BulletMath.Finite(target))
                        { Warning = "Invalid lock target coordinates."; continue; }
                        bool emitted = false;
                        foreach (var shot in Shots(e, definition, eventLooped))
                        {
                            if (World.TotalBorn >= 4096) { World.Notice("Replay emission limit reached (4096 particles)."); break; }
                            if (!fired.Add((e.Action, e.Animation, shot.Loop, shot.Shot))) continue;
                            emitted = true;
                            float delay = Math.Clamp(shot.Offset, 0, delta), fraction = delay / delta;
                            for (int i = 0; i < positions.Count; i++)
                            {
                                var origin = i < e.BeforePositions.Count ? Vector3.Lerp(e.BeforePositions[i], positions[i], fraction) : positions[i];
                                if (!BulletMath.Finite(origin)) continue;
                                var muzzleForward = directions != null && i < directions.Count ? directions[i] : forward;
                                if (e.BeforeDirections != null && i < e.BeforeDirections.Count)
                                    muzzleForward = Vector3.Lerp(e.BeforeDirections[i], muzzleForward, fraction);
                                muzzleForward = BulletMath.Unit(muzzleForward, forward);
                                bool muzzleDirected = e.AttachmentType is 1 or 2;
                                var aim = muzzleDirected ? muzzleForward : BulletMath.Aim(origin, forward, cfg.BulletPreview_UnlockedSource == 2 ? muzzleForward : unlocked,
                                    target, cfg.BulletPreview_Locked && World.Scene.HasTarget,
                                    cfg.BulletPreview_RespectLockCone ? definition.LockCone : 180);
                                // ER's ordinary emitter skips target-angle correction only for Attachment 1.
                                // Attachment 2 starts from the dummy frame, then allows the Bullet lock cone.
                                // Other supported games reuse this policy at the user's request;
                                // only ER's native implementation has been checked.
                                if (e.AttachmentType == 2)
                                    aim = BulletMath.Aim(origin, muzzleForward, muzzleForward, target,
                                        cfg.BulletPreview_Locked && World.Scene.HasTarget,
                                        cfg.BulletPreview_RespectLockCone ? definition.LockCone : 180);
                                int emitterIndex = i;
                                World.Emit(definition, origin, aim, cfg.BulletPreview_Locked && World.Scene.HasTarget, delay, cfg.BulletPreview_Seed ^ definition.ID ^ i ^ unchecked((int)shot.Shot * 397),
                                    () => { var current = e.Positions(); return emitterIndex < current.Count ? current[emitterIndex] : origin; }, e.Action.Type,
                                    muzzleDirected);
                            }
                        }
                        if (!emitted) continue;
                        Status = $"Bullet {definition.ID} | {(cfg.BulletPreview_Locked && World.Scene.HasTarget ? "LOCKED" : "UNLOCKED")} | {positions.Count} emitter(s) | Attachment {e.AttachmentType}: {(e.AttachmentType is 1 or 2 ? "animation muzzle" : "target / unlocked aim")}";
                        if (e.Continuous) Status += definition.Interval > 0
                            ? $" | continuous every {definition.Interval:0.###} s until {e.Action.EndTime:0.###} s"
                            : " | continuous enabled, zero interval: one volley";
                        var warnings = new List<string>();
                        if (!string.IsNullOrEmpty(e.Resolution))
                        {
                            if (e.Action.Type == 64) Status += " | " + e.Resolution;
                            else warnings.Add(e.Resolution);
                        }
                        if (e.AttachmentType is 1 or 2) warnings.Add("Attachment 1: muzzle direction; 2: muzzle + target correction within lock cone. Shared preview policy; native evidence from Elden Ring only. Full transforms / special branches are approximated.");
                        else if (e.AttachmentType != 0) warnings.Add($"Attachment {e.AttachmentType}: unknown mode; using target / unlocked aim fallback.");
                        if (definition.Emit == 2) warnings.Add($"Type 2 preview position: animation dummy + {Type2SpawnLift:0.###} m Y; native elevated placement not reproduced.");
                        if (definition.AutoHoming && !cfg.BulletPreview_Locked) warnings.Add("Automatic target acquisition omitted.");
                        if (!cfg.BulletPreview_Derived && definition.ChildID >= 0) warnings.Add($"Derived Bullet {definition.ChildID} disabled.");
                        if (definition.AimOffset != 0 || definition.TargetYOffset != 0) warnings.Add("Target random offsets omitted.");
                        if (!e.Action.HasInternalSimField("DummyPolyID") && e.Action.Type is 8 or 307 or 318)
                            warnings.Add("Event has no direct muzzle field; Behavior/Bullet muzzle or configured fallback is used.");
                        Warning = string.Join(" ", warnings);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
                    { Warning = $"Bullet {e.Bullet}: {ex.Message}"; }
                }
                float retention=cfg.BulletPreview_TrailRetention;
                if(cfg.BulletPreview_FxrEnabled)
                    retention=Math.Max(retention,3);
                RecordFrame(delta, target, cfg.BulletPreview_MaxLifetime, retention);
                World.Advance(delta, target, cfg.BulletPreview_MaxLifetime, retention);
            }
        }

        public string[] ChainHistory() { lock (sync) return World.History.TakeLast(24).ToArray(); }
        public string[] ApproximationNotices() { lock (sync) return World.Notices.ToArray(); }

        public string Summary()
        {
            lock (sync)
            {
                var p = World.Particles.LastOrDefault();
                return p == null ? "No emitted bullets. Replay from start; check Event 2, Behavior and dummy IDs."
                    : $"Bullet {p.Definition.ID} | {(p.Locked ? "Locked" : "Unlocked")} | age {p.Age:0.###} s | speed {p.Speed:0.###} m/s\n"
                    + $"Position {p.Position.X:0.###}, {p.Position.Y:0.###}, {p.Position.Z:0.###} m | path {p.Travel:0.###} m\n"
                    + $"Radius {p.Radius:0.###} m | {(p.Alive ? "Flying" : p.EndReason)} | {World.Particles.Count} visible, {World.Dropped} limited";
            }
        }

        public void Draw(Model model)
        {
            lock (sync)
            {
                if(Main.Config.BulletPreview_FxrEnabled)
                {
                    Effects.SetRoots(model.Document.GameRoot.InterrootPath,model.Document.GameRoot.InterrootModenginePath,model.Document.GameRoot.GameType,model.Name);
                    var instances=Main.Config.FxrPreview_BulletEffects && Enabled(model)
                        ? FxrPreviewRenderer.BulletInstances(World.Particles,World.Time):Enumerable.Empty<FxrPlaybackInstance>();
                    if(Main.Config.FxrPreview_AnimationEvents)
                        instances=instances.Concat(model.ActionSimulation.AnimationEffects.Instances());
                    FxrCollisionWorld collisions=null;
                    if(Main.Config.FxrPreview_ParticleCollision)
                    {
                        var cfg=Main.Config;collisions=new();
                        if(cfg.BulletPreview_GroundCollision&&float.IsFinite(cfg.BulletPreview_GroundY))collisions.AddPlane(Vector3.Up,-cfg.BulletPreview_GroundY);
                        var collisionTarget=Target(model);
                        if(cfg.BulletPreview_UnitCollision&&TargetPresent&&BulletMath.Finite(collisionTarget))
                        {
                            float radius=float.IsFinite(cfg.Event760_TargetCollisionRadius)?Math.Clamp(cfg.Event760_TargetCollisionRadius,.01f,100):.4f;
                            float height=float.IsFinite(cfg.Event760_TargetCollisionHeight)?Math.Max(radius*2,cfg.Event760_TargetCollisionHeight):Math.Max(radius*2,1.5f);
                            var basePos=collisionTarget-Vector3.Up*cfg.BulletPreview_AimHeight;
                            collisions.AddCapsule(basePos+Vector3.Up*radius,basePos+Vector3.Up*(height-radius),radius);
                        }
                    }
                    Effects.DrawInstances(instances,GFX.CurrentWorldView,collisions);
                }
                if(!Enabled(model))return;
                foreach (var p in World.Particles)
                {
                    var color = p.Depth > 1 ? Color.Magenta : p.Depth == 1 ? Color.Lime : p.Locked ? Color.Cyan : Color.Orange;
                    if (!p.Alive) color *= 0.55f;
                    for (int i = 1; i < p.Trail.Count; i++)
                        ImGuiDebugDrawer.DrawLine3D(p.Trail[i - 1], p.Trail[i], color, thickness: 2);
                    if (p.Trail.Count > 0) ImGuiDebugDrawer.DrawLine3D(p.Trail[^1], p.Position, color, thickness: 2);
                    float radius = p.DisplayRadius;
                    for (int plane = 0; plane < 3; plane++)
                    for (int i = 0; i < 16; i++)
                    {
                        Vector3 Point(int j)
                        {
                            float a = MathHelper.TwoPi * j / 16;
                            float x = MathF.Cos(a) * radius, y = MathF.Sin(a) * radius;
                            return p.Position + (plane == 0 ? new Vector3(x, 0, y) : plane == 1 ? new Vector3(x, y, 0) : new Vector3(0, x, y));
                        }
                        ImGuiDebugDrawer.DrawLine3D(Point(i), Point(i + 1), color, thickness: 1);
                    }
                    if (World.Particles.Count <= 64)
                        ImGuiDebugDrawer.DrawText3D($"{p.Definition.ID} #{p.Serial} {(p.Alive ? "" : p.EndReason)}", p.Position + Vector3.Up * .1f, color, Color.Black);
                    if (p.Alive) ImGuiDebugDrawer.DrawLine3D(p.Position, p.Position + p.Direction * Math.Max(radius, 0.25f), Color.White, thickness: 2);
                }
                if ((events.Count > 0 || World.Particles.Count > 0) && Main.Config.BulletPreview_GroundCollision)
                {
                    var center = model.CurrentTransform.WorldMatrix.Translation;
                    center.Y = float.IsFinite(Main.Config.BulletPreview_GroundY) ? Main.Config.BulletPreview_GroundY : 0;
                    for (int i = -10; i <= 10; i += 2)
                    {
                        ImGuiDebugDrawer.DrawLine3D(center + new Vector3(i, 0, -10), center + new Vector3(i, 0, 10), Color.SlateGray * .7f);
                        ImGuiDebugDrawer.DrawLine3D(center + new Vector3(-10, 0, i), center + new Vector3(10, 0, i), Color.SlateGray * .7f);
                    }
                    ImGuiDebugDrawer.DrawText3D($"Bullet ground Y={center.Y:0.##} (infinite plane)", center + new Vector3(2, .05f, 2), Color.LightGray, Color.Black);
                }
                var target = Target(model);
                if ((events.Count > 0 || World.Particles.Count > 0) && TargetPresent && BulletMath.Finite(target))
                {
                    var basePos = target - Vector3.Up * Main.Config.BulletPreview_AimHeight;
                    float r = float.IsFinite(Main.Config.Event760_TargetCollisionRadius) ? Math.Clamp(Main.Config.Event760_TargetCollisionRadius, .01f, 100) : .4f;
                    float height = float.IsFinite(Main.Config.Event760_TargetCollisionHeight) ? Math.Clamp(Main.Config.Event760_TargetCollisionHeight, .01f, 100) : 1.5f;
                    float h = Math.Max(2 * r, height);
                    for (int i = 0; i < 24; i++)
                    {
                        float a = MathHelper.TwoPi * i / 24, b = MathHelper.TwoPi * (i + 1) / 24;
                        var radial = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                        var next = new Vector3(MathF.Cos(b) * r, 0, MathF.Sin(b) * r);
                        foreach (float y in new[] { r, h - r })
                            ImGuiDebugDrawer.DrawLine3D(basePos + radial + Vector3.Up * y, basePos + next + Vector3.Up * y, Color.Yellow);
                        if (i % 6 == 0) ImGuiDebugDrawer.DrawLine3D(basePos + radial + Vector3.Up * r, basePos + radial + Vector3.Up * (h - r), Color.Yellow);
                        // Hemisphere meridians match the capsule used by the swept hit test.
                        for (int plane = 0; plane < 2; plane++)
                        {
                            Vector3 Arc(float angle) => (plane == 0 ? Vector3.Right : Vector3.Forward) * (MathF.Cos(angle) * r)
                                + Vector3.Up * (MathF.Sin(angle) * r + (MathF.Sin(angle) >= 0 ? h - r : r));
                            ImGuiDebugDrawer.DrawLine3D(basePos + Arc(a), basePos + Arc(b), Color.Yellow);
                        }
                    }
                    ImGuiDebugDrawer.DrawText3D("Bullet hit target", basePos + Vector3.Up * h, Color.Yellow, Color.Black);
                }
                if ((events.Count > 0 || World.Particles.Count > 0) && Main.Config.BulletPreview_Locked && TargetPresent && BulletMath.Finite(target))
                {
                    ImGuiDebugDrawer.DrawLine3D(target - Vector3.UnitX * 0.2f, target + Vector3.UnitX * 0.2f, Color.Cyan, thickness: 3);
                    ImGuiDebugDrawer.DrawLine3D(target - Vector3.UnitY * 0.2f, target + Vector3.UnitY * 0.2f, Color.Cyan, thickness: 3);
                    ImGuiDebugDrawer.DrawText3D("Bullet lock target", target + Vector3.Up * 0.2f, Color.Cyan, Color.Black);
                }
            }
        }
    }
}
