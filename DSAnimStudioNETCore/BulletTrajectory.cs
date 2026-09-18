using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using SoulsFormats;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    // Per-game Bullet parameter layouts; motion integration is an explicitly
    // bounded preview model, not a port of the native Bullet controller.
    public sealed class BulletDefinition
    {
        public int ID;
        public int FlightSfx = -1, HitSfx = -1;
        public SoulsGames Game = SoulsGames.ERNR;
        public int MuzzleDummy = -1, ShapeType, ShotgunID = -1, ChildCountLimit;
        public float HomingBeginTime;
        public int ContinueCount = 1;
        public bool ReturnsToOwner => Game != SoulsGames.AC6 && Follow == 5;
        public bool KeepsDirection => Game == SoulsGames.AC6 && Follow == 5;

        public string Name;
        public float Life, Range, Interval, GravityIn, GravityOut, StopHomingRange;
        public float Speed, AccelIn, AccelOut, MaxSpeed, MinSpeed, AccelDelay, HomingBegin;
        public float Radius, MaxRadius, SpreadTime, Yaw, Pitch, YawInterval, PitchInterval;
        public float RandomYaw, RandomPitch, Homing, HomingPitch;
        public int Count = 1, Follow, Emit, BallisticType, ChildID = -1;
        public int HitBulletID = -1, LaunchCondition, Posture;
        public float ChildIntervalMin, ChildWait, RandomCreateRadius, MapRadius = -1;
        public bool PenetrateCharacter, PenetrateMap, OwnerOverride, InheritSpeed;
        public float ChildInterval, ExternalForce, LifeRandom, AimOffset, TargetYOffset;
        public bool AutoHoming;
        public float LockCone;

        public static BulletDefinition Read(BinaryReaderEx br, int id, string name, SoulsGames game = SoulsGames.ERNR, long rowLength = -1)
        {
            long start = br.Position;
            if (rowLength < 0) rowLength = br.Length - start;
            var layout = BulletParamLayout.For(game);
            double N(string field, double fallback = 0) => layout.Read(br, start, rowLength, field, fallback);
            float F(string field, float fallback = 0) => (float)N(field, fallback);
            int I(string field, int fallback = 0) => (int)N(field, fallback);
            bool B(string field) => N(field) != 0;
            var d = new BulletDefinition
            {
                ID = id, Name = name, Game = game,
                FlightSfx = I("sfxId_Bullet", I("sfxIdBullet", -1)),
                HitSfx = I("sfxId_Hit", I("sfxIdHit", -1)),
                Life = F("life"), Range = F("dist"), Interval = F("shootInterval"),
                GravityIn = F("gravityInRange"), GravityOut = F("gravityOutRange"),
                StopHomingRange = F("hormingStopRange", F("homingStopRange")),
                Speed = F("initVellocity"), AccelIn = F("accelInRange"), AccelOut = F("accelOutRange"),
                MaxSpeed = F("maxVellocity"), MinSpeed = F("minVellocity"), AccelDelay = F("accelTime"), HomingBegin = F("homingBeginDist"),
                HomingBeginTime = F("homingBeginTime"), Radius = F("hitRadius"), MaxRadius = F("hitRadiusMax", -1), SpreadTime = F("spreadTime"),
                Count = I("numShoot", 1), Homing = F("homingAngle"), HomingPitch = F("homingAngleX", -1),
                Yaw = F("shootAngle"), YawInterval = F("shootAngleInterval"), PitchInterval = F("shootAngleXInterval"),
                Pitch = unchecked((sbyte)I("shootAngleXZ")), LockCone = F("lockShootLimitAng"),
                Follow = I("FollowType"), Emit = I("EmittePosType"), BallisticType = I("ballisticCalcType", B("isHowitzer") ? 1 : 0),
                AutoHoming = B("isEnableAutoHoming"), RandomYaw = F("shootAngleYMaxRandom"), RandomPitch = F("shootAngleXMaxRandom"),
                HitBulletID = I("HitBulletID", -1), LaunchCondition = I("launchConditionType"),
                ChildID = I("intervalCreateBulletId", -1), ChildIntervalMin = F("intervalCreateTimeMin"),
                ChildInterval = F("intervalCreateTimeMax"), ChildWait = F("intervalCreateWaitTime"),
                PenetrateCharacter = B("isPenetrateChr") || B("isPenetrate") || B("charaPenetrateType"), PenetrateMap = B("isPenetrateMap"),
                OwnerOverride = B("isOwnerOverrideInitAngle"), InheritSpeed = B("isInheritSpeedToChild"), Posture = I("sfxPostureType"),
                RandomCreateRadius = F("randomCreateRadius"), MapRadius = F("forMapHitRadius", F("hitRadiusForMap", -1)),
                ExternalForce = F("externalForce"), LifeRandom = F("lifeRandomRange", F("lifeAddRand")), AimOffset = F("hormingOffsetRange"), TargetYOffset = F("targetYOffsetRange"),
                MuzzleDummy = I("shootDmyPolyId", -1), ShapeType = I("shapeType"), ShotgunID = I("shotgunParamId", -1), ChildCountLimit = I("intervalCreateBulletMax"),
                ContinueCount = Math.Max(1, I("numContinueShoot", 1)),
            };
            if (game == SoulsGames.AC6 && d.Count == 0) d.Count = 1;
            return d;
        }

        public bool Valid => float.IsFinite(Life) && Life >= -1 && float.IsFinite(Speed) && Speed >= 0
            && float.IsFinite(Range) && float.IsFinite(Interval) && Interval >= 0
            && float.IsFinite(GravityIn) && float.IsFinite(GravityOut)
            && float.IsFinite(AccelIn) && float.IsFinite(AccelOut) && float.IsFinite(AccelDelay)
            && float.IsFinite(MinSpeed) && float.IsFinite(MaxSpeed) && MaxSpeed >= MinSpeed
            && float.IsFinite(Radius) && Radius >= -1 && float.IsFinite(MaxRadius)
            && float.IsFinite(SpreadTime) && float.IsFinite(RandomYaw) && float.IsFinite(RandomPitch)
            && float.IsFinite(HomingBegin) && float.IsFinite(StopHomingRange) && Count > 0
            && float.IsFinite(ChildIntervalMin) && float.IsFinite(ChildInterval) && float.IsFinite(ChildWait)
            && float.IsFinite(RandomCreateRadius) && float.IsFinite(MapRadius) && float.IsFinite(LifeRandom)
            && float.IsFinite(ExternalForce) && float.IsFinite(HomingBeginTime);
        public bool Supported => Follow >= 0 && Follow <= (Game is SoulsGames.DS1R or SoulsGames.BB ? 4 : 5)
            && Emit >= 0 && Emit <= (Game is SoulsGames.DS1R or SoulsGames.BB ? 4 : 6) && BallisticType >= 0 && BallisticType <= 1;
    }

    public static class BulletMath
    {
        public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        public static Vector3 Unit(Vector3 v, Vector3 fallback) => Finite(v) && v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : fallback;

        public static Vector3 LimitDirection(Vector3 from, Vector3 to, float radians)
        {
            from = Unit(from, Vector3.Forward); to = Unit(to, from);
            float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
            float angle = MathF.Acos(dot);
            if (angle <= radians || angle < 1e-6f) return to;
            if (radians <= 0) return from;
            Vector3 axis = Vector3.Cross(from, to);
            if (axis.LengthSquared() < 1e-8f)
                axis = Vector3.Cross(from, Math.Abs(from.Y) < 0.9f ? Vector3.Up : Vector3.Right);
            return Unit(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians)), from);
        }

        public static Vector3 Aim(Vector3 origin, Vector3 forward, Vector3 unlockedForward, Vector3 target, bool locked, float coneDegrees)
            => locked ? LimitDirection(forward, target - origin, MathHelper.ToRadians(Math.Clamp(coneDegrees, 0, 180)))
                : Unit(unlockedForward, Unit(forward, Vector3.Forward));

        public static Vector3 Spread(Vector3 direction, float yawDegrees, float pitchDegrees)
        {
            direction = Unit(direction, Vector3.Forward);
            // Spread belongs to the emission frame, including a parent traveling
            // vertically. World-Y yaw would collapse such a volley into a plane.
            var right = Unit(Vector3.Cross(direction, Vector3.Up), Vector3.Right);
            var up = Unit(Vector3.Cross(right, direction), Vector3.Up);
            var yaw = Quaternion.CreateFromAxisAngle(up, MathHelper.ToRadians(yawDegrees));
            var yawed = Vector3.Transform(direction, yaw);
            var pitchAxis = Vector3.Transform(right, yaw);
            return Unit(Vector3.Transform(yawed, Quaternion.CreateFromAxisAngle(pitchAxis, MathHelper.ToRadians(pitchDegrees))), direction);
        }

        public static Vector3 Home(Vector3 direction, Vector3 desired, float yawBudget, float pitchBudget)
        {
            direction = Unit(direction, Vector3.Forward); desired = Unit(desired, direction);
            float yaw = MathF.Atan2(-direction.X, -direction.Z), pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1));
            float targetYaw = MathF.Atan2(-desired.X, -desired.Z), targetPitch = MathF.Asin(Math.Clamp(desired.Y, -1, 1));
            yaw += Math.Clamp(MathHelper.WrapAngle(targetYaw - yaw), -yawBudget, yawBudget);
            pitch += Math.Clamp(targetPitch - pitch, -pitchBudget, pitchBudget);
            return new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        }
    }

    public sealed class BulletParticle
    {
        public BulletDefinition Definition;
        public Vector3 Position, Direction;
        public bool Locked;
        public float Age, Travel, Speed, DownSpeed, Radius;
        public float DisplayRadius => float.IsFinite(Radius) ? Math.Max(Radius, .025f) : .025f;
        public string EndReason;
        public bool Alive => EndReason == null;
        public List<Vector3> Trail { get; private set; } = new();
        public List<float> TrailAges { get; private set; } = new();
        private float trailTime;
        public int Serial, ParentSerial, ParentID = -1, Depth;
        public string SpawnReason = "Event 2";
        public double BirthTime, DeathTime, NextChildAge;
        public float EffectiveLife;
        public bool EndHandled, HitUnit;
        public int IntervalChildrenEmitted;
        public uint RandomState;
        public Vector3 InitialDirection, LastAnchor, HitPosition, HitNormal, ExternalVelocity;
        public Func<Vector3> FollowAnchor;

        public BulletParticle(BulletDefinition definition, Vector3 position, Vector3 direction, bool locked)
        {
            Definition = definition; Position = position; Direction = BulletMath.Unit(direction, Vector3.Forward);
            Locked = locked; Speed = definition.Speed; Radius = definition.Radius; Trail.Add(position); TrailAges.Add(0);
            EffectiveLife = definition.Life; InitialDirection = Direction; HitPosition = position;
        }

        internal BulletParticle CopyForHistory()
        {
            var copy = (BulletParticle)MemberwiseClone();
            copy.Trail = new List<Vector3>(Trail);
            copy.TrailAges = new List<float>(TrailAges);
            return copy;
        }

        public void Advance(float seconds, Vector3 target, float previewLifetime)
        {
            float remaining = seconds;
            float lifetime = EffectiveLife < 0 ? previewLifetime : Math.Min(EffectiveLife, previewLifetime);
            while (Alive && remaining > 1e-7f)
            {
                float dt = Math.Min(1f / 120, Math.Min(remaining, Math.Max(0, lifetime - Age)));
                if (dt <= 1e-7f) { EndReason = EffectiveLife >= 0 && EffectiveLife <= previewLifetime ? "Lifetime" : "Preview time limit"; break; }
                var d = Definition;
                var toTarget = target - Position;
                if (Locked && d.Homing > 0 && Age >= Math.Max(0, d.HomingBeginTime) && Travel >= Math.Max(0, d.HomingBegin)
                    && toTarget.Length() > Math.Max(0, d.StopHomingRange))
                    Direction = BulletMath.Home(Direction, toTarget, MathHelper.ToRadians(d.Homing) * dt,
                        MathHelper.ToRadians(Math.Max(0, d.HomingPitch < 0 ? d.Homing : d.HomingPitch)) * dt);
                bool insideRange = Travel < d.Range;
                float activeDt = Math.Clamp(Age + dt - Math.Max(0, d.AccelDelay), 0, dt);
                float nextSpeed = Speed + (insideRange ? d.AccelIn : d.AccelOut) * activeDt;
                nextSpeed = Math.Max(Math.Max(0, d.MinSpeed), nextSpeed);
                if (d.MaxSpeed >= 0) nextSpeed = Math.Min(nextSpeed, d.MaxSpeed);
                float gravity = insideRange ? d.GravityIn : d.GravityOut;
                var move = Direction * ((Speed + nextSpeed) * 0.5f * dt)
                    - Vector3.Up * (DownSpeed * dt + 0.5f * gravity * dt * dt);
                var force = new Vector3(InitialDirection.X, 0, InitialDirection.Z) * d.ExternalForce;
                move += ExternalVelocity * dt + force * (0.5f * dt * dt);
                ExternalVelocity += force * dt;
                Position += move; Travel += move.Length(); Speed = nextSpeed; DownSpeed += gravity * dt;
                Age += dt; remaining -= dt; trailTime += dt;
                Radius = d.MaxRadius < 0 ? d.Radius : MathHelper.Lerp(d.Radius, d.MaxRadius,
                    d.SpreadTime <= 0 ? 1 : Math.Clamp(Age / d.SpreadTime, 0, 1));
                if (!BulletMath.Finite(Position) || !float.IsFinite(Speed) || Travel > 10000)
                    EndReason = "Preview distance / numeric limit";
                if (trailTime >= 1f / 30 || Age >= lifetime - 1e-6f || !Alive)
                {
                    Trail.Add(Position); TrailAges.Add(Age); trailTime = 0;
                    if (Trail.Count > 512) { Trail.RemoveAt(0); TrailAges.RemoveAt(0); }
                }
            }
            if (Age >= lifetime - 1e-6f && Alive)
                EndReason = EffectiveLife >= 0 && EffectiveLife <= previewLifetime ? "Lifetime" : "Preview time limit";
        }
    }

}
