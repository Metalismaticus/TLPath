using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace NavMod
{
    /// <summary>
    /// Shared coordinate helpers used by both TlPathService and CompassRibbonRenderer.
    /// Fixes issue #6: AbsToHud3 was duplicated in both classes.
    /// </summary>
    internal static class NavUtils
    {
        public static Vec3d AbsToHud3(ICoreClientAPI capi, double absX, double absY, double absZ)
        {
            var sp = capi.World.DefaultSpawnPosition;
            return new Vec3d(absX - sp.X, absY, absZ - sp.Z);
        }

        public static Vec3d HudToAbs3(ICoreClientAPI capi, double hudX, double yWorld, double hudZ)
        {
            var sp = capi.World.DefaultSpawnPosition;
            return new Vec3d(hudX + sp.X, yWorld, hudZ + sp.Z);
        }

        public static double Dist2Hud(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        public static double Hud2Dist(in Vec3d a, in Vec3d b) =>
            System.Math.Sqrt(Dist2Hud(a, b));

        public static double Hud3Dist(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}