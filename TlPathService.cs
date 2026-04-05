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
using Vintagestory.API.MathTools;

namespace NavMod
{
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

        // ── Cached base graph ────────────────────────────────────────────────
        Graph    cachedBaseGraph   = null;
        DateTime cachedBaseGraphAt = DateTime.MinValue;

        // ── Navigation state ─────────────────────────────────────────────────
        Vec3d currentGoalHud = null;

        // FIX #3: prevPos kept here; RoutePos lives only in compass
        Vec3d     prevPosHud      = null;
        DateTime  lastRecalcAt   = DateTime.MinValue;
        DateTime  lastLegitTlAt  = DateTime.MinValue;
        DateTime? pendingRecalcAt = null;
        int       activeRoutePos  = 0; // local mirror of compass.RoutePos

        List<Vec3d> activeRouteHud = null;
        List<bool>  activeTlFlags  = null;


        // ── Constants ────────────────────────────────────────────────────────
        const double TLJumpThreshHUD   = 200.0;
        const double TLNearRadiusHUD   = 150.0;
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
            prevPosHud      = null;
            pendingRecalcAt = null;
            excludedTlPositions.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // Config
        // ════════════════════════════════════════════════════════════════════
        class Cfg
        {
            public string RemoteUrl       { get; set; } = "https://map.tops.vintagestory.at/data/geojson/translocators.geojson";
            public int    TtlHours        { get; set; } = 24;
            public double ScaleX          { get; set; } = 1;
            public double ScaleY          { get; set; } = 1;
            public double OffsetX         { get; set; } = 0;
            public double OffsetY         { get; set; } = 0;
            public bool   SwapAxes        { get; set; } = false;
            public bool   FlipX           { get; set; } = false;
            public bool   FlipY           { get; set; } = false;
            public double WalkRadiusHud   { get; set; } = 3000;
            public double FinishDirectHud { get; set; } = 150;
            public bool   ShowCompassIdle { get; set; } = false;
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
            if (cachedBaseGraph != null)
            {
                var mt = File.Exists(geoPath) ? File.GetLastWriteTimeUtc(geoPath) : DateTime.MinValue;
                if (mt <= cachedBaseGraphAt) return cachedBaseGraph;
            }
            var g = new Graph();
            if (!File.Exists(geoPath)) return g;
            JsonDocument jdoc;
            try { jdoc = JsonDocument.Parse(File.ReadAllText(geoPath, Encoding.UTF8)); } catch { return g; }

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
                var hA = FromMap2D(a[0].GetDouble(), a[1].GetDouble());
                var hB = FromMap2D(b[0].GetDouble(), b[1].GetDouble());
                int ia = GetOrAdd(new Vec3d(hA.X, h1, hA.Y)); int ib = GetOrAdd(new Vec3d(hB.X, h2, hB.Y));
                if (ia != ib) g.AddTeleportEdge(ia, ib);
            }
            DedupeAdj(g);
            cachedBaseGraph = g; cachedBaseGraphAt = File.Exists(geoPath) ? File.GetLastWriteTimeUtc(geoPath) : DateTime.UtcNow;
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
            double t = Math.Sqrt(dx * dx + dz * dz) * 0.12;
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
            if (!NeedsUpdate()) { onReady(); return; }
            capi.ShowChatMessage("[tl] Updating TL data...");
            Task.Run(() =>
            {
                bool ok = RefreshFromRemote();
                capi.Event.EnqueueMainThreadTask(() => { if (ok) onReady(); else capi.ShowChatMessage("[tl] Failed to update TL data."); }, "tlpath-route");
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
            var baseGraph = LoadGraph();
            var epos = capi.World.Player.Entity.SidedPos;
            Vec3d me   = AbsToHud3(epos.X, epos.Y, epos.Z);
            Vec3d goal = new Vec3d(destX, me.Y, destZ);

            currentGoalHud = goal; activeRouteHud = null; activeTlFlags = null;
            activeRoutePos = 0;    pendingRecalcAt = null;
            // Note: excludedTlPositions is intentionally NOT cleared here — user keeps excludes across recalcs

            if (Math.Sqrt(NavUtils.Dist2Hud(me, goal)) <= cfg.FinishDirectHud)
            {
                capi.ShowChatMessage("[tl] Destination nearby, going direct.");
                compass?.SetRoute3(new List<Vec3d> { HudToAbs3(goal.X, goal.Y, goal.Z) }, new List<bool>());
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
            ConnectAllWalk(g, cfg.WalkRadiusHud, BuildGrid(g, cell));

            // FIX #4: one grid for both start and goal
            var grid = BuildGrid(g, cell);

            int startIdx = g.AddNode(me);
            var sk = CellKey(me, cell); if (!grid.TryGetValue(sk, out var sl)) { sl = new List<int>(); grid[sk] = sl; } sl.Add(startIdx);
            ConnectNodeWalk(g, startIdx, cfg.WalkRadiusHud, grid);

            int goalIdx = g.AddNode(goal);
            var gk = CellKey(goal, cell); if (!grid.TryGetValue(gk, out var gl)) { gl = new List<int>(); grid[gk] = gl; } gl.Add(goalIdx);
            ConnectNodeWalk(g, goalIdx, cfg.WalkRadiusHud, grid);

            capi.ShowChatMessage($"[tl] {Fmt(me)} → {Fmt(goal)}  TL={baseGraph.Nodes.Count} walkR={cfg.WalkRadiusHud:0}");

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
            if (path == null || path.Count < 2) { Direct(goal); return; }

            path = CleanPath(g, path);
            if (path.Count < 2) { Direct(goal); return; }

            var hudPts = path.Select(i => g.Nodes[i]).ToList();
            var absPts = hudPts.Select(p => HudToAbs3(p.X, p.Y, p.Z)).ToList();

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

            capi.ShowChatMessage($"[tl] ETA ≈ {(walkSec + tp * 3.0) / 60.0:0.#} min  ({tp} TL, {walkSec / 60.0:0.#} min walking)");

            activeRouteHud = hudPts; activeTlFlags = tlFlags; activeRoutePos = 0;
            compass?.SetRoute3(absPts, tlFlags);
        }

        void Direct(Vec3d goal)
        {
            capi.ShowChatMessage("[tl] Route not found. Going direct.");
            compass?.SetRoute3(new List<Vec3d> { HudToAbs3(goal.X, goal.Y, goal.Z) }, new List<bool>());
        }

        static string Fmt(Vec3d p) => $"{p.X:0} {p.Z:0}";

        public void ClearCompass()
        {
            compass?.ClearRoute(); compass?.SetTarget(null, null);
            currentGoalHud = null; prevPosHud = null;
            activeRouteHud = null; activeTlFlags = null;
            activeRoutePos = 0;   pendingRecalcAt = null;
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
            { FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z); lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null; prevPosHud = me; return; }

            // Jump detection
            if (prevPosHud != null && Math.Sqrt(NavUtils.Dist2Hud(me, prevPosHud)) >= TLJumpThreshHUD)
            {
                int iP = FindNearestRouteIndex(prevPosHud, TLNearRadiusHUD);
                int iC = FindNearestRouteIndex(me,         TLNearRadiusHUD);
                bool planned = iP >= 0 && iC >= 0 && Math.Abs(iC - iP) == 1 &&
                               Math.Min(iP, iC) < activeTlFlags.Count && activeTlFlags[Math.Min(iP, iC)];
                if (planned)
                {
                    int newPos = Math.Max(activeRoutePos, Math.Max(iP, iC));
                    activeRoutePos = newPos; compass.SyncRoutePos(newPos); // FIX #3
                    lastLegitTlAt = lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null;
                }
                else pendingRecalcAt = DateTime.UtcNow.AddSeconds(PendingRecalcDelay);
                prevPosHud = me; return;
            }

            // Corridor check
            if (activeRoutePos < activeRouteHud.Count - 1 && DateTime.UtcNow - lastLegitTlAt > TlGrace)
            {
                if (DistPointToSegment2D(me, activeRouteHud[activeRoutePos], activeRouteHud[activeRoutePos + 1]) > CorridorRadiusHUD
                    && DateTime.UtcNow - lastRecalcAt > RecalcCooldown)
                { FindRouteTo((int)currentGoalHud.X, (int)currentGoalHud.Z); lastRecalcAt = DateTime.UtcNow; pendingRecalcAt = null; prevPosHud = me; return; }
            }

            // Soft advance
            int nr = FindNearestRouteIndex(me, TLNearRadiusHUD);
            if (nr >= 0 && nr >= activeRoutePos - 1 && nr != activeRoutePos)
            { activeRoutePos = Math.Max(activeRoutePos, nr); compass.SyncRoutePos(activeRoutePos); } // FIX #3

            prevPosHud = me;
        }

        int FindNearestRouteIndex(Vec3d p, double r)
        {
            if (activeRouteHud == null) return -1;
            double r2 = r * r; int best = -1; double bestD2 = double.MaxValue;
            for (int i = 0; i < activeRouteHud.Count; i++) { double d2 = NavUtils.Dist2Hud(p, activeRouteHud[i]); if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = i; } }
            return best;
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