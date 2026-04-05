#nullable disable
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace NavMod
{
    /// <summary>
    /// Ribbon-compass + target marker + 3D waypoint queue.
    ///
    /// Fixes applied:
    ///  #3  RoutePos is now a public property so TlPathService can read/sync it.
    ///      A RouteCompleted event lets the service know when the renderer auto-advances to the end.
    ///  #6  AbsToHud3 removed; NavUtils.AbsToHud3 used instead.
    ///  #7  fontRed built via a fresh CairoFont instance so .Color assignment sticks.
    ///  #11 RenderTextCenteredWithOutline caches last rendered string; GenOrUpdateTextTexture
    ///      is only called when the text actually changes.
    /// </summary>
    public class CompassRibbonRenderer : IRenderer
    {
        readonly ICoreClientAPI capi;
        public bool Enabled = true;

        // ── Idle compass ────────────────────────────────────────────────────
        bool showCompassIdle;
        float idleAnim01;
        const float IdleAnimSpeed = 10f;

        public void SetIdleVisible(bool on) => showCompassIdle = on;

        // ── Route state ─────────────────────────────────────────────────────
        readonly List<Vec3d> routeAbs         = new();
        readonly List<Vec3d> routeHud         = new();
        readonly List<bool>  isTeleportEdge   = new();

        // FIX #3: public read-only property so TlPathService can sync
        public int RoutePos { get; private set; }
        public int RouteCount => routeHud.Count;

        public double? TargetX;
        public double? TargetZ;
        bool hasHudTarget;
        Vec3d currentHudTarget;

        // FIX #3: event fired when renderer auto-advances past the last point
        public event Action RouteCompleted;
        // FIX #3: event fired on each auto-advance (step index = new RoutePos)
        public event Action<int> RouteAdvanced;

        // ── Thresholds ──────────────────────────────────────────────────────
        public double ReachDistBlocks = 1;
        public double FinalReachHud   = 10;

        // ── TL progress (read from RoutePos, not a separate counter) ────────
        int TotalTeleports  => CountTrue(isTeleportEdge);
        int PassedTeleports => CountTrue(isTeleportEdge, 0, Math.Min(RoutePos, isTeleportEdge.Count));

        // ── Fonts ───────────────────────────────────────────────────────────
        readonly TextTextureUtil ttu;

        // FIX #7: build fontRed as a completely separate instance so .Color sticks
        readonly CairoFont fontWhite;
        readonly CairoFont fontRed;
        readonly CairoFont fontHud;
        readonly CairoFont fontHudBlack;

        // ── Cardinal textures (created once in ctor) ────────────────────────
        LoadedTexture nTex, eTex, sTex, wTex;
        LoadedTexture nTexBlack, eTexBlack, sTexBlack, wTexBlack;

        // ── HUD overlay textures ────────────────────────────────────────────
        LoadedTexture hudTopWhite, hudTopBlack;
        LoadedTexture hudBotWhite, hudBotBlack;

        // FIX #11: cache last rendered strings to avoid regenerating every frame
        string lastTopLine = null;
        string lastBotLine = null;

        // ── Heading smoothing ───────────────────────────────────────────────
        double headingAcc;
        bool   accInit;

        // ── Layout constants ────────────────────────────────────────────────
        struct HudCfg
        {
            public float RibbonWidthPx, RibbonHeightPx, RibbonTopPct;
            public int   BgAlpha, BorderAlpha, TickAlpha, CenterAlpha;
        }
        static readonly HudCfg cfg = new HudCfg
        {
            RibbonWidthPx  = 400f,
            RibbonHeightPx = 40f,
            RibbonTopPct   = 0.10f,
            BgAlpha        = 90,
            BorderAlpha    = 140,
            TickAlpha      = 210,
            CenterAlpha    = 200
        };

        // ────────────────────────────────────────────────────────────────────
        public CompassRibbonRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            ttu = new TextTextureUtil(capi);

            // FIX #7: create each font as an independent object
            fontWhite    = CairoFont.WhiteSmallText().WithFontSize(20);

            fontRed      = CairoFont.WhiteSmallText().WithFontSize(20);
            fontRed.Color = new double[] { 1.0, 0.2, 0.2, 1.0 };   // red N

            fontHud      = CairoFont.WhiteSmallText().WithFontSize(18);

            fontHudBlack = CairoFont.WhiteSmallText().WithFontSize(18);
            fontHudBlack.Color = new double[] { 0.0, 0.0, 0.0, 1.0 };

            var fontBlack20      = CairoFont.WhiteSmallText().WithFontSize(20);
            fontBlack20.Color    = new double[] { 0.0, 0.0, 0.0, 1.0 };

            nTex      = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("N", fontRed,      ref nTex);
            eTex      = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("E", fontWhite,    ref eTex);
            sTex      = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("S", fontWhite,    ref sTex);
            wTex      = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("W", fontWhite,    ref wTex);

            nTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("N", fontBlack20,  ref nTexBlack);
            eTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("E", fontBlack20,  ref eTexBlack);
            sTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("S", fontBlack20,  ref sTexBlack);
            wTexBlack = new LoadedTexture(capi); ttu.GenOrUpdateTextTexture("W", fontBlack20,  ref wTexBlack);

            hudTopWhite = new LoadedTexture(capi);
            hudTopBlack = new LoadedTexture(capi);
            hudBotWhite = new LoadedTexture(capi);
            hudBotBlack = new LoadedTexture(capi);
        }

        // ── Route setup ─────────────────────────────────────────────────────

        public void SetRoute3(List<Vec3d> absPoints, List<bool> tlEdgeFlags)
        {
            routeAbs.Clear(); routeHud.Clear(); isTeleportEdge.Clear();
            RoutePos = 0; hasHudTarget = false; TargetX = TargetZ = null;
            lastTopLine = null; lastBotLine = null; // FIX #11: invalidate cache

            if (absPoints != null) routeAbs.AddRange(absPoints);

            // FIX #6: use NavUtils
            foreach (var p in routeAbs)
                routeHud.Add(NavUtils.AbsToHud3(capi, p.X, p.Y, p.Z));

            if (tlEdgeFlags != null && tlEdgeFlags.Count == Math.Max(0, routeAbs.Count - 1))
                isTeleportEdge.AddRange(tlEdgeFlags);
            else
                for (int i = 0; i + 1 < routeHud.Count; i++)
                    isTeleportEdge.Add(NavUtils.Hud2Dist(routeHud[i], routeHud[i + 1]) <= 1.5);

            // Skip points we're already standing on
            if (routeHud.Count > 0)
            {
                var epos  = capi.World.Player.Entity.SidedPos;
                var plHud = NavUtils.AbsToHud3(capi, epos.X, epos.Y, epos.Z);
                while (RoutePos < routeHud.Count && NavUtils.Hud3Dist(plHud, routeHud[RoutePos]) <= ReachDistBlocks)
                    RoutePos++;
            }

            if (RoutePos < routeAbs.Count)
            {
                TargetX          = routeAbs[RoutePos].X;
                TargetZ          = routeAbs[RoutePos].Z;
                currentHudTarget = routeHud[RoutePos];
                hasHudTarget     = true;
            }
        }

        // FIX #3: allow service to forcibly sync RoutePos (e.g. after TL detection)
        public void SyncRoutePos(int pos)
        {
            if (pos < 0 || pos >= routeAbs.Count) return;
            RoutePos         = pos;
            TargetX          = routeAbs[RoutePos].X;
            TargetZ          = routeAbs[RoutePos].Z;
            currentHudTarget = routeHud[RoutePos];
            hasHudTarget     = true;
            lastTopLine      = null; // FIX #11: force text refresh
            lastBotLine      = null;
        }

        public void ClearRoute()
        {
            routeAbs.Clear(); routeHud.Clear(); isTeleportEdge.Clear();
            RoutePos = 0; hasHudTarget = false; TargetX = TargetZ = null;
            lastTopLine = null; lastBotLine = null;
        }

        // Keep backward-compat names used in TlPathService
        public void ClearTargetsQueue() => ClearRoute();
        public void SetTarget(double? x, double? z) { TargetX = x; TargetZ = z; }

        // ── Planned TL helpers (used by TlPathService tick) ─────────────────

        public bool TryGetPlannedTeleportPair(out Vec3d entryHud, out Vec3d exitHud)
        {
            entryHud = null; exitHud = null;
            if (RoutePos < routeHud.Count &&
                RoutePos < isTeleportEdge.Count && isTeleportEdge[RoutePos] &&
                RoutePos + 1 < routeHud.Count)
            {
                entryHud = routeHud[RoutePos];
                exitHud  = routeHud[RoutePos + 1];
                return true;
            }
            return false;
        }

        public bool AdvancePastTeleportPair()
        {
            if (RoutePos < isTeleportEdge.Count && isTeleportEdge[RoutePos] && RoutePos + 1 < routeAbs.Count)
            {
                AdvanceBy(2);
                return true;
            }
            return false;
        }

        // Backward compat
        public void SetTargetsQueue(IEnumerable<Vec2d> points)
        {
            var list = new List<Vec3d>();
            double y = capi.World?.Player?.Entity?.Pos?.Y ?? 0;
            if (points != null) foreach (var p in points) list.Add(new Vec3d(p.X, y, p.Y));
            SetRoute3(list, null);
        }

        // ── Internal advance ────────────────────────────────────────────────

        void AdvanceBy(int steps)
        {
            RoutePos += steps;
            lastTopLine = null; lastBotLine = null; // FIX #11

            if (RoutePos >= routeAbs.Count)
            {
                TargetX = TargetZ = null;
                hasHudTarget = false;
                capi.ShowChatMessage("[tl] Route completed!");
                RouteCompleted?.Invoke();           // FIX #3: notify service
                return;
            }

            TargetX          = routeAbs[RoutePos].X;
            TargetZ          = routeAbs[RoutePos].Z;
            currentHudTarget = routeHud[RoutePos];
            hasHudTarget     = true;
            RouteAdvanced?.Invoke(RoutePos);        // FIX #3: notify service
        }

        void AutoAdvanceIfReached()
        {
            if (!hasHudTarget || !TargetX.HasValue || !TargetZ.HasValue) return;

            var epos  = capi.World.Player.Entity.SidedPos;
            var plHud = NavUtils.AbsToHud3(capi, epos.X, epos.Y, epos.Z);

            bool   isLast    = RoutePos >= routeHud.Count - 1;
            double d         = isLast ? NavUtils.Hud2Dist(plHud, currentHudTarget)
                                      : NavUtils.Hud3Dist(plHud, currentHudTarget);
            double threshold = isLast ? FinalReachHud : ReachDistBlocks;
            if (d > threshold) return;

            // Skip TL exit automatically
            if (RoutePos < isTeleportEdge.Count && isTeleportEdge[RoutePos])
                AdvanceBy(2);
            else
                AdvanceBy(1);
        }

        // ── Render ──────────────────────────────────────────────────────────

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Enabled || stage != EnumRenderStage.Ortho) return;
            if (capi?.Render == null || capi.World?.Player?.Entity == null) return;

            bool hasRoute = RoutePos < routeHud.Count && hasHudTarget && TargetX.HasValue && TargetZ.HasValue;

            float target = (hasRoute || showCompassIdle) ? 1f : 0f;
            float k = 1f - (float)Math.Exp(-IdleAnimSpeed * Math.Clamp(dt, 0f, 0.1f));
            idleAnim01 = GameMath.Lerp(idleAnim01, target, k);
            if (idleAnim01 <= 0.01f) return;

            var   pl      = capi.World.Player;
            float w       = capi.Render.FrameWidth;
            float h       = capi.Render.FrameHeight;
            float ribbonW = cfg.RibbonWidthPx;
            float ribbonH = cfg.RibbonHeightPx;
            float xCenter = w * 0.5f;

            float slideOffset = (1f - idleAnim01) * 12f;
            float yTop        = h * cfg.RibbonTopPct - slideOffset;

            int bg       = ColorUtil.ToRgba((int)(cfg.BgAlpha      * idleAnim01), 0,   0,   0);
            int border   = ColorUtil.ToRgba((int)(cfg.BorderAlpha  * idleAnim01), 255, 255, 255);
            int colMinor = ColorUtil.ToRgba((int)(cfg.TickAlpha    * idleAnim01), 230, 230, 230);
            int colMed   = ColorUtil.ToRgba((int)(cfg.TickAlpha    * idleAnim01), 250, 250, 250);
            int colMajor = ColorUtil.ToRgba((int)(cfg.TickAlpha    * idleAnim01), 255, 255, 255);
            int colCenter= ColorUtil.ToRgba((int)(cfg.CenterAlpha  * idleAnim01), 220, 240, 255);
            int colOutline= ColorUtil.ToRgba((int)(255             * idleAnim01), 0,   0,   0);

            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop, 0f, ribbonW, ribbonH, bg);
            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop, 0f, ribbonW, 1f, border);
            capi.Render.RenderRectangle(xCenter - ribbonW / 2f, yTop + ribbonH - 1f, 0f, ribbonW, 1f, border);

            double yawRad    = pl.Entity.SidedPos.Yaw;
            double headingDeg = Normalize360(Math.Atan2(Math.Sin(yawRad), -Math.Cos(yawRad)) * GameMath.RAD2DEG);
            if (!accInit) { headingAcc = headingDeg; accInit = true; }
            double alpha_  = Math.Clamp(dt * 12.0, 0, 1);
            headingAcc    += AngleDeltaDeg(headingDeg, Normalize360(headingAcc)) * alpha_;
            double headingDisp = Normalize360(headingAcc);

            const float visibleDeg = 120f;
            float pxPerDeg = ribbonW / visibleDeg;
            const int majorStep = 90, medStep = 45, minorStep = 15;

            int firstTick  = (int)Math.Floor((headingDisp - visibleDeg / 2f) / minorStep) * minorStep;
            int ticksCount = (int)(visibleDeg / minorStep) + 4;

            for (int i = 0; i <= ticksCount; i++)
            {
                int a = firstTick + i * minorStep;
                double rel = AngleDeltaDeg(a, headingDisp);
                if (Math.Abs(rel) > visibleDeg / 2f) continue;
                float xx = xCenter + (float)(rel * pxPerDeg);
                float tickH; int col;
                if      (Modulo(a, majorStep) == 0) { tickH = ribbonH * 0.40f; col = colMajor; }
                else if (Modulo(a, medStep)   == 0) { tickH = ribbonH * 0.52f; col = colMed; }
                else                                { tickH = ribbonH * 0.30f; col = colMinor; }
                DrawRectWithOutline(xx - 1f, yTop + ribbonH - tickH - 3f, 2f, tickH, col, colOutline, 1.5f);
            }

            DrawCardinal(0,   nTex, nTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(90,  eTex, eTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(180, sTex, sTexBlack, xCenter, yTop, headingDisp, pxPerDeg);
            DrawCardinal(270, wTex, wTexBlack, xCenter, yTop, headingDisp, pxPerDeg);

            DrawRectWithOutline(xCenter - 2.5f, yTop + 2f, 5f, ribbonH - 4f, colCenter, colOutline, 1.5f);

            // Target marker
            if (hasRoute && TargetX.HasValue && TargetZ.HasValue)
            {
                double vx = TargetX.Value - pl.Entity.SidedPos.X;
                double vz = TargetZ.Value - pl.Entity.SidedPos.Z;
                double tgtDeg = Normalize360(Math.Atan2(vx, -vz) * GameMath.RAD2DEG);
                double rel = AngleDeltaDeg(tgtDeg, headingDisp);
                if (Math.Abs(rel) <= visibleDeg / 2f)
                {
                    float tx = xCenter + (float)(rel * pxPerDeg);
                    capi.Render.RenderRectangle(tx - 2f, yTop + 4f, 0f, 4f, ribbonH - 8f,
                        ColorUtil.ToRgba((int)(255 * idleAnim01), 220, 40, 40));
                }
            }

            // HUD overlay text
            if (hasRoute && RoutePos < routeHud.Count)
            {
                var plHud  = NavUtils.AbsToHud3(capi, pl.Entity.SidedPos.X, pl.Entity.SidedPos.Y, pl.Entity.SidedPos.Z);
                var tgtHud = routeHud[RoutePos];
                bool isLast = RoutePos >= routeHud.Count - 1;
                double dist = isLast ? NavUtils.Hud2Dist(plHud, tgtHud) : NavUtils.Hud3Dist(plHud, tgtHud);

                // FIX #11: only regenerate texture when text changes
                string topLine = $"TL {PassedTeleports}/{TotalTeleports},  dist={dist:0}";
                if (topLine != lastTopLine)
                {
                    lastTopLine = topLine;
                    ttu.GenOrUpdateTextTexture(topLine, fontHudBlack, ref hudTopBlack);
                    ttu.GenOrUpdateTextTexture(topLine, fontHud,      ref hudTopWhite);
                }

                string botLine = $"Target: {tgtHud.X:0}  {tgtHud.Y:0}  {tgtHud.Z:0}";
                if (botLine != lastBotLine)
                {
                    lastBotLine = botLine;
                    ttu.GenOrUpdateTextTexture(botLine, fontHudBlack, ref hudBotBlack);
                    ttu.GenOrUpdateTextTexture(botLine, fontHud,      ref hudBotWhite);
                }

                RenderTextWithOutline(hudTopWhite, hudTopBlack, xCenter, yTop - 22);
                RenderTextWithOutline(hudBotWhite, hudBotBlack, xCenter, yTop + cfg.RibbonHeightPx + 6);
            }

            AutoAdvanceIfReached();
        }

        // ── Render helpers ───────────────────────────────────────────────────

        // FIX #11: textures are passed in pre-built; no generation here
        void RenderTextWithOutline(LoadedTexture white, LoadedTexture black, float xCenter, float y)
        {
            if (white == null || white.TextureId == 0) return;
            float tw = white.Width, th = white.Height;
            float bx = xCenter - tw / 2f;
            const float o = 1.5f;

            if (black != null && black.TextureId != 0)
            {
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx - o, y,     tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx + o, y,     tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx, y - o,     tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx, y + o,     tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx - o, y - o, tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx + o, y - o, tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx - o, y + o, tw, th, 20);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, bx + o, y + o, tw, th, 20);
            }
            capi.Render.Render2DTexturePremultipliedAlpha(white.TextureId, bx, y, tw, th, 21);
        }

        void DrawCardinal(int angleDeg, LoadedTexture main, LoadedTexture black,
                          float xCenter, float yTop, double headingDeg, float pxPerDeg)
        {
            if (main == null || main.TextureId == 0) return;
            double rel = AngleDeltaDeg(angleDeg, headingDeg);
            if (Math.Abs(rel) > 60) return;

            float x  = xCenter + (float)(rel * pxPerDeg);
            float tw = main.Width, th = main.Height;
            float ty = yTop + 4f;
            const float o = 1.5f;

            if (black != null && black.TextureId != 0)
            {
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, x - tw / 2f - o, ty, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, x - tw / 2f + o, ty, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, x - tw / 2f, ty - o, tw, th, 10f);
                capi.Render.Render2DTexturePremultipliedAlpha(black.TextureId, x - tw / 2f, ty + o, tw, th, 10f);
            }
            capi.Render.Render2DTexturePremultipliedAlpha(main.TextureId, x - tw / 2f, ty, tw, th, 11f);
        }

        void DrawRectWithOutline(float x, float y, float w, float h, int color, int outline, float o)
        {
            capi.Render.RenderRectangle(x - o, y - o, 0f, w + 2 * o, h + 2 * o, outline);
            capi.Render.RenderRectangle(x, y, 0f, w, h, color);
        }

        // ── Math utils ───────────────────────────────────────────────────────

        static int CountTrue(List<bool> arr, int from = 0, int toExclusive = int.MaxValue)
        {
            int end = Math.Min(arr.Count, toExclusive), c = 0;
            for (int i = from; i < end; i++) if (arr[i]) c++;
            return c;
        }

        static double Normalize360(double a) { a %= 360.0; if (a < 0) a += 360.0; return a; }
        static int    Modulo(int a, int n)   { int r = a % n; return r < 0 ? r + n : r; }
        static double AngleDeltaDeg(double a, double b) => (a - b + 540.0) % 360.0 - 180.0;

        public double RenderOrder => 0.9;
        public int    RenderRange => 9999;

        public void Dispose()
        {
            nTex?.Dispose(); eTex?.Dispose(); sTex?.Dispose(); wTex?.Dispose();
            nTexBlack?.Dispose(); eTexBlack?.Dispose(); sTexBlack?.Dispose(); wTexBlack?.Dispose();
            hudTopWhite?.Dispose(); hudTopBlack?.Dispose();
            hudBotWhite?.Dispose(); hudBotBlack?.Dispose();
        }
    }
}