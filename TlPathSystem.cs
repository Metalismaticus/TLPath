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

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            compass = new CompassRibbonRenderer(capi);
            capi.Event.RegisterRenderer(compass, EnumRenderStage.Ortho);

            var datadir = System.IO.Path.Combine(capi.GetOrCreateDataPath("tlpath"), "client");
            svc = new TlPathService(capi, datadir, compass);

            capi.RegisterCommand("tlpath", "Navigate via TL", "",
                (int groupId, CmdArgs args) =>
                {
                    if (args.Length == 0) { Help(); return; }
                    string sub = args.PopWord().ToLowerInvariant();

                    switch (sub)
                    {
                        case "find":
                        {
                            // .tlpath find [x z] — HUD-координаты. Без аргументов — к 0 0.
                            int x = 0, z = 0;
                            if (args.Length >= 2 &&
                                int.TryParse(args.PopWord(), out x) &&
                                int.TryParse(args.PopWord(), out z))
                            {
                                // ok
                            }
                            svc.FindRouteTo(x, z);
                            return;
                        }

                        case "walk":
                        {
                            // .tlpath walk <радиусHUD>
                            if (args.Length == 0 || !int.TryParse(args.PopWord(), out int r))
                            {
                                capi.ShowChatMessage($"[tl] walk={svc.WalkRadius:0} (HUD)");
                                return;
                            }
                            svc.SetWalkRadius(r);
                            capi.ShowChatMessage($"[tl] walk set to {svc.WalkRadius:0} (HUD)");
                            return;
                        }

                        case "stop":
                            svc.ClearCompass();
                            capi.ShowChatMessage("[tl] navigation stopped");
                            return;

                        default:
                            Help();
                            return;
                    }
                }
            );
        }

        void Help()
        {
            capi.ShowChatMessage("[tl] commands:");
            capi.ShowChatMessage("  .tlpath find [x z]   — plot a route to the specified coordinates");
            capi.ShowChatMessage("  .tlpath walk <r>     — set the radius for walking connections between TLs");
            capi.ShowChatMessage("  .tlpath stop         — stop and clear the current route");
            capi.ShowChatMessage("  .tlpath link <url>         — set a GeoJSON link to update data");
        }

        public override void Dispose()
        {
            compass?.Dispose();
        }
    }
}
