#nullable disable
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NavMod
{
    public class TlPathSystem : ModSystem
    {
        ICoreClientAPI capi;
        CompassRibbonRenderer compass;
        TlPathService svc;
        TlPathGui     gui;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            compass = new CompassRibbonRenderer(capi);
            capi.Event.RegisterRenderer(compass, EnumRenderStage.Ortho);

            var datadir = System.IO.Path.Combine(capi.GetOrCreateDataPath("tlpath"), "client");
            svc = new TlPathService(capi, datadir, compass);
            gui = new TlPathGui(capi, svc);

            // ── Hotkey ───────────────────────────────────────────────────────
            capi.Input.RegisterHotKey(
                "tlpathgui",
                "TLPath: Open navigation window",
                GlKeys.N,
                HotkeyType.GUIOrOtherControls);

            capi.Input.SetHotKeyHandler("tlpathgui", _ => { gui.Open(); return true; });

            // ── Commands ─────────────────────────────────────────────────────
            capi.RegisterCommand("tlpath", "Navigate via TL", "",
                (int groupId, CmdArgs args) =>
                {
                    if (args.Length == 0) { Help(); return; }
                    string sub = args.PopWord().ToLowerInvariant();

                    switch (sub)
                    {
                        case "find":
                        {
                            // FIX #9: parse as double
                            double x = 0, z = 0;
                            if (args.Length >= 2 &&
                                double.TryParse(args.PopWord(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out x) &&
                                double.TryParse(args.PopWord(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out z))
                            { /* parsed */ }
                            svc.FindRouteTo(x, z);
                            svc.AddToHistory((int)Math.Round(x), (int)Math.Round(z));
                            return;
                        }

                        case "walk":
                        {
                            if (args.Length == 0 || !double.TryParse(args.PopWord(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double r))
                            {
                                capi.ShowChatMessage($"[tl] walk radius = {svc.WalkRadius:0} (in-game units)");
                                return;
                            }
                            svc.SetWalkRadius(r);
                            capi.ShowChatMessage($"[tl] walk radius set to {r:0} (in-game units)");
                            return;
                        }

                        case "stop":
                            svc.ClearCompass();
                            capi.ShowChatMessage("[tl] Navigation stopped.");
                            return;

                        case "link":
                            if (args.Length == 0) { capi.ShowChatMessage("[tl] GeoJSON URL: " + svc.GetRemoteUrl()); return; }
                            svc.SetRemoteUrl(args.PopAll());
                            return;

                        case "show":
                            svc.CmdShow();
                            return;

                        case "fav":
                        case "save":
                        {
                            string name = args.Length > 0 ? args.PopAll().Trim() : "";
                            if (string.IsNullOrEmpty(name)) { capi.ShowChatMessage("[tl] Usage: .tlpath fav <name>"); return; }
                            svc.SaveFavourite(name);
                            return;
                        }

                        case "reverse":
                            capi.ShowChatMessage("[tl] 'reverse' command removed. Use .tlpath find <x z> instead.");
                            return;

                        case "gui":
                            gui.Open();
                            return;

                        default:
                            Help();
                            return;
                    }
                });
        }

        void Help()
        {
            capi.ShowChatMessage("[tl] commands:");
            capi.ShowChatMessage("  .tlpath find <x z>    — plot route to coordinates");
            capi.ShowChatMessage("  .tlpath walk <r>      — set walk radius (in-game units)");
            capi.ShowChatMessage("  .tlpath stop          — stop and clear current route");
            capi.ShowChatMessage("  .tlpath link [url]    — show or set GeoJSON source URL");
            capi.ShowChatMessage("  .tlpath show          — toggle idle compass on/off");
            capi.ShowChatMessage("  .tlpath fav <name>    — save current route as favourite");
            capi.ShowChatMessage("  .tlpath gui           — open navigation window  [hotkey: N]");
        }

        public override void Dispose()
        {
            gui?.TryClose();
            compass?.Dispose();
        }
    }
}