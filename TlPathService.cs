﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace NavMod
{
    /// <summary>
    /// Routing via TL. Nodes: HUD X/Z + world height (Y).
    /// Walking edges weighted by time:
    ///   horiz: 0.12 s/m
    ///   vertical: 8 s per 30 down, 15 s per 30 up
    /// TL edge fixed 3 s.
    /// GeoJSON Y is inverted (my => -my).
    /// </summary>
    public class TlPathService
    {
        readonly ICoreClientAPI capi;
        readonly string dataDir;
        readonly string cfgPath;
        readonly string geoPath;
        readonly HttpClient http = new HttpClient();
        readonly CompassRibbonRenderer compass;

        public TlPathService(ICoreClientAPI capi, string dataDir, CompassRibbonRenderer compass)
        {
            this.capi = capi;
            this.dataDir = dataDir;
            this.compass = compass;

            Directory.CreateDirectory(dataDir);
            cfgPath = Path.Combine(dataDir, "config.json");
            geoPath = Path.Combine(dataDir, "translocators.geojson");
            LoadCfg();

            // применяем сохранённый флаг "показывать пустой компас"
            ApplyIdleFlagToCompass();
        }

        // ===== config =====
        class Cfg
        {
            public string RemoteUrl { get; set; } = "https://map.tops.vintagestory.at/data/geojson/translocators.geojson";
            public int TtlHours { get; set; } = 24;

            public double ScaleX { get; set; } = 1;
            public double ScaleY { get; set; } = 1;
            public double OffsetX { get; set; } = 0;
            public double OffsetY { get; set; } = 0;
            public bool SwapAxes { get; set; } = false;
            public bool FlipX { get; set; } = false;
            public bool FlipY { get; set; } = false;

            public double WalkRadiusHud { get; set; } = 3000;
            public double FinishDirectHud { get; set; } = 150;

            // показывать пустой компас при отсутствии маршрута
            public bool ShowCompassIdle { get; set; } = false;
        }
        Cfg cfg = new Cfg();

        public double WalkRadius => cfg.WalkRadiusHud;

        public void SetWalkRadius(double r)
        {
            cfg.WalkRadiusHud = GameMath.Clamp(r, 1, 50000);
            SaveCfg();
        }

        public void SetRemoteUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                capi.ShowChatMessage("[tl] Invalid URL.");
                return;
            }
            cfg.RemoteUrl = url.Trim();
            SaveCfg();
            capi.ShowChatMessage("[tl] GeoJSON link set to: " + cfg.RemoteUrl);
        }

        public string GetRemoteUrl() => cfg.RemoteUrl;

        // .tlpath show  — toggle/on/off
        public void CmdShow(string arg = null)
        {
            bool? wanted = ParseBoolLoose(arg);
            if (wanted.HasValue) cfg.ShowCompassIdle = wanted.Value;
            else cfg.ShowCompassIdle = !cfg.ShowCompassIdle;

            SaveCfg();
            ApplyIdleFlagToCompass();

            capi.ShowChatMessage(cfg.ShowCompassIdle
                ? "[tl] Compass: idle display ON"
                : "[tl] Compass: idle display OFF");
        }

        // helper: "on/off/true/false/1/0" → bool? ; null if unknown/empty
        bool? ParseBoolLoose(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "on" || s == "true"  || s == "enable"  || s == "enabled")  return true;
            if (s == "0" || s == "off"|| s == "false" || s == "disable" || s == "disabled") return false;
            return null;
        }

        void ApplyIdleFlagToCompass()
        {
            try { compass?.SetIdleVisible(cfg.ShowCompassIdle); }
            catch { /* старая версия рендерера — молча игнор */ }
        }

        void LoadCfg()
        {
            try
            {
                if (File.Exists(cfgPath))
                    cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(cfgPath, Encoding.UTF8)) ?? new Cfg();
            }
            catch { cfg = new Cfg(); }
        }

        void SaveCfg()
        {
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

        // ===== coords =====
        Vec3d AbsToHud3(double absX, double absY, double absZ)
        {
            var sp = capi.World.DefaultSpawnPosition;
            return new Vec3d(absX - sp.X, absY, absZ - sp.Z);
        }
        Vec3d HudToAbs3(double hudX, double yWorld, double hudZ)
        {
            var sp = capi.World.DefaultSpawnPosition;
            return new Vec3d(hudX + sp.X, yWorld, hudZ + sp.Z);
        }

        // GeoJSON -> HUD (invert map Y)
        Vec2d FromMap2D(double mx, double my)
        {
            double x = mx, y = -my;
            if (cfg.SwapAxes) { var t = x; x = y; y = t; }
            if (cfg.FlipX) x = -x;
            if (cfg.FlipY) y = -y;
            x = x * cfg.ScaleX + cfg.OffsetX;
            y = y * cfg.ScaleY + cfg.OffsetY;
            return new Vec2d(x, y);
        }

        // ===== data =====
        bool NeedsUpdate()
        {
            if (!File.Exists(geoPath)) return true;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(geoPath);
            return age.TotalHours > Math.Max(1, cfg.TtlHours);
        }

        bool RefreshFromRemote()
        {
            try
            {
                var url = cfg.RemoteUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    capi.ShowChatMessage("[tl] Remote URL not set.");
                    return false;
                }

                var tmp = Path.Combine(dataDir, "translocators.tmp");

                using (var resp = http.GetAsync(url).Result)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        capi.ShowChatMessage($"[tl] HTTP {(int)resp.StatusCode}");
                        return false;
                    }
                    using (var s = resp.Content.ReadAsStreamAsync().Result)
                    using (var fs = File.Create(tmp)) { s.CopyTo(fs); }
                }

                using (var doc = JsonDocument.Parse(File.ReadAllText(tmp, Encoding.UTF8)))
                {
                    if (doc.RootElement.GetProperty("type").GetString() != "FeatureCollection")
                    {
                        capi.ShowChatMessage("[tl] Invalid GeoJSON");
                        File.Delete(tmp);
                        return false;
                    }
                }

                File.Copy(tmp, geoPath, true);
                File.Delete(tmp);
                return true;
            }
            catch (Exception ex)
            {
                capi.ShowChatMessage("[tl] error loading: " + ex.Message);
                return false;
            }
        }

        class Graph
        {
            public readonly List<Vec3d> Nodes = new List<Vec3d>();
            public readonly List<List<(int to, double w)>> Adj = new List<List<(int, double)>>();
            // explicit TL-edge registry to avoid “weight≈3” heuristics
            public readonly HashSet<(int a, int b)> Tele = new HashSet<(int, int)>();

            public int AddNode(Vec3d p)
            {
                int idx = Nodes.Count;
                Nodes.Add(p);
                Adj.Add(new List<(int, double)>());
                return idx;
            }
            public void AddTeleportEdge(int a, int b)
            {
                if (a == b) return;
                Adj[a].Add((b, 3.0));
                Adj[b].Add((a, 3.0));
                Tele.Add((a, b));
                Tele.Add((b, a));
            }
            public void AddWalkEdge(int a, int b, double w)
            {
                if (a == b) return;
                Adj[a].Add((b, w));
                Adj[b].Add((a, w));
            }
            public bool IsTeleport(int a, int b) => Tele.Contains((a, b));
        }

        Graph LoadGraph()
        {
            var g = new Graph();
            if (!File.Exists(geoPath)) return g;

            JsonDocument jdoc;
            try { jdoc = JsonDocument.Parse(File.ReadAllText(geoPath, Encoding.UTF8)); }
            catch { return g; }

            const double Snap = 1.0;
            (long, long) Key(Vec3d p) => ((long)Math.Round(p.X / Snap), (long)Math.Round(p.Z / Snap));
            var index = new Dictionary<(long, long), int>();

            int GetOrAdd(Vec3d v)
            {
                var k = Key(v);
                if (index.TryGetValue(k, out int id)) return id;
                int nid = g.AddNode(v);
                index[k] = nid;
                return nid;
            }

            var feats = jdoc.RootElement.GetProperty("features");
            foreach (var f in feats.EnumerateArray())
            {
                if (!f.TryGetProperty("geometry", out var geom)) continue;
                var type = geom.GetProperty("type").GetString();
                if (type != "LineString") continue;

                var coords = geom.GetProperty("coordinates");
                if (coords.GetArrayLength() < 2) continue;

                var a = coords[0];
                var b = coords[coords.GetArrayLength() - 1];

                double ax = a[0].GetDouble();
                double ay = a[1].GetDouble();
                double bx = b[0].GetDouble();
                double by = b[1].GetDouble();

                double h1 = 0, h2 = 0;
                if (f.TryGetProperty("properties", out var props))
                {
                    if (props.TryGetProperty("depth1", out var d1) && d1.ValueKind == JsonValueKind.Number) h1 = d1.GetDouble();
                    if (props.TryGetProperty("depth2", out var d2) && d2.ValueKind == JsonValueKind.Number) h2 = d2.GetDouble();
                }

                var hudA = FromMap2D(ax, ay);
                var hudB = FromMap2D(bx, by);

                var va = new Vec3d(hudA.X, h1, hudA.Y);
                var vb = new Vec3d(hudB.X, h2, hudB.Y);

                int ia = GetOrAdd(va);
                int ib = GetOrAdd(vb);
                if (ia != ib) g.AddTeleportEdge(ia, ib); // TL = 3s + explicit flag
            }

            // dedup by min weight (keeps 3.0 for TL)
            for (int a = 0; a < g.Adj.Count; a++)
            {
                g.Adj[a] = g.Adj[a]
                    .GroupBy(e => e.to)
                    .Select(grp => (to: grp.Key, w: grp.Min(e => e.w)))
                    .ToList();
            }

            return g;
        }

        // 2D HUD distance^2
        static double Dist2_HUD(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        // Walking time (seconds)
        static double WalkTime(in Vec3d a, in Vec3d b)
        {
            double dx = a.X - b.X, dz = a.Z - b.Z, dy = b.Y - a.Y;
            double horiz = Math.Sqrt(dx * dx + dz * dz);
            double t = horiz * 0.12;                 // 100 m -> 12 s
            if (dy > 0) t += (dy / 30.0) * 15.0;     // up
            else        t += (Math.Abs(dy) / 30.0) * 8.0; // down
            return t;
        }

        Dictionary<(int, int), List<int>> BuildGrid(Graph g, int cell)
        {
            var grid = new Dictionary<(int, int), List<int>>(g.Nodes.Count * 2);
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var p = g.Nodes[i];
                var k = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Z / cell));
                if (!grid.TryGetValue(k, out var list)) { list = new List<int>(); grid[k] = list; }
                list.Add(i);
            }
            return grid;
        }
        static IEnumerable<(int, int)> NeighCells((int, int) k)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    yield return (k.Item1 + dx, k.Item2 + dz);
        }

        void ConnectWalkBetweenAllNodes(Graph g, double walkRadius)
        {
            int cell = Math.Max(1, (int)walkRadius);
            var grid = BuildGrid(g, cell);
            double r2 = walkRadius * walkRadius;

            foreach (var kv in grid)
            foreach (var nb in NeighCells(kv.Key))
            {
                if (!grid.TryGetValue(nb, out var list)) continue;
                foreach (int a in kv.Value)
                foreach (int b in list)
                {
                    if (b <= a) continue;
                    var d2 = Dist2_HUD(g.Nodes[a], g.Nodes[b]);
                    if (d2 <= r2)
                    {
                        double w = WalkTime(g.Nodes[a], g.Nodes[b]);
                        g.AddWalkEdge(a, b, w);
                    }
                }
            }

            // dedup
            for (int a = 0; a < g.Adj.Count; a++)
            {
                g.Adj[a] = g.Adj[a]
                    .GroupBy(e => e.to)
                    .Select(grp => (to: grp.Key, w: grp.Min(e => e.w)))
                    .ToList();
            }
        }

        void ConnectByWalk(Graph g, int nodeIdx, double walkRadius, out int links)
        {
            links = 0;
            int cell = Math.Max(1, (int)walkRadius);
            var grid = BuildGrid(g, cell);
            var p = g.Nodes[nodeIdx];
            var k = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Z / cell));
            double r2 = walkRadius * walkRadius;

            foreach (var nb in NeighCells(k))
            {
                if (!grid.TryGetValue(nb, out var list)) continue;
                foreach (int i in list)
                {
                    if (i == nodeIdx) continue;
                    var d2 = Dist2_HUD(p, g.Nodes[i]);
                    if (d2 <= r2)
                    {
                        double w = WalkTime(p, g.Nodes[i]);
                        g.AddWalkEdge(nodeIdx, i, w); links++;
                    }
                }
            }
        }

        static List<int> Dijkstra(Graph g, int start, int goal)
        {
            int n = g.Nodes.Count;
            var dist = Enumerable.Repeat(double.PositiveInfinity, n).ToArray();
            var prev = Enumerable.Repeat(-1, n).ToArray();
            var used = new bool[n];

            dist[start] = 0;
            for (int _ = 0; _ < n; _++)
            {
                int v = -1; double best = double.PositiveInfinity;
                for (int i = 0; i < n; i++)
                    if (!used[i] && dist[i] < best) { best = dist[i]; v = i; }
                if (v == -1 || v == goal) break;

                used[v] = true;
                foreach (var (to, w) in g.Adj[v])
                {
                    double nd = dist[v] + w;
                    if (nd < dist[to]) { dist[to] = nd; prev[to] = v; }
                }
            }

            if (start != goal && prev[goal] == -1) return null;

            var path = new List<int>();
            for (int cur = goal; cur != -1; cur = prev[cur]) path.Add(cur);
            path.Reverse();
            return path;
        }

        void EnsureUpToDateThen(Action onReady)
        {
            if (NeedsUpdate())
            {
                if (!RefreshFromRemote())
                {
                    capi.ShowChatMessage("[tl] Failed to update TL data.");
                    return;
                }
            }
            onReady();
        }

        // ===== API =====

        public void FindRouteTo(int destX, int destYhud)
        {
            EnsureUpToDateThen(() =>
            {
                var baseGraph = LoadGraph();
                int tlCount = baseGraph.Nodes.Count;
                int tlEdges = baseGraph.Adj.Sum(l => l.Count) / 2;

                var pos = capi.World.Player.Entity.Pos;
                Vec3d meHud = AbsToHud3(pos.X, pos.Y, pos.Z);
                Vec3d goalHud = new Vec3d(destX, meHud.Y, destYhud);

                if (Math.Sqrt(Dist2_HUD(meHud, goalHud)) <= cfg.FinishDirectHud)
                {
                    capi.ShowChatMessage("[tl] Destination close, going direct.");
                    var oneAbs = HudToAbs3(goalHud.X, goalHud.Y, goalHud.Z);
                    compass?.SetRoute3(new List<Vec3d> { oneAbs }, new List<bool>());
                    return;
                }

                var g = new Graph();
                foreach (var p in baseGraph.Nodes) g.AddNode(p);
                for (int from = 0; from < baseGraph.Adj.Count; from++)
                    foreach (var e in baseGraph.Adj[from])
                        if (from < e.to) g.AddWalkEdge(from, e.to, e.w); // weights already set (3s for TL kept below)

                // restore TL flags for copied edges
                foreach (var ab in baseGraph.Adj.SelectMany((lst, a) => lst.Select(e => (a, e.to))))
                    if (baseGraph.IsTeleport(ab.a, ab.to)) g.Tele.Add((ab.a, ab.to));

                ConnectWalkBetweenAllNodes(g, cfg.WalkRadiusHud);

                int start = g.AddNode(meHud);
                ConnectByWalk(g, start, cfg.WalkRadiusHud, out _);

                int goal = g.AddNode(goalHud);
                ConnectByWalk(g, goal, cfg.WalkRadiusHud, out _);

                capi.ShowChatMessage($"[tl] HUD start={Fmt(meHud)} goal={Fmt(goalHud)}; TL={tlCount}, edges≈{tlEdges}, walkR={cfg.WalkRadiusHud:0}");

                var path = Dijkstra(g, start, goal);
                if (path == null || path.Count < 2)
                {
                    capi.ShowChatMessage("[tl] route not found. Going direct.");
                    var oneAbs = HudToAbs3(goalHud.X, goalHud.Y, goalHud.Z);
                    compass?.SetRoute3(new List<Vec3d> { oneAbs }, new List<bool>());
                    return;
                }

                // remove “stand-on-TL then walk away” points
                List<int> CleanPath(Graph gg, List<int> raw)
                {
                    var outp = new List<int>();
                    for (int i = 0; i < raw.Count; i++)
                    {
                        int cur = raw[i];
                        bool nextIsTL = false;
                        if (i + 1 < raw.Count)
                        {
                            int nxt = raw[i + 1];
                            nextIsTL = gg.IsTeleport(cur, nxt);
                        }
                        // keep if start/goal, or adjacent to a TL edge
                        if (i == 0 || i == raw.Count - 1) { outp.Add(cur); continue; }
                        bool prevIsTL = gg.IsTeleport(raw[i - 1], cur);
                        if (nextIsTL || prevIsTL) outp.Add(cur);
                        else
                        {
                            bool curHasTLIncident = gg.Adj[cur].Any(e => gg.IsTeleport(cur, e.to));
                            if (!curHasTLIncident) outp.Add(cur);
                        }
                    }
                    return outp;
                }
                path = CleanPath(g, path);
                if (path.Count < 2)
                {
                    capi.ShowChatMessage("[tl] route degenerated after TL-clean. Going direct.");
                    var oneAbs = HudToAbs3(goalHud.X, goalHud.Y, goalHud.Z);
                    compass?.SetRoute3(new List<Vec3d> { oneAbs }, new List<bool>());
                    return;
                }

                var hudPoints = path.Select(i => g.Nodes[i]).ToList();
                var absPoints = hudPoints.Select(p => HudToAbs3(p.X, p.Y, p.Z)).ToList();

                // TL flags strictly from registry
                var tlFlags = new List<bool>(hudPoints.Count > 0 ? hudPoints.Count - 1 : 0);
                double walkSec = 0;
                int tp = 0;
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    int a = path[i], b = path[i + 1];
                    bool isTL = g.IsTeleport(a, b);
                    tlFlags.Add(isTL);
                    if (isTL) tp++;

                    // безопасно берём вес: без Exception при расхождении
                    var edge = g.Adj[a].FirstOrDefault(e => e.to == b);
                    if (!isTL)
                    {
                        if (edge.to == b) walkSec += edge.w;
                        else walkSec += WalkTime(g.Nodes[a], g.Nodes[b]); // редкая защита
                    }
                }

                // safety: align sizes for renderer
                if (tlFlags.Count != Math.Max(0, absPoints.Count - 1))
                {
                    tlFlags = new List<bool>();
                    for (int i = 0; i + 1 < hudPoints.Count; i++)
                        tlFlags.Add(Math.Sqrt(Dist2_HUD(hudPoints[i], hudPoints[i + 1])) <= 1.5);
                }

                double totalSec = walkSec + tp * 3.0;
                double totalMin = totalSec / 60.0;
                double walkMin  = walkSec  / 60.0;
                capi.ShowChatMessage($"[tl] ETA ≈ {totalMin:0.#} min ({tp} TL, {walkMin:0.#} min walking)");

                compass?.SetRoute3(absPoints, tlFlags);
            });
        }

        static string Fmt(Vec3d p) => $"{p.X:0} {p.Y:0} {p.Z:0}";

        public void ClearCompass()
        {
            compass?.ClearTargetsQueue();
            compass?.SetTarget(null, null);
        }
    }
}
