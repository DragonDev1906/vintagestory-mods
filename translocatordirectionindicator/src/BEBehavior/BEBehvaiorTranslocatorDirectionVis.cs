using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

using Vintagestory.GameContent;

namespace TranslocatorDirectionIndicator {
    // This logic should probably be on the BlockEntity, but that causes
    // compatibility issues with other mods.
    internal class BEBehaviorTranslocatorDirectionVis : BlockEntityBehavior {
        public SimpleParticleProperties directionParticles;

        public BlockEntityStaticTranslocator Translocator {
            get {
                return ((BlockEntityStaticTranslocator)Blockentity);
            }
        }

        // Constructor
        public BEBehaviorTranslocatorDirectionVis(BlockEntity blockentity): base(blockentity) { }

        public override void Initialize(ICoreAPI api, JsonObject properties) {
            base.Initialize(api, properties);

            // I still have no clue why the base game stores this in the Block
            // (probably performance/memory allocs). For simplicity we're
            // storing it here, though we can probably also just generate it
            // when needed.
            directionParticles = new SimpleParticleProperties(
                0.5f, 1, // Min/Max quantity
                ColorUtil.ToRgba(0, 0, 255, 128),
                new Vec3d(),new Vec3d(), // Min/max position
                new Vec3f(0.1f, -0.1f, 0),new Vec3f(0.1f, 0.1f, 0), // Min/Max velocity
                1.5f, 0, // lifeLength, gravity
                0.1f, 1f, // Min/Max size
                EnumParticleModel.Quad
            );

            if (api.World.Side == EnumAppSide.Client) {
                api.Logger.Event("BEBehaviorTranslocatorDirectionVis initialized (Client)");
                Translocator.RegisterGameTickListener(OnClientGameTick, 50);
            }
        }

        private void OnClientGameTick(float dt) {
            if (Translocator.tpLocation == null) {
                return;
            }

            var dir = new Vec3f(Translocator.tpLocation.X - Pos.X, Translocator.tpLocation.Y - Pos.Y, Translocator.tpLocation.Z - Pos.Z);
            var distance = dir.Length();
            var speed = mapRange(distance, 0, Translocator.MaxTeleporterRangeInBlocks, 0.1f, 0.5f);
            // var speed = 0.1f + distance / base.MaxTeleporterRangeInBlocks * 0.2; // Map to (roughly) [0.1, 1.1]
            dir.Normalize();
            dir = dir.Mul(speed);

            directionParticles.MinPos.Set(Pos.X + 0.5, Pos.Y + 2.7, Pos.Z + 0.5);
            directionParticles.AddPos.Set(0.01f, 0.01f, 0.01f);
            directionParticles.MinVelocity.Set(dir.X, dir.Y, dir.Z);
            directionParticles.AddVelocity.Set(0.01f, 0.01f, 0.01f);
            directionParticles.MinQuantity = 0;
            directionParticles.AddQuantity = 1f;
            directionParticles.SelfPropelled = true;

            int r = 53;
            int g = 221;
            int b = 172;
            directionParticles.Color = (r << 16) | (g << 8) | (b << 0) | (100 << 24);

            directionParticles.BlueEvolve = null;
            directionParticles.RedEvolve = null;
            directionParticles.GreenEvolve = null;
            directionParticles.MinSize = 0.1f;
            directionParticles.MaxSize = 0.11f;
            directionParticles.SizeEvolve = null;
            directionParticles.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -10f);

            // directionParticles.MinPos.Set(Pos.X, Pos.Y, Pos.Z);
            // directionParticles.AddPos.Set(1,1,1);
            Api.World.SpawnParticles(directionParticles);
        }

        static float mapRange(float value, float fromMin, float fromMax, float toMin, float toMax) {
            return toMin + (value - fromMin) * (toMax - toMin) / (fromMax - fromMin);
        }
    }
}
