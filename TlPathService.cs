#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace NavMod
{
    public enum GeoSource { Url, Waypointer }
    /// <summary>
    /// Routing service for TLPath.
    /// Fixes: #1 async HTTP, #2 Dijkstra O(E log V), #3 single RoutePos in renderer,
    ///        #4 grid built once, #5 graph cache, #6 NavUtils, #8 CleanPath, #9 double coords.
    /// </summary>
    public class TlPathService
    {
        readonly ICoreClientAPI        capi;
        readonly string                dataDir;
        readonly string                cfgPath;
        readonly string                geoPath;
        readonly HttpClient            http    = new HttpClient();
        readonly CompassRibbonRenderer compass;

        // ── Cached base graph (volatile: written from background HTTP thread) ─
        volatile Graph cachedBaseGraph   = null;
        DateTime       cachedBaseGraphAt = DateTime.MinValue;
        DateTime       lastHttpFailAt    = DateTime.MinValue;

        // ── Navigation state ─────────────────────────────────────────────────
        Vec3d currentGoalHud = null;

        // FIX #3: prevPos kept here; RoutePos lives only in compass
        Vec3d     prevPosHud      = null;
        DateTime  lastRecalcAt   = DateTime.MinValue;
        DateTime  lastLegitTlAt  = DateTime.MinValue;
        DateTime? pendingRecalcAt = null;
        int       activeRoutePos  = 0; // local mirror of compass.RoutePos

        // Touch-based TL pair detection: catches short TL pairs that don't trigger jump-detect
        int       lastTouchedRouteIdx = -1;
        DateTime  lastTouchedAt       = DateTime.MinValue;
        static readonly TimeSpan TouchPairWindow = TimeSpan.FromSeconds(30);

        // GPS-style: schedule a periodic recalc to refresh the route from the player's current position
        DateTime  nextActiveRecalcAt   = DateTime.MinValue;
        static readonly TimeSpan ActiveRecalcInterval = TimeSpan.FromSeconds(7);

        // Suppress route-built / ETA chats for the next BuildRoute call. Used by automatic recalcs
        // (GPS recalc, plannedBackward) so the chat doesn't spam every 7s as the player walks.
        bool suppressBuildChat = false;

        List<Vec3d> activeRouteHud = null;
        List<bool>  activeTlFlags  = null;
        int         routeTotalTp   = 0;   // Ratcheted total TL count for this destination (never decreases mid-route)
        int         passedTlCount  = 0;   // Actual TL crossings made on this destination (forward++, backward--)

        // History of TL crossings as (from_idx, to_idx) base-graph node pairs.
        // A new crossing whose reverse already exists in this stack = backward (decrement + remove).
        // Without this, forward+back through the same TL would count as 2 instead of 0.
        readonly List<(int from, int to)> tlCrossingStack = new();

        // ── Constants ────────────────────────────────────────────────────────
        const double TLJumpThreshHUD   = 200.0;
        const double TLNearRadiusHUD   = 150.0;  // soft-advance / corridor bypass
        const double TLArrivalRadiusHUD = 20.0;   // must land within ~20 blocks of destination TL
        const double CorridorRadiusHUD = 250.0;
        static readonly TimeSpan RecalcCooldown     = TimeSpan.FromSeconds(3);
        static readonly TimeSpan TlGrace            = TimeSpan.FromSeconds(6);
        const  double            PendingRecalcDelay = 2.0;

        // ════════════════════════════════════════════════════════════════════
        public TlPathService(ICoreClientAPI capi, string dataDir, CompassRibbonRenderer compass)
        {
            this.capi    = capi;
            this.dataDir = dataDir;
            this.compass = compass;

            Directory.CreateDirectory(dataDir);
            cfgPath = Path.Combine(dataDir, "config.json");
            geoPath = Path.Combine(dataDir, "translocators.geojson");

            LoadCfg();
            L.Lang = cfg.Language == "Russian" ? GuiLang.Russian : GuiLang.English;
            LoadPersisted();
            ApplyIdleFlagToCompass();

            // FIX #3: sync activeRoutePos from renderer events
            compass.RouteAdvanced += pos => activeRoutePos = pos;
            compass.RouteCompleted += OnRouteCompleted;

            capi.Event.RegisterGameTickListener(OnClientTick, 50);
        }

        void OnRouteCompleted()
        {
            currentGoalHud  = null;
            activeRouteHud  = null;
            activeTlFlags   = null;
            activeRoutePos  = 0;
            routeTotalTp    = 0;
            passedTlCount   = 0;
            prevPosHud      = null;
            pendingRecalcAt = null;
            lastTouchedRouteIdx = -1;
            nextActiveRecalcAt  = DateTime.MinValue;
            tlCrossingStack.Clear();
            excludedTlPositions.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // Config
        // ════════════════════════════════════════════════════════════════════
        class Cfg
        {
            public string    RemoteUrl       { get; set; } = "https://map.tops.vintagestory.at/data/geojson/translocators.geojson";
            public int       TtlHours        { get; set; } = 24;
            public double    ScaleX          { get; set; } = 1;
            public double    ScaleY          { get; set; } = 1;
            public double    OffsetX         { get; set; } = 0;
            public double    OffsetY         { get; set; } = 0;
            public bool      SwapAxes        { get; set; } = false;
            public bool      FlipX           { get; set; } = false;
            public bool      FlipY           { get; set; } = false;
            public double    WalkRadiusHud   { get; set; } = 3000;
            public double    FinishDirectHud { get; set; } = 150;
            public bool      ShowCompassIdle { get; set; } = false;
            public GeoSource Source          { get; set; } = GeoSource.Url;
            public string    Language        { get; set; } = "English";
        }
        Cfg cfg = new Cfg();

        public double WalkRadius      => cfg.WalkRadiusHud;
        public bool   IsIdleCompassOn => cfg.ShowCompassIdle;

        public void SetWalkRadius(double r) { cfg.WalkRadiusHud = GameMath.Clamp(r, 1, 50000); SaveCfg(); }

        public void SetRemoteUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) { capi.ShowChatMessage("[tl] Invalid URL."); return; }
            cfg.RemoteUrl = url.Trim(); cachedBaseGraph = null; SaveCfg();
            capi.ShowChatMessage("[tl] GeoJSON URL set to: " + cfg.RemoteUrl);
        }

        public string GetRemoteUrl() => cfg.RemoteUrl;

        public void SetIdleCompass(bool on) { cfg.ShowCompassIdle = on; SaveCfg(); ApplyIdleFlagToCompass(); }

        public void SetLanguage(GuiLang lang)
        {
            L.Lang = lang;
            cfg.Language = lang == GuiLang.Russian ? "Russian" : "English";
            SaveCfg();
        }

        public GeoSource GetSource() => cfg.Source;

        public void SetSource(GeoSource src)
        {
            if (cfg.Source == src) return;
            cfg.Source = src;
            cachedBaseGraph = null;
            SaveCfg();
            capi.ShowChatMessage(src == GeoSource.Waypointer
                ? "[tl] Source: Waypointer"
                : "[tl] Source: URL");
        }

        // Byte-for-byte copy of GeoJsonStore.SanitizeKey from Waypointer.
        // If Waypointer changes its sanitizer, update this too.
        static string SanitizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "unknown";
            var sb = new StringBuilder();
            foreach (char c in key)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        public string GetWorldKey() => SanitizeKey(capi.World?.SavegameIdentifier ?? "default");

        public string GetWaypointerGeoPath()
            => Path.Combine(GamePaths.DataPath, "ModData", "waypointer",
                            "world-" + GetWorldKey(), "translocators.geojson");

        string CurrentGeoPath()
            => cfg.Source == GeoSource.Waypointer ? GetWaypointerGeoPath() : geoPath;

        public bool WaypointerFileExists() => File.Exists(GetWaypointerGeoPath());
        public bool IsWaypointerInstalled() => capi.ModLoader.GetMod("waypointer") != null;

        public void CmdShow(string arg = null)
        {
            cfg.ShowCompassIdle = ParseBoolLoose(arg) ?? !cfg.ShowCompassIdle;
            SaveCfg(); ApplyIdleFlagToCompass();
            capi.ShowChatMessage(cfg.ShowCompassIdle ? "[tl] Compass idle: ON" : "[tl] Compass idle: OFF");
        }

        static bool? ParseBoolLoose(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            switch (s.Trim().ToLowerInvariant())
            {
                case "1": case "on": case "true":  case "enable":  return true;
                case "0": case "off": case "false": case "disable": return false;
                default: return null;
            }
        }

        void ApplyIdleFlagToCompass() { try { compass?.SetIdleVisible(cfg.ShowCompassIdle); } catch { } }
        void LoadCfg()
        {
            try { if (File.Exists(cfgPath)) cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(cfgPath, Encoding.UTF8)) ?? new Cfg(); }
            catch { cfg = new Cfg(); }
        }
        void SaveCfg() => File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        // ════════════════════════════════════════════════════════════════════
        // Favourites & History
        // ════════════════════════════════════════════════════════════════════
        string FavsFilePath => Path.Combine(dataDir, "favourites.json");
        string HistFilePath => Path.Combine(dataDir, "history.json");

        List<FavouriteEntry> favourites = new List<FavouriteEntry>();
        List<HistoryEntry>   history    = new List<HistoryEntry>();
        const int MaxHistory = 10;

        void LoadPersisted()
        {
            try { if (File.Exists(FavsFilePath)) favourites = JsonSerializer.Deserialize<List<FavouriteEntry>>(File.ReadAllText(FavsFilePath, Encoding.UTF8)) ?? new List<FavouriteEntry>(); }
            catch { favourites = new List<FavouriteEntry>(); }
            try { if (File.Exists(HistFilePath)) history = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistFilePath, Encoding.UTF8)) ?? new List<HistoryEntry>(); }
            catch { history = new List<HistoryEntry>(); }
        }
        void SaveFavs()    => File.WriteAllText(FavsFilePath, JsonSerializer.Serialize(favourites, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        void SaveHistory() => File.WriteAllText(HistFilePath, JsonSerializer.Serialize(history,    new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        public List<FavouriteEntry> GetFavourites() => new List<FavouriteEntry>(favourites);
        public List<HistoryEntry>   GetHistory()    => new List<HistoryEntry>(history);

        public void SaveFavourite(string name)
        {
            if (currentGoalHud == null) { capi.ShowChatMessage("[tl] No active route to save."); return; }
            SaveFavouriteEntry((int)currentGoalHud.X, (int)currentGoalHud.Z, name);
        }
        public void SaveFavouriteEntry(int x, int z, string name)
        {
            if (favourites.Any(f => f.DestX == x && f.DestZ == z)) { capi.ShowChatMessage("[tl] Already in favourites."); return; }
            favourites.Add(new FavouriteEntry { Name = name, DestX = x, DestZ = z }); SaveFavs();
        }
        public void DeleteFavourite(int i) { if (i >= 0 && i < favourites.Count) { favourites.RemoveAt(i); SaveFavs(); } }
        public void MoveFavourite(int i, int d)
        {
            int j = i + d;
            if (j < 0 || j >= favourites.Count) return;
            (favourites[i], favourites[j]) = (favourites[j], favourites[i]); SaveFavs();
        }
        public void AddToHistory(int x, int z, string name = "")
        {
            history.RemoveAll(h => h.DestX == x && h.DestZ == z);
            history.Insert(0, new HistoryEntry { DestX = x, DestZ = z, Name = name });
            if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
            SaveHistory();
        }
        public void ClearHistory() { history.Clear(); SaveHistory(); }

        // ════════════════════════════════════════════════════════════════════
        // Waypoint exclusion
        // Stores the HUD-space position of TL entry nodes to exclude.
        // During BuildRoute, we find the nearest base-graph node to each
        // excluded position and mark it forbidden in Dijkstra.
        // ════════════════════════════════════════════════════════════════════
        readonly List<Vec3d> excludedTlPositions = new List<Vec3d>();

        public void ExcludeWaypoint(int routeIndex)
        {
            if (activeRouteHud == null || routeIndex < 0 || routeIndex >= activeRouteHud.Count) return;
            var pos = activeRouteHud[routeIndex];
            // Avoid duplicates
            if (!excludedTlPositions.Any(p => NavUtils.Hud2Dist(p, pos) < 5.0))
                excludedTlPositions.Add(pos);
            // Also exclude the exit node (routeIndex+1) for the TL pair
            if (routeIndex + 1 < activeRouteHud.Count)
            {
                var posExit = activeRouteHud[routeIndex + 1];
                if (!excludedTlPositions.Any(p => NavUtils.Hud2Dist(p, posExit) < 5.0))
                    excludedTlPositions.Add(posExit);
            }
        }

        public void RestoreWaypoint(int routeIndex)
        {
            if (activeRouteHud == null || routeIndex < 0 || routeIndex >= activeRouteHud.Count) return;
            var pos = activeRouteHud[routeIndex];
            excludedTlPositions.RemoveAll(p => NavUtils.Hud2Dist(p, pos) < 5.0);
            if (routeIndex + 1 < activeRouteHud.Count)
            {
                var posExit = activeRouteHud[routeIndex + 1];
                excludedTlPositions.RemoveAll(p => NavUtils.Hud2Dist(p, posExit) < 5.0);
            }
        }

        public void ResetExcludes() => excludedTlPositions.Clear();

        public void RecalculateWithExcludes()
        {
            if (currentGoalHud == null) return;
            FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z);
        }

        public List<RoutePointInfo> GetRoutePointInfos()
        {
            var res = new List<RoutePointInfo>();
            if (activeRouteHud == null) return res;
            int cur = compass.RoutePos;
            for (int i = 0; i < activeRouteHud.Count; i++)
            {
                var pos = activeRouteHud[i];
                bool excluded = excludedTlPositions.Any(p => NavUtils.Hud2Dist(p, pos) < 5.0);
                res.Add(new RoutePointInfo
                {
                    OriginalIndex = i,
                    X          = pos.X,
                    Z          = pos.Z,
                    IsTeleport = activeTlFlags != null && i < activeTlFlags.Count && activeTlFlags[i],
                    IsCurrent  = i == cur,
                    IsExcluded = excluded
                });
            }
            return res;
        }

        // ════════════════════════════════════════════════════════════════════
        // Reverse / ForceRefresh
        // ════════════════════════════════════════════════════════════════════
        public void ReverseRoute()
        {
            if (currentGoalHud == null) { capi.ShowChatMessage("[tl] No active route."); return; }
            var epos = capi.World.Player.Entity.SidedPos;
            var me   = NavUtils.AbsToHud3(capi, epos.X, epos.Y, epos.Z);
            FindRouteTo((int)me.X, (int)me.Z);
        }

        public void ForceRefresh()
        {
            lastHttpFailAt = DateTime.MinValue;
            if (cfg.Source == GeoSource.Waypointer)
            {
                cachedBaseGraph = null;
                capi.ShowChatMessage(WaypointerFileExists()
                    ? "[tl] Waypointer cache cleared. Reloading on next route."
                    : "[tl] Waypointer GeoJSON not found yet: " + GetWaypointerGeoPath());
                return;
            }
            cachedBaseGraph = null;
            capi.ShowChatMessage("[tl] Refreshing TL data...");
            Task.Run(() =>
            {
                bool ok = RefreshFromRemote();
                capi.Event.EnqueueMainThreadTask(
                    () => capi.ShowChatMessage(ok ? "[tl] TL data updated." : "[tl] Failed to update."),
                    "tlpath-refresh");
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // Coordinates  (FIX #6: thin wrappers over NavUtils)
        // ════════════════════════════════════════════════════════════════════
        Vec3d AbsToHud3(double x, double y, double z) => NavUtils.AbsToHud3(capi, x, y, z);
        Vec3d HudToAbs3(double x, double y, double z) => NavUtils.HudToAbs3(capi, x, y, z);

        Vec2d FromMap2D(double mx, double my)
        {
            double x = mx, y = -my;
            if (cfg.SwapAxes) (x, y) = (y, x);
            if (cfg.FlipX) x = -x;
            if (cfg.FlipY) y = -y;
            return new Vec2d(x * cfg.ScaleX + cfg.OffsetX, y * cfg.ScaleY + cfg.OffsetY);
        }

        // ════════════════════════════════════════════════════════════════════
        // Data management  (FIX #1: HTTP on background thread)
        // ════════════════════════════════════════════════════════════════════
        bool NeedsUpdate() => !File.Exists(geoPath) ||
            (DateTime.UtcNow - File.GetLastWriteTimeUtc(geoPath)).TotalHours > Math.Max(1, cfg.TtlHours);

        bool RefreshFromRemote() // called from background thread only
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.RemoteUrl))
                {
                    capi.Event.EnqueueMainThreadTask(() => capi.ShowChatMessage("[tl] Remote URL not set."), "tlpath-msg");
                    return false;
                }
                var tmp = Path.Combine(dataDir, "translocators.tmp");
                using (var resp = http.GetAsync(cfg.RemoteUrl).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        int code = (int)resp.StatusCode;
                        capi.Event.EnqueueMainThreadTask(() => capi.ShowChatMessage($"[tl] HTTP {code}"), "tlpath-msg");
                        return false;
                    }
                    using var s  = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var fs = File.Create(tmp);
                    s.CopyTo(fs);
                }
                using (var doc = JsonDocument.Parse(File.ReadAllText(tmp, Encoding.UTF8)))
                {
                    if (doc.RootElement.GetProperty("type").GetString() != "FeatureCollection")
                    {
                        capi.Event.EnqueueMainThreadTask(() => capi.ShowChatMessage("[tl] Invalid GeoJSON"), "tlpath-msg");
                        File.Delete(tmp); return false;
                    }
                }
                File.Copy(tmp, geoPath, true); File.Delete(tmp);
                cachedBaseGraph = null; return true;
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                capi.Event.EnqueueMainThreadTask(() => capi.ShowChatMessage("[tl] error: " + msg), "tlpath-msg");
                return false;
            }
        }

        // FIX #5: cache; only rebuild when file changes
        Graph LoadGraph()
        {
            string geo = CurrentGeoPath();
            if (cachedBaseGraph != null)
            {
                var mt = File.Exists(geo) ? File.GetLastWriteTimeUtc(geo) : DateTime.MinValue;
                if (mt <= cachedBaseGraphAt) return cachedBaseGraph;
            }
            var g = new Graph();
            if (!File.Exists(geo)) return g;
            JsonDocument jdoc;
            try { jdoc = JsonDocument.Parse(File.ReadAllText(geo, Encoding.UTF8)); } catch { return g; }

            // Waypointer GeoJSON is already in HUD (spawn-relative) coords — no flip.
            // URL source (map.tops) uses screen-Y orientation — apply existing FromMap2D.
            System.Func<double, double, Vec2d> toHud2 = cfg.Source == GeoSource.Waypointer
                ? (mx, my) => new Vec2d(mx, my)
                : (System.Func<double, double, Vec2d>)FromMap2D;

            const double Snap = 1.0;
            (long, long) Key(Vec3d p) => ((long)Math.Round(p.X / Snap), (long)Math.Round(p.Z / Snap));
            var index = new Dictionary<(long, long), int>();
            int GetOrAdd(Vec3d v) { var k = Key(v); if (index.TryGetValue(k, out int id)) return id; int nid = g.AddNode(v); index[k] = nid; return nid; }

            foreach (var f in jdoc.RootElement.GetProperty("features").EnumerateArray())
            {
                if (!f.TryGetProperty("geometry", out var geom)) continue;
                if (geom.GetProperty("type").GetString() != "LineString") continue;
                var coords = geom.GetProperty("coordinates");
                if (coords.GetArrayLength() < 2) continue;
                var a = coords[0]; var b = coords[coords.GetArrayLength() - 1];
                double h1 = 0, h2 = 0;
                if (f.TryGetProperty("properties", out var props))
                {
                    if (props.TryGetProperty("depth1", out var d1) && d1.ValueKind == JsonValueKind.Number) h1 = d1.GetDouble();
                    if (props.TryGetProperty("depth2", out var d2) && d2.ValueKind == JsonValueKind.Number) h2 = d2.GetDouble();
                }
                var hA = toHud2(a[0].GetDouble(), a[1].GetDouble());
                var hB = toHud2(b[0].GetDouble(), b[1].GetDouble());
                int ia = GetOrAdd(new Vec3d(hA.X, h1, hA.Y)); int ib = GetOrAdd(new Vec3d(hB.X, h2, hB.Y));
                if (ia != ib) g.AddTeleportEdge(ia, ib);
            }
            DedupeAdj(g);
            cachedBaseGraph = g; cachedBaseGraphAt = File.Exists(geo) ? File.GetLastWriteTimeUtc(geo) : DateTime.UtcNow;
            return g;
        }

        // ════════════════════════════════════════════════════════════════════
        // Graph
        // ════════════════════════════════════════════════════════════════════
        class Graph
        {
            public readonly List<Vec3d>                    Nodes = new();
            public readonly List<List<(int to, double w)>> Adj   = new();
            public readonly HashSet<(int a, int b)>        Tele  = new();
            public int AddNode(Vec3d p) { int i = Nodes.Count; Nodes.Add(p); Adj.Add(new List<(int, double)>()); return i; }
            public void AddTeleportEdge(int a, int b) { if (a == b) return; Adj[a].Add((b, 3.0)); Adj[b].Add((a, 3.0)); Tele.Add((a, b)); Tele.Add((b, a)); }
            public void AddWalkEdge(int a, int b, double w) { if (a == b) return; Adj[a].Add((b, w)); Adj[b].Add((a, w)); }
            public bool IsTeleport(int a, int b) => Tele.Contains((a, b));
        }
        static void DedupeAdj(Graph g) { for (int i = 0; i < g.Adj.Count; i++) g.Adj[i] = g.Adj[i].GroupBy(e => e.to).Select(grp => (to: grp.Key, w: grp.Min(e => e.w))).ToList(); }

        // ════════════════════════════════════════════════════════════════════
        // Walk edges  (FIX #4: grid reuse)
        // ════════════════════════════════════════════════════════════════════
        static double WalkTime(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z, dy = b.Y - a.Y;
            // 0.22 ≈ empirically calibrated (was 0.12, ran ~1.85× too fast)
            double t = Math.Sqrt(dx * dx + dz * dz) * 0.22;
            return t + (dy > 0 ? (dy / 30.0) * 15.0 : (Math.Abs(dy) / 30.0) * 8.0);
        }
        static (int, int) CellKey(Vec3d p, int cell) => ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Z / cell));
        static IEnumerable<(int, int)> NeighCells((int cx, int cz) k) { for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) yield return (k.cx + dx, k.cz + dz); }

        Dictionary<(int, int), List<int>> BuildGrid(Graph g, int cell)
        {
            var grid = new Dictionary<(int, int), List<int>>(g.Nodes.Count * 2);
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var k = CellKey(g.Nodes[i], cell);
                if (!grid.TryGetValue(k, out var list)) { list = new List<int>(); grid[k] = list; }
                list.Add(i);
            }
            return grid;
        }
        void ConnectAllWalk(Graph g, double r, Dictionary<(int, int), List<int>> grid)
        {
            double r2 = r * r;
            foreach (var kv in grid) foreach (var nb in NeighCells(kv.Key))
            {
                if (!grid.TryGetValue(nb, out var list)) continue;
                foreach (int a in kv.Value) foreach (int b in list) { if (b <= a) continue; if (NavUtils.Dist2Hud(g.Nodes[a], g.Nodes[b]) <= r2) g.AddWalkEdge(a, b, WalkTime(g.Nodes[a], g.Nodes[b])); }
            }
            DedupeAdj(g);
        }
        void ConnectNodeWalk(Graph g, int idx, double r, Dictionary<(int, int), List<int>> grid)
        {
            double r2 = r * r; var p = g.Nodes[idx];
            foreach (var nb in NeighCells(CellKey(p, Math.Max(1, (int)r))))
            {
                if (!grid.TryGetValue(nb, out var list)) continue;
                foreach (int i in list) { if (i == idx) continue; if (NavUtils.Dist2Hud(p, g.Nodes[i]) <= r2) g.AddWalkEdge(idx, i, WalkTime(p, g.Nodes[i])); }
            }
        }

        // FIX #2: O(E log V) Dijkstra
        // Note: SortedSet element is (dist, nodeIndex). Named tuple fields not used to avoid
        // Comparer lambda resolution issues on older Roslyn — use Item1/Item2 explicitly.
        static List<int>? Dijkstra(Graph g, int start, int goal, HashSet<int>? forbidden = null)
        {
            int n = g.Nodes.Count;
            var dist = new double[n]; var prev = new int[n];
            Array.Fill(dist, double.PositiveInfinity); Array.Fill(prev, -1);
            dist[start] = 0;

            // Comparer: sort by distance first, then by node index to break ties (required for SortedSet uniqueness)
            var cmp = Comparer<(double, int)>.Create((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1);
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            });
            var pq = new SortedSet<(double, int)>(cmp);
            pq.Add((0.0, start));

            while (pq.Count > 0)
            {
                var top = pq.Min;
                pq.Remove(top);
                double d = top.Item1; int v = top.Item2;
                if (v == goal) break;
                if (d > dist[v]) continue;

                foreach (var edge in g.Adj[v])
                {
                    int to = edge.to; double w = edge.w;
                    if (forbidden != null && forbidden.Contains(to) && to != goal) continue;
                    double nd = dist[v] + w;
                    if (nd < dist[to])
                    {
                        pq.Remove((dist[to], to));
                        dist[to] = nd; prev[to] = v;
                        pq.Add((nd, to));
                    }
                }
            }

            if (start != goal && prev[goal] == -1) return null;
            var path = new List<int>();
            for (int cur = goal; cur != -1; cur = prev[cur]) path.Add(cur);
            path.Reverse();
            return path;
        }

        // FIX #8: CleanPath as private static method
        static List<int> CleanPath(Graph g, List<int> raw)
        {
            var res = new List<int>();
            for (int i = 0; i < raw.Count; i++)
            {
                int cur = raw[i];
                if (i == 0 || i == raw.Count - 1) { res.Add(cur); continue; }
                bool nTL = g.IsTeleport(cur, raw[i + 1]), pTL = g.IsTeleport(raw[i - 1], cur);
                if (nTL || pTL) { res.Add(cur); continue; }
                if (!g.Adj[cur].Any(e => g.IsTeleport(cur, e.to))) res.Add(cur);
            }
            return res;
        }

        // ════════════════════════════════════════════════════════════════════
        // EnsureUpToDate (FIX #1: async)
        // ════════════════════════════════════════════════════════════════════
        void EnsureUpToDateThen(Action onReady)
        {
            if (cfg.Source == GeoSource.Waypointer)
            {
                var p = GetWaypointerGeoPath();
                if (File.Exists(p)) { onReady(); return; }
                capi.ShowChatMessage("[tl] Waypointer GeoJSON not found. Walk past a TL pair first (or open Settings).");
                return;
            }
            // Silence repeated failures: if HTTP failed recently and cache exists, use it quietly
            if ((DateTime.UtcNow - lastHttpFailAt).TotalMinutes < 5 && File.Exists(geoPath))
            { onReady(); return; }

            if (!NeedsUpdate()) { onReady(); return; }
            capi.ShowChatMessage("[tl] Updating TL data...");
            Task.Run(() =>
            {
                bool ok = RefreshFromRemote();
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (ok)
                    {
                        lastHttpFailAt = DateTime.MinValue;
                        onReady();
                    }
                    else if (File.Exists(geoPath))
                    {
                        if (lastHttpFailAt == DateTime.MinValue)
                            capi.ShowChatMessage("[tl] Update failed — using cached data.");
                        lastHttpFailAt = DateTime.UtcNow;
                        onReady();
                    }
                    else
                    {
                        lastHttpFailAt = DateTime.UtcNow;
                        capi.ShowChatMessage("[tl] Failed to update TL data. No cache available.");
                    }
                }, "tlpath-route");
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // Public route API
        // ════════════════════════════════════════════════════════════════════

        // FIX #9: accept double, round to int
        public void FindRouteTo(double destXd, double destZd) => FindRouteTo((int)Math.Round(destXd), (int)Math.Round(destZd));
        public void FindRouteTo(int destX, int destZ) => EnsureUpToDateThen(() => BuildRoute(destX, destZ));

        void BuildRoute(int destX, int destZ)
        {
            // Capture the silent flag at the top so all paths (early-return "destination nearby",
            // Direct() on no-path-found, and the main !sameRoute branch) can respect it.
            bool silentBuild = suppressBuildChat;
            suppressBuildChat = false;

            var baseGraph = LoadGraph();
            var epos = capi.World.Player.Entity.SidedPos;
            Vec3d me   = AbsToHud3(epos.X, epos.Y, epos.Z);
            Vec3d goal = new Vec3d(destX, me.Y, destZ);

            var prevRouteHud = activeRouteHud;
            int prevRoutePos = compass?.RoutePos ?? 0;
            var prevGoalHud  = currentGoalHud;

            // Determine if this is a fresh destination before mutating state.
            bool isNewDest = prevGoalHud == null
                          || Math.Abs(prevGoalHud.X - goal.X) > 1.0
                          || Math.Abs(prevGoalHud.Z - goal.Z) > 1.0;
            if (isNewDest)
            {
                passedTlCount = 0;
                routeTotalTp  = 0;
                tlCrossingStack.Clear();
            }

            currentGoalHud = goal; activeRouteHud = null; activeTlFlags = null;
            activeRoutePos = 0;    pendingRecalcAt = null;
            lastTouchedRouteIdx = -1;
            // Note: excludedTlPositions is intentionally NOT cleared here — user keeps excludes across recalcs

            if (Math.Sqrt(NavUtils.Dist2Hud(me, goal)) <= cfg.FinishDirectHud)
            {
                if (!silentBuild) capi.ShowChatMessage("[tl] Destination nearby, going direct.");
                // Preserve counter values — without these, GPS recalc near the goal would reset "3/10" to "0/0"
                compass?.SetRoute3(new List<Vec3d> { HudToAbs3(goal.X, goal.Y, goal.Z) }, new List<bool>(),
                                   routeTotalTp, passedTlCount);
                nextActiveRecalcAt = DateTime.UtcNow + ActiveRecalcInterval;
                return;
            }

            // Clone base graph and add walk edges
            var g = new Graph();
            foreach (var p in baseGraph.Nodes) g.AddNode(p);
            for (int from = 0; from < baseGraph.Adj.Count; from++)
                foreach (var e in baseGraph.Adj[from]) if (from < e.to) g.AddWalkEdge(from, e.to, e.w);
            foreach (var ab in baseGraph.Adj.SelectMany((lst, a) => lst.Select(e => (a, e.to))))
                if (baseGraph.IsTeleport(ab.a, ab.to)) g.Tele.Add((ab.a, ab.to));

            int cell = Math.Max(1, (int)cfg.WalkRadiusHud);
            var grid = BuildGrid(g, cell);
            ConnectAllWalk(g, cfg.WalkRadiusHud, grid);

            int startIdx = g.AddNode(me);
            var sk = CellKey(me, cell); if (!grid.TryGetValue(sk, out var sl)) { sl = new List<int>(); grid[sk] = sl; } sl.Add(startIdx);
            ConnectNodeWalk(g, startIdx, cfg.WalkRadiusHud, grid);

            int goalIdx = g.AddNode(goal);
            var gk = CellKey(goal, cell); if (!grid.TryGetValue(gk, out var gl)) { gl = new List<int>(); grid[gk] = gl; } gl.Add(goalIdx);
            ConnectNodeWalk(g, goalIdx, cfg.WalkRadiusHud, grid);

            // Build forbidden set: find base-graph nodes closest to each excluded TL position
            HashSet<int> forbidden = null;
            if (excludedTlPositions.Count > 0)
            {
                forbidden = new HashSet<int>();
                foreach (var excPos in excludedTlPositions)
                {
                    // Search within base graph node range (tight snap: 10 HUD units)
                    int best = -1; double bestD2 = 100.0;
                    for (int ni = 0; ni < baseGraph.Nodes.Count; ni++)
                    {
                        double d2 = NavUtils.Dist2Hud(excPos, baseGraph.Nodes[ni]);
                        if (d2 < bestD2) { bestD2 = d2; best = ni; }
                    }
                    if (best >= 0) forbidden.Add(best);
                }
                if (forbidden.Count == 0) forbidden = null;
            }

            var path = Dijkstra(g, startIdx, goalIdx, forbidden);
            if (path == null || path.Count < 2) { Direct(goal, silentBuild); return; }

            path = CleanPath(g, path);
            if (path.Count < 2) { Direct(goal, silentBuild); return; }

            var hudPts = path.Select(i => g.Nodes[i]).ToList();
            var absPts = hudPts.Select(p => HudToAbs3(p.X, p.Y, p.Z)).ToList();

            bool sameRoute = RoutesEqual(prevRouteHud, hudPts);
            if (!sameRoute && !silentBuild)
                capi.ShowChatMessage($"[tl] {Fmt(me)} → {Fmt(goal)}  TL={baseGraph.Nodes.Count} walkR={cfg.WalkRadiusHud:0}");

            var tlFlags = new List<bool>(hudPts.Count - 1);
            double walkSec = 0; int tp = 0;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                int a = path[i], b = path[i + 1]; bool isTL = g.IsTeleport(a, b); tlFlags.Add(isTL);
                if (isTL) { tp++; continue; }
                var edge = g.Adj[a].FirstOrDefault(e => e.to == b);
                walkSec += edge.to == b ? edge.w : WalkTime(g.Nodes[a], g.Nodes[b]);
            }
            if (tlFlags.Count != Math.Max(0, absPts.Count - 1))
                tlFlags = Enumerable.Range(0, hudPts.Count - 1).Select(i => Math.Sqrt(NavUtils.Dist2Hud(hudPts[i], hudPts[i + 1])) <= 1.5).ToList();

            // Ratchet total up — never let the denominator shrink mid-journey.
            // (passedTlCount/routeTotalTp were already reset to 0 on isNewDest at the top of BuildRoute.)
            routeTotalTp = Math.Max(routeTotalTp, passedTlCount + tp);

            if (!sameRoute && !silentBuild)
            {
                string tlInfo = (passedTlCount > 0 && routeTotalTp > 0)
                    ? $"{passedTlCount}/{routeTotalTp} TL done"
                    : $"{tp} TL";
                double etaSec = walkSec + tp * 10.0; // ~10 sec per TL (approach + activation + arrival)
                capi.ShowChatMessage($"[tl] ETA ≈ {etaSec / 60.0:0.#} min  ({tlInfo}, {walkSec / 60.0:0.#} min walk)");
            }

            activeRouteHud = hudPts; activeTlFlags = tlFlags;
            if (sameRoute)
            {
                // Snap RoutePos to player's actual segment (handles backward walking when recalc fires
                // and the topology stays identical). Skip seg(0,1) since hudPts[0] is the player itself.
                int snapped = SnapRoutePosToPlayer(hudPts, me, prevRoutePos);
                activeRoutePos = snapped;
                compass?.SyncRoutePos(snapped);
                compass?.SetTpProgress(passedTlCount, routeTotalTp);
            }
            else
            {
                activeRoutePos = 0;
                compass?.SetRoute3(absPts, tlFlags, routeTotalTp, passedTlCount);
            }
            // Schedule the next GPS-style recalc
            nextActiveRecalcAt = DateTime.UtcNow + ActiveRecalcInterval;
        }

        // Snap to the closest route segment whose end-index is at or before prevRoutePos.
        // Skips segment (0, 1) because route[0] is always the player's own position
        // (added as startIdx in BuildRoute), which would bias snap to always return 1.
        static int SnapRoutePosToPlayer(List<Vec3d> route, Vec3d player, int prevRoutePos)
        {
            if (route == null || route.Count < 3) return Math.Max(1, prevRoutePos);
            double bestD = double.MaxValue;
            int bestSegEnd = prevRoutePos;
            for (int i = 1; i + 1 < route.Count; i++)
            {
                double d = DistPointToSegment2D(player, route[i], route[i + 1]);
                if (d < bestD) { bestD = d; bestSegEnd = i + 1; }
            }
            // Only regress: forward progress is handled by soft-advance / jump-detect / touch.
            return Math.Min(prevRoutePos, bestSegEnd);
        }

        void Direct(Vec3d goal, bool silent)
        {
            if (!silent) capi.ShowChatMessage("[tl] Route not found. Going direct.");
            // Preserve counter values — GPS-style recalc must not clobber "3/10" to "0/0"
            compass?.SetRoute3(new List<Vec3d> { HudToAbs3(goal.X, goal.Y, goal.Z) }, new List<bool>(),
                               routeTotalTp, passedTlCount);
            nextActiveRecalcAt = DateTime.UtcNow + ActiveRecalcInterval;
        }

        static bool RoutesEqual(List<Vec3d> a, List<Vec3d> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            // Skip index 0: it's the player's current position, which changes every recalc.
            // Compare only intermediate waypoints (TL nodes) and goal — that's what actually matters.
            for (int i = 1; i < a.Count; i++)
                if (Math.Sqrt(NavUtils.Dist2Hud(a[i], b[i])) > 1.0) return false;
            return true;
        }

        static string Fmt(Vec3d p) => $"{p.X:0} {p.Z:0}";

        public void ClearCompass()
        {
            compass?.ClearRoute(); compass?.SetTarget(null, null);
            currentGoalHud = null; prevPosHud = null;
            activeRouteHud = null; activeTlFlags = null;
            activeRoutePos = 0;   routeTotalTp = 0;   passedTlCount = 0;
            pendingRecalcAt = null;
            lastTouchedRouteIdx = -1;
            nextActiveRecalcAt  = DateTime.MinValue;
            tlCrossingStack.Clear();
            excludedTlPositions.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // Tick
        // ════════════════════════════════════════════════════════════════════
        void OnClientTick(float dt)
        {
            var ent = capi.World?.Player?.Entity;
            if (ent == null) return;
            if (activeRouteHud == null || activeRouteHud.Count < 2 || currentGoalHud == null) { prevPosHud = null; pendingRecalcAt = null; return; }

            activeRoutePos = compass.RoutePos; // FIX #3: always read from renderer
            var me = AbsToHud3(ent.SidedPos.X, ent.SidedPos.Y, ent.SidedPos.Z);

            // Pending recalc after unplanned TL
            if (pendingRecalcAt.HasValue && DateTime.UtcNow >= pendingRecalcAt.Value && DateTime.UtcNow - lastRecalcAt > RecalcCooldown)
            {
                suppressBuildChat = true;  // automatic — silence chat
                FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z);
                lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null; prevPosHud = me; return;
            }

            // Jump detection MUST run before GPS recalc: otherwise a TL crossing on the same tick as
            // a scheduled GPS recalc would be "stolen" — GPS would rebuild from the new position and
            // reset prevPosHud, so the actual TL event would never be counted (passedTlCount stuck at 0).
            if (prevPosHud != null && Math.Sqrt(NavUtils.Dist2Hud(me, prevPosHud)) >= TLJumpThreshHUD)
            {
                // ── Counter: route-independent, direction-aware via base graph + crossing stack ──
                bool baseMatched = TryMatchBaseTl(prevPosHud, me, out int bIdxFrom, out int bIdxTo);
                if (baseMatched) RecordTlCrossing(bIdxFrom, bIdxTo);

                // ── RoutePos: only adjusts based on the active route, doesn't touch counter ──
                int iP = FindNearestRouteIndex(prevPosHud, TLNearRadiusHUD);
                int iC = FindNearestRouteIndex(me,         TLArrivalRadiusHUD);
                bool plannedForward  = iP >= 0 && iC >= 0 && iC == iP + 1 &&
                                       iP < activeTlFlags.Count && activeTlFlags[iP];
                bool plannedBackward = iP >= 0 && iC >= 0 && iC == iP - 1 &&
                                       iC < activeTlFlags.Count && activeTlFlags[iC];
                if (plannedForward)
                {
                    // Advance past the TL exit so HUD shows the next walk waypoint immediately.
                    int afterTl = Math.Min(iC + 1, activeRouteHud.Count - 1);
                    int newPos  = Math.Max(activeRoutePos, afterTl);
                    activeRoutePos = newPos; compass.SyncRoutePos(newPos);
                    lastLegitTlAt = lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null;
                }
                else if (plannedBackward)
                {
                    // Point compass back at the TL exit they came from — they need to re-enter the TL to continue.
                    activeRoutePos = iP; compass.SyncRoutePos(iP);
                    lastLegitTlAt = DateTime.UtcNow; pendingRecalcAt = null;
                    capi.ShowChatMessage("[tl] Backward TL — re-enter to continue.");
                    if (DateTime.UtcNow - lastRecalcAt > RecalcCooldown)
                    {
                        suppressBuildChat = true;  // we already chatted "Backward TL" — don't double-chat
                        FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z);
                        lastRecalcAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    if (baseMatched) lastLegitTlAt = DateTime.UtcNow;
                    pendingRecalcAt = DateTime.UtcNow.AddSeconds(PendingRecalcDelay);
                }
                lastTouchedRouteIdx = -1;
                prevPosHud = me; return;
            }

            // GPS-style: periodic active recalc from current position. Runs AFTER jump-detect so that
            // a TL crossing this tick is counted first; the recalc then rebuilds from the post-TL position.
            // BuildRoute reschedules nextActiveRecalcAt to UtcNow + ActiveRecalcInterval, so no runaway loop.
            if (nextActiveRecalcAt != DateTime.MinValue && DateTime.UtcNow >= nextActiveRecalcAt)
            {
                suppressBuildChat = true;  // silent — don't spam chat every 7 seconds
                FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z);
                lastRecalcAt = DateTime.UtcNow;
                prevPosHud = me;
                return;
            }

            // Soft advance — walk segments only; TL edges are handled by jump detection
            int nr = FindNearestRouteIndex(me, TLNearRadiusHUD);
            if (nr >= 0 && nr >= activeRoutePos - 1 && nr != activeRoutePos)
            {
                int advanceTo = Math.Max(activeRoutePos, nr);
                bool crossesTl = false;
                for (int k = activeRoutePos; k < advanceTo && k < activeTlFlags.Count; k++)
                    if (activeTlFlags[k]) { crossesTl = true; break; }
                if (!crossesTl)
                { activeRoutePos = advanceTo; compass.SyncRoutePos(activeRoutePos); }
            }

            // FIX #2: Touch-based TL pair detection — catches close TL pairs that don't trigger jump-detect
            int touched = FindNearestRouteIndex(me, TLArrivalRadiusHUD);
            if (touched >= 0)
            {
                if (touched != lastTouchedRouteIdx && lastTouchedRouteIdx >= 0)
                {
                    bool tlPairCompleted = touched == lastTouchedRouteIdx + 1
                                        && lastTouchedRouteIdx < activeTlFlags.Count
                                        && activeTlFlags[lastTouchedRouteIdx]
                                        && (DateTime.UtcNow - lastTouchedAt) < TouchPairWindow;
                    if (tlPairCompleted && touched > activeRoutePos)
                    {
                        // Advance past the TL exit, same as jump-detect plannedForward
                        int afterTl = Math.Min(touched + 1, activeRouteHud.Count - 1);
                        activeRoutePos = afterTl;
                        compass.SyncRoutePos(afterTl);
                        lastLegitTlAt = DateTime.UtcNow;
                        // Counter: direction-aware via base graph (handles forward+back correctly)
                        if (TryMatchBaseTl(activeRouteHud[lastTouchedRouteIdx], activeRouteHud[touched],
                                           out int bP, out int bC))
                        {
                            RecordTlCrossing(bP, bC);
                        }
                    }
                }
                if (touched != lastTouchedRouteIdx)
                {
                    lastTouchedRouteIdx = touched;
                    lastTouchedAt = DateTime.UtcNow;
                }
            }

            // FIX #3: Soft regress — player walked backward close to a previous waypoint
            if (activeRoutePos >= 2 && DateTime.UtcNow - lastLegitTlAt > TlGrace)
            {
                int prevIdx = activeRoutePos - 1;
                double dPrev2 = NavUtils.Dist2Hud(me, activeRouteHud[prevIdx]);
                double dCur2  = NavUtils.Dist2Hud(me, activeRouteHud[activeRoutePos]);
                bool prevIsTlEdge = prevIdx < activeTlFlags.Count && activeTlFlags[prevIdx];
                double softNearR2 = TLNearRadiusHUD * TLNearRadiusHUD;
                if (!prevIsTlEdge && dPrev2 < softNearR2 && dPrev2 + 1.0 < dCur2)
                {
                    activeRoutePos = prevIdx;
                    compass.SyncRoutePos(activeRoutePos);
                    lastTouchedRouteIdx = -1;
                }
            }

            // FIX #3: Corridor check — use min distance to current (R-1, R) and next (R, R+1) segments.
            // Previous version only checked the next segment, which missed perpendicular drift before reaching R.
            if (activeRouteHud.Count >= 2 && DateTime.UtcNow - lastLegitTlAt > TlGrace)
            {
                double dMin = double.MaxValue;
                if (activeRoutePos >= 1 && activeRoutePos < activeRouteHud.Count)
                {
                    double d = DistPointToSegment2D(me, activeRouteHud[activeRoutePos - 1], activeRouteHud[activeRoutePos]);
                    if (d < dMin) dMin = d;
                }
                if (activeRoutePos < activeRouteHud.Count - 1)
                {
                    double d = DistPointToSegment2D(me, activeRouteHud[activeRoutePos], activeRouteHud[activeRoutePos + 1]);
                    if (d < dMin) dMin = d;
                }
                if (dMin > CorridorRadiusHUD && DateTime.UtcNow - lastRecalcAt > RecalcCooldown)
                {
                    suppressBuildChat = true;  // automatic — silence chat
                    FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z);
                    lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null; prevPosHud = me; return;
                }
            }

            prevPosHud = me;
        }

        int FindNearestRouteIndex(Vec3d p, double r)
        {
            if (activeRouteHud == null) return -1;
            double r2 = r * r; int best = -1; double bestD2 = double.MaxValue;
            for (int i = 0; i < activeRouteHud.Count; i++) { double d2 = NavUtils.Dist2Hud(p, activeRouteHud[i]); if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = i; } }
            return best;
        }

        // Identify which base-graph TL pair the player just used. Returns the (from, to)
        // node indices so the counter logic can detect forward vs. backward direction.
        bool TryMatchBaseTl(Vec3d prev, Vec3d cur, out int idxPrev, out int idxCur)
        {
            idxPrev = -1; idxCur = -1;
            var g = cachedBaseGraph;
            if (g == null) return false;
            double bestPrevD2 = 50.0 * 50.0;   // 50 HUD slack for the entry side (player may have walked a bit)
            double bestCurD2  = 30.0 * 30.0;   // tighter for the exit side (player lands precisely)
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                double dp = NavUtils.Dist2Hud(prev, g.Nodes[i]);
                if (dp < bestPrevD2) { bestPrevD2 = dp; idxPrev = i; }
                double dc = NavUtils.Dist2Hud(cur, g.Nodes[i]);
                if (dc < bestCurD2) { bestCurD2 = dc; idxCur = i; }
            }
            return idxPrev >= 0 && idxCur >= 0 && idxPrev != idxCur
                && g.IsTeleport(idxPrev, idxCur);
        }

        // Direction-aware counter: if the new crossing reverses an earlier one already on the stack,
        // remove it and decrement (player undid that TL). Otherwise push and increment.
        // Handles forward+back through the same TL correctly (counter returns to original).
        void RecordTlCrossing(int from, int to)
        {
            for (int i = tlCrossingStack.Count - 1; i >= 0; i--)
            {
                if (tlCrossingStack[i].from == to && tlCrossingStack[i].to == from)
                {
                    tlCrossingStack.RemoveAt(i);
                    passedTlCount = Math.Max(0, passedTlCount - 1);
                    compass?.SetTpProgress(passedTlCount, routeTotalTp);
                    return;
                }
            }
            tlCrossingStack.Add((from, to));
            passedTlCount++;
            routeTotalTp = Math.Max(routeTotalTp, passedTlCount);
            compass?.SetTpProgress(passedTlCount, routeTotalTp);
        }

        static double DistPointToSegment2D(Vec3d p, Vec3d a, Vec3d b)
        {
            double vx = b.X - a.X, vz = b.Z - a.Z, wx = p.X - a.X, wz = p.Z - a.Z, vv = vx * vx + vz * vz;
            if (vv <= 1e-9) return Math.Sqrt(NavUtils.Dist2Hud(p, a));
            double t = Math.Clamp((vx * wx + vz * wz) / vv, 0.0, 1.0);
            double dx = p.X - (a.X + t * vx), dz = p.Z - (a.Z + t * vz);
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}