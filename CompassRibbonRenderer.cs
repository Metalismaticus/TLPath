using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace NavMod
{
    /// <summary>
    /// Ribbon-compass + target marker + 3D waypoint queue.
    /// TL progress on top, current HUD target below.
    /// Switch:
    ///  - intermediate points: 3D threshold (ReachDistBlocks)
    ///  - final point: 2D HUD threshold (FinalReachHud)
    ///  - TL pair: skip exit node after hitting entry
    ///  Additionally:
    ///  - Idle compass: when no route but showCompassIdle=true, draw compass without targets.
    ///  - Smooth fade/slide animation in/out for idle visibility.
    /// </summary>
    public class CompassRibbonRenderer : IRenderer
    {
        readonly ICoreClientAPI capi;
        public bool Enabled = true;

        // Idle compass
        bool showCompassIdle = false;
        float idleAnim01 = 0f; // 0..1
        const float IdleAnimSpeed = 10f; // higher = faster

        public void SetIdleVisible(bool on) => showCompassIdle = on;

        // Current target (ABS) for compass marker
        public double? TargetX = null;
        public double? TargetZ = null;

        // Route: ABS and HUD (XZ in HUD, Y absolute)
        readonly List<Vec3d> routeAbs = new();
        readonly List<Vec3d> routeHud = new();
        readonly List<bool>  isTeleportEdge = new(); // edge i->i+1: true = TL
        int routePos = 0;

        // Thresholds
        public double ReachDistBlocks = 1;   // 3D for intermediate points
        public double FinalReachHud   = 10;  // 2D HUD for the final point

        // TL progress
        int TotalTeleports  => isTeleportEdge.Count > 0 ? CountTrue(isTeleportEdge) : 0;
        int PassedTeleports => CountTrue(isTeleportEdge, 0, Math.Min(routePos, isTeleportEdge.Count));

        // HUD target for bottom line
        bool hasHudTarget = false;
        Vec3d currentHudTarget;

        // Text / fonts
        readonly TextTextureUtil ttu;
        readonly CairoFont fontWhite;
        readonly CairoFont fontRed;
        readonly CairoFont fontHud;
        readonly CairoFont fontHudBlack;

        LoadedTexture nTex, eTex, sTex, wTex;
        LoadedTexture nTexBlack, eTexBlack, sTexBlack, wTexBlack;

        LoadedTexture hudTopWhite, hudTopBlack;
        LoadedTexture hudBotWhite, hudBotBlack;

        // Heading smoothing
        double headingAcc;
        bool accInit;

        struct HudCfg
        {
            public float RibbonWidthPx;
            public float RibbonHeightPx;
            public float RibbonTopPct;
            public int BgAlpha;
            public int BorderAlpha;
            public int TickAlpha;
            public int CenterAlpha;
        }
        static readonly HudCfg cfg = new HudCfg
        {
            RibbonWidthPx = 400f,
            RibbonHeightPx = 40f,
            RibbonTopPct = 0.10f,
            BgAlpha = 90,
            BorderAlpha = 140,
            TickAlpha = 210,
            CenterAlpha = 200
        };

        public CompassRibbonRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            ttu = new TextTextureUtil(capi);

            fontWhite = CairoFont.WhiteSmallText().WithFontSize(20);
            fontRed   = CairoFont.WhiteSmallText().WithFontSize(20);
            fontRed.Color = new double[] { 1, 0.2, 0.2, 1 };

            fontHud = CairoFont.WhiteSmallText().WithFontSize(18);
            fontHudBlack = CairoFont.WhiteSmallText().WithFontSize(18);
            fontHudBlack.Color = new double[] { 0, 0, 0, 1 };

            nTex = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("N", fontRed,   ref nTex);
            eTex = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("E", fontWhite, ref eTex);
            sTex = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("S", fontWhite, ref sTex);
            wTex = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("W", fontWhite, ref wTex);

            var black = CairoFont.WhiteSmallText().WithFontSize(20);
            black.Color = new double[] { 0, 0, 0, 1 };
            nTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("N", black, ref nTexBlack);
            eTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("E", black, ref eTexBlack);
            sTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("S", black, ref sTexBlack);
            wTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("W", black, ref wTexBlack);

            hudTopWhite = new LoadedTexture(capi);
            hudTopBlack = new LoadedTexture(capi);
            hudBotWhite = new LoadedTexture(capi);
            hudBotBlack = new LoadedTexture(capi);
        }

        // === Access for service: planned TL entry/exit in HUD and skipping ===
        public bool TryGetPlannedTeleportPair(out Vec3d entryHud, out Vec3d exitHud)
        {
            entryHud = null; exitHud = null;
            if (routePos >= 0 && routePos < routeHud.Count &&
                routePos < isTeleportEdge.Count && isTeleportEdge[routePos] &&
                routePos + 1 < routeHud.Count)
            {
                entryHud = routeHud[routePos];
                exitHud  = routeHud[routePos + 1];
                return true;
            }
            return false;
        }

        public bool AdvancePastTeleportPair()
        {
            if (routePos < isTeleportEdge.Count && isTeleportEdge[routePos] && routePos + 1 < routeAbs.Count)
            {
                AdvanceBy(2);
                return true;
            }
            return false;
        }

        // New entry: points + TL-edge flags (ABS3)
        public void SetRoute3(List<Vec3d> absPoints, List<bool> tlEdgeFlags)
        {
            routeAbs.Clear(); routeHud.Clear(); isTeleportEdge.Clear();
            routePos = 0; hasHudTarget = false; TargetX = TargetZ = null;

            if (absPoints != null) routeAbs.AddRange(absPoints);
            foreach (var p in routeAbs) routeHud.Add(AbsToHud3(p.X, p.Y, p.Z));

            if (tlEdgeFlags != null && tlEdgeFlags.Count == Math.Max(0, routeAbs.Count - 1))
                isTeleportEdge.AddRange(tlEdgeFlags);
            else
                for (int i = 0; i + 1 < routeHud.Count; i++)
                    isTeleportEdge.Add(Hud2Dist(routeHud[i], routeHud[i + 1]) <= 1.5);

            // Skip initial points if we already stand on them (3D)
            if (routeHud.Count > 0)
            {
                var pl = capi.World.Player.Entity.Pos;
                var plHud = AbsToHud3(pl.X, pl.Y, pl.Z);
                while (routePos < routeHud.Count && Hud3Dist(plHud, routeHud[routePos]) <= ReachDistBlocks)
                    routePos++;
            }

            if (routePos < routeAbs.Count)
            {
                TargetX = routeAbs[routePos].X;
                TargetZ = routeAbs[routePos].Z;
                currentHudTarget = routeHud[routePos];
                hasHudTarget = true;
            }
        }

        // Backward compat: 2D points (Y = player's current)
        public void SetTargetsQueue(IEnumerable<Vec2d> points)
        {
            var list = new List<Vec3d>();
            double y = capi.World?.Player?.Entity?.Pos?.Y ?? 0;
            if (points != null) foreach (var p in points) list.Add(new Vec3d(p.X, y, p.Y));
            SetRoute3(list, null);
        }

        public void ClearTargetsQueue()
        {
            routeAbs.Clear(); routeHud.Clear(); isTeleportEdge.Clear();
            routePos = 0; hasHudTarget = false; TargetX = TargetZ = null;
        }

        void AdvanceBy(int steps)
        {
            routePos += steps;
            if (routePos >= routeAbs.Count)
            {
                SetTarget(null, null); hasHudTarget = false;
                capi.ShowChatMessage("[tl] route completed");
                return;
            }
            TargetX = routeAbs[routePos].X; TargetZ = routeAbs[routePos].Z;
            currentHudTarget = routeHud[routePos]; hasHudTarget = true;
            capi.ShowChatMessage("[tl] next waypoint");
        }

        void AutoAdvanceIfReached()
        {
            if (!(TargetX.HasValue && TargetZ.HasValue) || !hasHudTarget) return;

            var pl = capi.World.Player.Entity.Pos;
            var plHud = AbsToHud3(pl.X, pl.Y, pl.Z);

            bool isLast = routePos >= routeHud.Count - 1;
            double d = isLast ? Hud2Dist(plHud, currentHudTarget)
                              : Hud3Dist(plHud, currentHudTarget);

            double threshold = isLast ? FinalReachHud : ReachDistBlocks;
            if (d > threshold) return;

            if (routePos < isTeleportEdge.Count && isTeleportEdge[routePos] == true)
            {
                AdvanceBy(2);
            }
            else
            {
                AdvanceBy(1);
            }
        }

        public void SetTarget(double? x, double? z) { TargetX = x; TargetZ = z; }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled || stage != EnumRenderStage.Ortho) return;
            if (capi?.Render == null || capi.World?.Player?.Entity == null) return;

            // route active?
            bool hasRoute = routePos < routeHud.Count && hasHudTarget && TargetX.HasValue && TargetZ.HasValue;

            // animate visibility (route OR idle)
            float target = (hasRoute || showCompassIdle) ? 1f : 0f;
            float k = 1f - (float)Math.Exp(-IdleAnimSpeed * Math.Clamp(dt, 0f, 0.1f));
            idleAnim01 = GameMath.Lerp(idleAnim01, target, k);

            if (idleAnim01 <= 0.01f) return; // fully hidden

            var pl = capi.World.Player;
            float w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;

            float ribbonW = cfg.RibbonWidthPx, ribbonH = cfg.RibbonHeightPx;
            float xCenter = w * 0.5f;

            float slideOffset = (1f - idleAnim01) * 12f;
            float yTop = h * cfg.RibbonTopPct - slideOffset;

            int bg = ColorUtil.ToRgba((int)(cfg.BgAlpha     * idleAnim01), 0, 0, 0);
            int border = ColorUtil.ToRgba((int)(cfg.BorderAlpha * idleAnim01), 255, 255, 255);
            int colMinor = ColorUtil.ToRgba((int)(cfg.TickAlpha  * idleAnim01), 230, 230, 230);
            int colMed   = ColorUtil.ToRgba((int)(cfg.TickAlpha  * idleAnim01), 250, 250, 250);
            int colMajor = ColorUtil.ToRgba((int)(cfg.TickAlpha  * idleAnim01), 255, 255, 255);
            int colCenter= ColorUtil.ToRgba((int)(cfg.CenterAlpha* idleAnim01), 220, 240, 255);
            int colOutline=ColorUtil.ToRgba((int)(255 * idleAnim01), 0, 0, 0);

            // Bg + borders
            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop, 0f, ribbonW, ribbonH, bg);
            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop, 0f, ribbonW, 1f, border);
            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop + ribbonH - 1f, 0f, ribbonW, 1f, border);

            // Heading
            double yawRad = pl.Entity.Pos.Yaw;
            double dirX = Math.Sin(yawRad), dirZ = Math.Cos(yawRad);
            double headingDeg = Normalize360(Math.Atan2(dirX, -dirZ) * GameMath.RAD2DEG);
            if (!accInit) { headingAcc = headingDeg; accInit = true; }
            double alpha = Math.Clamp(dt * 12.0, 0, 1);
            double delta = AngleDeltaDeg(headingDeg, Normalize360(headingAcc));
            headingAcc += delta * alpha; double headingDisp = Normalize360(headingAcc);

            const float visibleDeg = 120f; float pxPerDeg = ribbonW / visibleDeg;
            const int majorStep = 90, medStep = 45, minorStep = 15;

            int firstTick = (int)Math.Floor((headingDisp - visibleDeg / 2f) / minorStep) * minorStep;
            int ticksCount = (int)(visibleDeg / minorStep) + 4;
            for (int i = 0; i <= ticksCount; i++)
            {
                int a = firstTick + i * minorStep;
                double rel = AngleDeltaDeg(a, headingDisp);
                if (Math.Abs(rel) > visibleDeg / 2f) continue;
                float xx = xCenter + (float)(rel * pxPerDeg);
                float tickH; int col;
                if (Modulo(a, majorStep) == 0) { tickH = ribbonH * 0.40f; col = colMajor; }
                else if (Modulo(a, medStep) == 0) { tickH = ribbonH * 0.52f; col = colMed; }
                else { tickH = ribbonH * 0.30f; col = colMinor; }
                float yTick = yTop + ribbonH - tickH - 3f;
                DrawRectWithOutline(xx - 1f, yTick, 2f, tickH, col, colOutline, 1.5f);
            }

            DrawCardinal(0,   nTex, nTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(90,  eTex, eTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(180, sTex, sTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(270, wTex, wTexBlack, xCenter, yTop, headingDisp, pxPerDeg);

            DrawRectWithOutline(xCenter - 2.5f, yTop + 2f, 5f, ribbonH - 4f, colCenter, colOutline, 1.5f);

            // Target marker (only when route active)
            if (hasRoute && TargetX.HasValue && TargetZ.HasValue)
            {
                double vx = TargetX.Value - pl.Entity.Pos.X;
                double vz = TargetZ.Value - pl.Entity.Pos.Z;
                double tgtDeg = Normalize360(Math.Atan2(vx, -vz) * GameMath.RAD2DEG);
                double rel = AngleDeltaDeg(tgtDeg, headingDisp);
                if (Math.Abs(rel) <= visibleDeg / 2f)
                {
                    float tx = xCenter + (float)(rel * pxPerDeg);
                    int colRed = ColorUtil.ToRgba((int)(255 * idleAnim01), 220, 40, 40);
                    capi.Render.RenderRectangle(tx - 2f, yTop + 4f, 0f, 4f, ribbonH - 8f, colRed);
                }
            }

            // HUD overlay (only when route active)
            if (hasRoute && routePos < routeHud.Count)
            {
                var plHud = AbsToHud3(pl.Entity.Pos.X, pl.Entity.Pos.Y, pl.Entity.Pos.Z);
                var tgtHud = routeHud[routePos];

                bool isLast = routePos >= routeHud.Count - 1;
                double dist = isLast ? Hud2Dist(plHud, tgtHud) : Hud3Dist(plHud, tgtHud);

                string topLine = $"TL {PassedTeleports}/{TotalTeleports}, dist={dist:0}";
                RenderTextCenteredWithOutline(topLine, xCenter, yTop - 22, ref hudTopWhite, ref hudTopBlack);

                string bottomLine = $"Target: {tgtHud.X:0} {tgtHud.Y:0} {tgtHud.Z:0}";
                RenderTextCenteredWithOutline(bottomLine, xCenter, yTop + cfg.RibbonHeightPx + 6, ref hudBotWhite, ref hudBotBlack);
            }

            AutoAdvanceIfReached();
        }

        // text with outline
        void RenderTextCenteredWithOutline(string text, float xCenter, float y, ref LoadedTexture whiteTex, ref LoadedTexture blackTex)
        {
            ttu.GenOrUpdateTextTexture(text, fontHudBlack, ref blackTex);
            ttu.GenOrUpdateTextTexture(text, fontHud,      ref whiteTex);

            float tw = whiteTex.Width, th = whiteTex.Height;
            float bx = xCenter - tw / 2f, by = y;
            float o = 1.5f;

            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx - o, by,     tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx + o, by,     tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx,     by - o, tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx,     by + o, tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx - o, by - o, tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx + o, by - o, tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx - o, by + o, tw, th, 20);
            capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, bx + o, by + o, tw, th, 20);

            capi.Render.Render2DTexturePremultipliedAlpha(whiteTex.TextureId, bx, by, tw, th, 21);
        }

        // utils
        static int CountTrue(List<bool> arr, int from = 0, int toExclusive = int.MaxValue)
        {
            int end = Math.Min(arr.Count, toExclusive);
            int c = 0; for (int i = from; i < end; i++) if (arr[i]) c++; return c;
        }
        static double Hud2Dist(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z; return Math.Sqrt(dx * dx + dz * dz);
        }
        static double Hud3Dist(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        Vec3d AbsToHud3(double absX, double absY, double absZ)
        {
            var sp = capi.World.DefaultSpawnPosition;
            return new Vec3d(absX - sp.X, absY, absZ - sp.Z);
        }

        void DrawCardinal(int angleDeg, LoadedTexture mainTex, LoadedTexture blackTex,
                          float xCenter, float yTop, double headingDeg, float pxPerDeg)
        {
            if (mainTex == null || mainTex.TextureId == 0) return;
            double rel = AngleDeltaDeg(angleDeg, headingDeg);
            if (Math.Abs(rel) > 60) return;

            float x = xCenter + (float)(rel * pxPerDeg);
            float tw = mainTex.Width, th = mainTex.Height;
            float ty = yTop + 4f;

            if (blackTex != null && blackTex.TextureId != 0)
            {
                float o = 1.5f;
                capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, x - tw / 2f - o, ty, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, x - tw / 2f + o, ty, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, x - tw / 2f, ty - o, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(blackTex.TextureId, x - tw / 2f, ty + o, tw, th, 10f);
            }

            capi.Render.Render2DTexturePremultipliedAlpha(mainTex.TextureId, x - tw / 2f, ty, tw, th, 11f);
        }

        void DrawRectWithOutline(float x, float y, float w, float h, int color, int outline, float o)
        {
            capi.Render.RenderRectangle(x - o, y - o, 0f, w + 2 * o, h + 2 * o, outline);
            capi.Render.RenderRectangle(x, y, 0f, w, h, color);
        }

        static double Normalize360(double a) { a %= 360.0; if (a < 0) a += 360.0; return a; }
        static int Modulo(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }
        static double AngleDeltaDeg(double a, double b) { double d = (a - b + 540.0) % 360.0 - 180.0; return d; }

        public double RenderOrder => 0.9;
        public int RenderRange => 9999;

        public void Dispose()
        {
            nTex?.Dispose(); eTex?.Dispose(); sTex?.Dispose(); wTex?.Dispose();
            nTexBlack?.Dispose(); eTexBlack?.Dispose(); sTexBlack?.Dispose(); wTexBlack?.Dispose();
            hudTopWhite?.Dispose(); hudTopBlack?.Dispose(); hudBotWhite?.Dispose(); hudBotBlack?.Dispose();
        }
    }
}
