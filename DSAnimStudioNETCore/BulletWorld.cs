using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    // Parameter-driven preview scene. Special placement/follow policies are approximations.
    public sealed class BulletScene
    {
        public bool HasTarget, UnitCollision, GroundCollision, ForceStopUnit, Condition5Ground = true;
        public bool RespectLockCone = true;
        public Vector3 TargetAim, TargetBase, OwnerPosition, OwnerForward = Vector3.Forward;
        internal BulletScene Copy() => (BulletScene)MemberwiseClone();
        public float TargetRadius = .5f, TargetHeight = 2, GroundY, Type2Lift;
    }

    public sealed class BulletWorld
    {
        public readonly List<BulletParticle> Particles = new();
        public readonly List<string> Notices = new(), History = new();
        public BulletScene Scene = new();
        public Func<int, BulletDefinition> ResolveDefinition;
        public bool EnableDerivation = true;
        public int Dropped { get; private set; }
        public double Time { get; private set; }
        public int TotalBorn { get; private set; }
        private int serial;
        private readonly List<double> historyTimes = new();
        private readonly PriorityQueue<BulletParticle, (double, int)> pending = new();
        private readonly Dictionary<int, BulletDefinition> cache = new();
        private IReadOnlyDictionary<Func<Vector3>, Vector3> replayAnchors;
        internal Dictionary<Func<Vector3>, Vector3> SampleFollowAnchors()
            => Particles.Concat(pending.UnorderedItems.Select(x => x.Element)).Select(p => p.FollowAnchor)
                .Where(a => a != null).Distinct().ToDictionary(a => a, a => a());
        internal long HistorySize => 1024L + cache.Count * 48L + History.Sum(s => 32L + s.Length * 2L)
            + Particles.Concat(pending.UnorderedItems.Select(x => x.Element)).Sum(p => 512L + p.Trail.Count * 16L);
        internal BulletWorld CopyForHistory()
        {
            var copy = new BulletWorld(); copy.RestoreFrom(this); return copy;
        }
        internal void RestoreFrom(BulletWorld snapshot)
        {
            Reset(); Scene = snapshot.Scene.Copy(); ResolveDefinition = snapshot.ResolveDefinition;
            EnableDerivation = snapshot.EnableDerivation; Time = snapshot.Time; serial = snapshot.serial;
            TotalBorn = snapshot.TotalBorn; Dropped = snapshot.Dropped;
            Particles.AddRange(snapshot.Particles.Select(p => p.CopyForHistory()));
            foreach (var item in snapshot.pending.UnorderedItems) pending.Enqueue(item.Element.CopyForHistory(), item.Priority);
            foreach (var item in snapshot.cache) cache.Add(item.Key, item.Value);
            Notices.AddRange(snapshot.Notices); History.AddRange(snapshot.History); historyTimes.AddRange(snapshot.historyTimes);
        }
        public void Reset() { Particles.Clear(); pending.Clear(); cache.Clear(); Notices.Clear(); History.Clear(); historyTimes.Clear(); Time = 0; Dropped = TotalBorn = serial = 0; }
        public bool HasUnfinishedBullets => pending.Count > 0 || Particles.Any(p => p.Alive);
        public void Notice(string text) { if (Notices.Count < 16 && !Notices.Contains(text)) Notices.Add(text); }
        private void Log(double at, string text)
        {
            int index = historyTimes.FindIndex(t => t > at);
            if (index < 0) index = History.Count;
            historyTimes.Insert(index, at); History.Insert(index, text);
            if (History.Count > 128) { History.RemoveAt(0); historyTimes.RemoveAt(0); }
        }
        private static float Random(ref uint state) { state = state * 1664525u + 1013904223u; return (state >> 8) / 16777216f; }

        public void Emit(BulletDefinition d, Vector3 origin, Vector3 aim, bool locked, float delay, int seed, Func<Vector3> anchor = null,
            int eventId = 2, bool muzzleDirected = false)
            => Spawn(d, origin, aim, locked, Time + Math.Max(0, delay), unchecked((uint)seed), null, $"Event {eventId}", anchor, muzzleDirected);

        private void Spawn(BulletDefinition d, Vector3 origin, Vector3 aim, bool locked, double at, uint seed,
            BulletParticle parent, string reason, Func<Vector3> anchor, bool muzzleDirected = false)
        {
            if (d == null || !d.Valid || !d.Supported) { Notice($"Missing, invalid or unsupported Bullet {d?.ID}; omitted."); return; }
            cache[d.ID] = d;
            if (d.Game == SoulsAssetPipeline.SoulsGames.AC6)
                Notice("Armored Core VI: simplified flight/children; penetration tiers, missile phases, shotgun tables, distance lifetime and non-sphere hit shapes are not reproduced. Continuous bursts use legacy Bullet fields, without weapon-controller overrides.");
            if ((parent?.Depth ?? -1) >= 8) { Dropped++; Notice("Derivation depth limited to 8."); return; }
            if (d.Emit >= 1 || d.Follow != 0 || d.BallisticType != 0)
                Notice($"Bullet {d.ID}: placement {d.Emit}, follow {d.Follow}, ballistic {d.BallisticType} use preview policies.");
            if (d.AimOffset != 0 || d.TargetYOffset != 0 || d.AutoHoming)
                Notice("Automatic acquisition and random target offsets are not reproduced.");
            long total = (long)d.Count * (d.Game == SoulsAssetPipeline.SoulsGames.AC6 ? d.ContinueCount : 1);
            for (int shot = 0; shot < total; shot++)
            {
                int i = shot % d.Count;
                if (Particles.Count + pending.Count >= 256 || TotalBorn >= 4096)
                { Dropped = (int)Math.Min(int.MaxValue, Dropped + total - shot); Notice("Preview pool / replay birth budget reached; no terminal derivation for omitted bullets."); break; }
                var pos = origin;
                if (d.Emit == 1) pos = Scene.OwnerPosition;
                if ((d.Emit == 3 || d.Emit == 4) && Scene.HasTarget) pos = Scene.TargetBase;
                if (d.Emit == 5 && parent != null) pos = parent.Position;
                if (d.Emit == 2) pos.Y += Scene.Type2Lift;
                if (d.Emit == 6 && Scene.HasTarget && d.Game != SoulsAssetPipeline.SoulsGames.AC6)
                {
                    var horizontal = Scene.TargetBase - Scene.OwnerPosition; horizontal.Y = 0;
                    pos = Scene.TargetBase - BulletMath.Unit(horizontal, Scene.OwnerForward) * 2 + Vector3.Up * 5;
                    Notice("Placement 6 uses target +5m height / 2m toward shooter (preview approximation).");
                }
                if (d.Emit == 1 || d.Emit == 4)
                {
                    float angle = Random(ref seed) * MathHelper.TwoPi, radius = MathF.Sqrt(Random(ref seed)) * Math.Max(0, d.RandomCreateRadius);
                    pos += new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                }
                var direction = parent != null && d.OwnerOverride ? Scene.OwnerForward : aim;
                if (parent == null && locked && Scene.HasTarget && !muzzleDirected)
                    direction = BulletMath.Aim(pos, Scene.OwnerForward, aim, Scene.TargetAim, true, Scene.RespectLockCone ? d.LockCone : 180);
                if (!Scene.HasTarget && (d.Emit is 3 or 4 || d.Emit == 6 && d.Game != SoulsAssetPipeline.SoulsGames.AC6)) Notice($"Placement {d.Emit}: no target; using source position.");
                if (d.BallisticType == 1 && locked && Scene.HasTarget && !muzzleDirected)
                {
                    var offset = Scene.TargetAim - pos;
                    float distance = new Vector2(offset.X, offset.Z).Length(), v2 = d.Speed * d.Speed, g = d.GravityIn;
                    float discriminant = v2 * v2 - g * (g * distance * distance + 2 * offset.Y * v2);
                    if (g > 0 && distance > .001f && discriminant >= 0)
                    {
                        float pitch = MathF.Atan((v2 - MathF.Sqrt(discriminant)) / (g * distance));
                        direction = BulletMath.Unit(new Vector3(offset.X, 0, offset.Z), direction) * MathF.Cos(pitch) + Vector3.Up * MathF.Sin(pitch);
                    }
                    Notice("Ballistic 1: constant-gravity low arc approximation; unreachable / varying-gravity cases keep initial aim.");
                }
                if (d.Game == SoulsAssetPipeline.SoulsGames.AC6 && d.Emit == 6) direction = Vector3.Up;
                direction = BulletMath.Spread(direction, d.Yaw + i * d.YawInterval + (Random(ref seed) * 2 - 1) * d.RandomYaw,
                    d.Pitch + i * d.PitchInterval + (Random(ref seed) * 2 - 1) * d.RandomPitch);
                var p = new BulletParticle(d, pos, direction, locked)
                {
                    Serial = ++serial, ParentSerial = parent?.Serial ?? 0, ParentID = parent?.Definition.ID ?? -1,
                    Depth = (parent?.Depth ?? -1) + 1, SpawnReason = reason,
                    // ER numShoot is the size of one immediate volley. shootInterval belongs
                    // to the emitter's repeat timer, not the members of that volley.
                    BirthTime = at + (d.Game is SoulsAssetPipeline.SoulsGames.ER or SoulsAssetPipeline.SoulsGames.ERNR
                        ? 0 : d.Game == SoulsAssetPipeline.SoulsGames.AC6 ? shot / d.Count : i) * d.Interval,
                    RandomState = seed, FollowAnchor = anchor, NextChildAge = Math.Max(0, d.ChildWait),
                };
                if (d.Life >= 0 && d.LifeRandom != 0)
                {
                    float random = Random(ref p.RandomState);
                    p.EffectiveLife = Math.Max(0, d.Life + (d.Game == SoulsAssetPipeline.SoulsGames.AC6 ? random : random * 2 - 1) * Math.Abs(d.LifeRandom));
                }
                if (parent?.Definition.InheritSpeed == true) p.Speed = parent.Speed;
                p.LastAnchor = Anchor(p);
                pending.Enqueue(p, (p.BirthTime, p.Serial)); TotalBorn++;
            }
        }
        private Vector3 Anchor(BulletParticle p) => p.Definition.Follow switch
        {
            1 => p.FollowAnchor == null ? Scene.OwnerPosition : replayAnchors != null && replayAnchors.TryGetValue(p.FollowAnchor, out var recorded) ? recorded : p.FollowAnchor(),
            2 => Scene.OwnerPosition,
            3 => Scene.HasTarget ? Scene.TargetBase : p.LastAnchor,
            _ => Vector3.Zero
        };
        private void Child(BulletParticle parent, int id, double at, string reason, Vector3 origin)
        {
            if (!EnableDerivation || id < 0) return;
            if (!cache.TryGetValue(id, out var d))
            {
                try { d = ResolveDefinition?.Invoke(id); }
                catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
                { Notice($"Bullet {id}: {ex.Message}"); }
                cache[id] = d;
            }
            if (d == null) { Notice($"Missing Bullet {id} in the loaded parameter table; child omitted."); return; }
            var direction = parent.Direction;
            if (parent.Definition.Posture == 1) direction = BulletMath.Unit(new Vector3(direction.X, 0, direction.Z), Scene.OwnerForward);
            if (parent.Definition.Posture == 2 && parent.HitNormal != Vector3.Zero)
            { direction = parent.HitNormal; Notice("Posture 2 child orientation uses surface normal (approximation)."); }
            Spawn(d, origin, direction, parent.Locked, at, parent.RandomState + (uint)serial, parent, reason, parent.FollowAnchor);
        }
        private void End(BulletParticle p)
        {
            if (p.EndHandled) return;
            p.EndHandled = true; p.DeathTime = p.BirthTime + p.Age;
            Log(p.DeathTime, $"{p.DeathTime:0.###}s #{p.Serial} Bullet {p.Definition.ID}: {p.EndReason}");
            bool unit = p.EndReason == "Unit hit", ground = p.EndReason == "Ground hit", life = p.EndReason == "Lifetime";
            if (!(unit || ground || life)) return;
            int c = p.Definition.LaunchCondition;
            bool launch = c switch { 0 => true, 3 => !p.HitUnit, 4 => p.HitUnit, 5 => ground && Scene.Condition5Ground,
                254 => life, 255 => unit || ground, _ => false };
            if (p.Definition.HitBulletID >= 0 && c == 5) Notice("Launch condition 5 is treated as ground-only when enabled; native meaning unverified.");
            if (p.Definition.HitBulletID >= 0 && (c == 1 || c == 2 || c == 6 || (c > 6 && c < 254)))
                Notice($"Launch condition {c}: water / absorption / unknown conditions are not simulated.");
            if (launch) Child(p, p.Definition.HitBulletID, p.DeathTime, p.EndReason, unit || ground ? p.HitPosition : p.Position);
        }
        public void Advance(float seconds, Vector3 target, float previewLifetime, float retention)
            => AdvanceRecorded(seconds, target, previewLifetime, retention, null);
        internal void AdvanceRecorded(float seconds, Vector3 target, float previewLifetime, float retention,
            IReadOnlyDictionary<Func<Vector3>, Vector3> anchors)
        {
            replayAnchors = anchors;
            try { AdvanceCore(seconds, target, previewLifetime, retention); }
            finally { replayAnchors = null; }
        }
        private void AdvanceCore(float seconds, Vector3 target, float previewLifetime, float retention)
        {
            if (!float.IsFinite(seconds) || seconds <= 0) return;
            seconds = Math.Min(seconds, 30); previewLifetime = float.IsFinite(previewLifetime) ? Math.Clamp(previewLifetime, .01f, 30) : 8;
            retention = float.IsFinite(retention) ? Math.Clamp(retention, 0, 30) : 3;
            double until = Time + seconds;
            foreach (var p in Particles.ToArray()) if (p.Alive) AdvanceParticle(p, (float)(until - Math.Max(Time, p.BirthTime)), target, previewLifetime);
            while (pending.TryPeek(out var p, out var priority) && priority.Item1 <= until + 1e-8)
            {
                pending.Dequeue(); Particles.Add(p);
                Log(p.BirthTime, $"{p.BirthTime:0.###}s #{p.Serial} Bullet {p.Definition.ID} <- {(p.ParentSerial == 0 ? "Event 2" : $"#{p.ParentSerial} / {p.ParentID}")} [{p.SpawnReason}]");
                AdvanceParticle(p, (float)Math.Max(0, until - p.BirthTime), target, previewLifetime);
            }
            Time = until;
            Particles.RemoveAll(p => !p.Alive && until - p.DeathTime > retention);
        }
        private void AdvanceParticle(BulletParticle p, float duration, Vector3 target, float cap)
        {
            var d = p.Definition;
            float life = p.EffectiveLife < 0 ? cap : Math.Min(cap, p.EffectiveLife);
            var anchor = Anchor(p); var shift = anchor - p.LastAnchor; p.LastAnchor = anchor;
            if (d.Follow is >= 1 and <= 3) p.Position += shift;
            while (p.Alive)
            {
                if (p.Age >= life - 1e-6f) { p.EndReason = p.EffectiveLife >= 0 && p.EffectiveLife <= cap ? "Lifetime" : "Preview time limit"; break; }
                if (EnableDerivation && d.ChildID >= 0 && d.ChildInterval > 0 && (d.ChildCountLimit <= 0 || p.IntervalChildrenEmitted < d.ChildCountLimit) && p.Age >= p.NextChildAge - 1e-6)
                {
                    Child(p, d.ChildID, p.BirthTime + p.Age, "Interval", p.Position);
                    p.IntervalChildrenEmitted++;
                    float min = Math.Max(0, Math.Min(d.ChildIntervalMin, d.ChildInterval)), max = Math.Max(min, d.ChildInterval);
                    p.NextChildAge += Math.Max(1f / 120, min + Random(ref p.RandomState) * (max - min));
                }
                if (duration <= 1e-7f) break;
                float dt = Math.Min(1f / 120, Math.Min(duration, life - p.Age));
                if (EnableDerivation && d.ChildID >= 0 && d.ChildInterval > 0 && (d.ChildCountLimit <= 0 || p.IntervalChildrenEmitted < d.ChildCountLimit)) dt = Math.Min(dt, (float)Math.Max(1e-7, p.NextChildAge - p.Age));
                var old = p.Position; float age = p.Age, travel = p.Travel, radius = p.Radius, speed = p.Speed, downSpeed = p.DownSpeed;
                var externalVelocity = p.ExternalVelocity;
                if (d.ReturnsToOwner)
                    p.Direction = BulletMath.LimitDirection(p.Direction, Scene.OwnerPosition - p.Position, MathHelper.ToRadians(d.Homing > 0 ? d.Homing : 180) * dt);
                bool savedLock = p.Locked; if (!Scene.HasTarget || d.ReturnsToOwner || d.KeepsDirection) p.Locked = false;
                p.Advance(dt, target, cap); p.Locked = savedLock; duration -= dt;
                if (d.Follow == 4) p.Position.Y = Scene.GroundY + Math.Max(0, p.Radius);
                float t = 2, unitContact = 2; Vector3 normal = Vector3.Zero; string reason = null;
                float hitRadius = Math.Max(0, Math.Max(radius, p.Radius));
                if (Scene.HasTarget && Scene.UnitCollision && Capsule(old, p.Position, hitRadius, out float unitT, out var unitNormal))
                {
                    unitContact = unitT;
                    if (!d.PenetrateCharacter || Scene.ForceStopUnit) { t = unitT; normal = unitNormal; reason = "Unit hit"; }
                }
                float mapRadius = d.MapRadius < 0 ? hitRadius : d.MapRadius;
                if (Scene.GroundCollision && !d.PenetrateMap && d.Follow != 4 && p.Position.Y < old.Y - 1e-7f && p.Position.Y - mapRadius <= Scene.GroundY)
                {
                    float groundT = Math.Clamp((old.Y - mapRadius - Scene.GroundY) / (old.Y - p.Position.Y), 0, 1);
                    if (groundT < t) { t = groundT; normal = Vector3.Up; reason = "Ground hit"; }
                }
                if (unitContact <= Math.Min(1, t)) p.HitUnit = true;
                if (reason == null && d.ReturnsToOwner && p.Travel > .5f)
                {
                    var segment = p.Position - old;
                    float fraction = segment.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(Scene.OwnerPosition - old, segment) / segment.LengthSquared(), 0, 1) : 0;
                    if (Vector3.DistanceSquared(Vector3.Lerp(old, p.Position, fraction), Scene.OwnerPosition) <= .25f * .25f)
                    { t = fraction; reason = "Returned to owner"; }
                }
                if (reason != null)
                {
                    bool appendedEndpoint = p.Trail.Count > 1 && p.Trail[^1] == p.Position;
                    p.Position = Vector3.Lerp(old, p.Position, t); p.Age = age + dt * t; p.Travel = MathHelper.Lerp(travel, p.Travel, t);
                    p.Speed = MathHelper.Lerp(speed, p.Speed, t); p.DownSpeed = MathHelper.Lerp(downSpeed, p.DownSpeed, t);
                    p.Radius = MathHelper.Lerp(radius, p.Radius, t); p.ExternalVelocity = Vector3.Lerp(externalVelocity, p.ExternalVelocity, t);
                    p.HitNormal = normal; p.HitPosition = p.Position - normal * (reason == "Ground hit" ? mapRadius : hitRadius);
                    p.EndReason = reason;
                    // The core may append the end-of-step sample; keep the displayed endpoint at contact.
                    if (appendedEndpoint) { p.Trail.RemoveAt(p.Trail.Count - 1); p.TrailAges.RemoveAt(p.TrailAges.Count - 1); }
                    p.Trail.Add(p.Position); p.TrailAges.Add(p.Age);
                }
            }
            if (!p.Alive) End(p);
        }
        // Swept sphere against a vertical capsule: finite cylinder + both spherical caps.
        private bool Capsule(Vector3 from, Vector3 to, float bulletRadius, out float time, out Vector3 normal)
        {
            float r = Math.Max(.001f, Scene.TargetRadius), expanded = r + bulletRadius;
            float bottom = Scene.TargetBase.Y + r, top = Scene.TargetBase.Y + Math.Max(r, Scene.TargetHeight - r);
            Vector3 center = new(Scene.TargetBase.X, Math.Clamp(from.Y, bottom, top), Scene.TargetBase.Z);
            var move = to - from; float best = 2;
            if (Vector3.DistanceSquared(from, center) <= expanded * expanded) best = 0;
            void Sphere(float y)
            {
                var off = from - new Vector3(Scene.TargetBase.X, y, Scene.TargetBase.Z);
                float a = move.LengthSquared(), b = Vector3.Dot(off, move), c = off.LengthSquared() - expanded * expanded;
                float disc = b * b - a * c;
                if (a > 1e-12f && disc >= 0) { float value = (-b - MathF.Sqrt(disc)) / a; if (value >= 0 && value <= 1) best = Math.Min(best, value); }
            }
            Sphere(bottom); Sphere(top);
            float x = from.X - Scene.TargetBase.X, z = from.Z - Scene.TargetBase.Z;
            float aa = move.X * move.X + move.Z * move.Z, bb = x * move.X + z * move.Z, cc = x * x + z * z - expanded * expanded;
            float dd = bb * bb - aa * cc;
            if (aa > 1e-12f && dd >= 0)
            {
                float value = (-bb - MathF.Sqrt(dd)) / aa, y = from.Y + value * move.Y;
                if (value >= 0 && value <= 1 && y >= bottom && y <= top) best = Math.Min(best, value);
            }
            time = best;
            var point = Vector3.Lerp(from, to, Math.Min(best, 1)); center.Y = Math.Clamp(point.Y, bottom, top);
            normal = BulletMath.Unit(point - center, BulletMath.Unit(-move, Vector3.Up));
            return best <= 1;
        }
    }
}
