#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace NavMod
{
    // ── Localisation ─────────────────────────────────────────────────────────

    public enum GuiLang { English, Russian }

    static class L
    {
        public static GuiLang Lang = GuiLang.English;
        static string En(string en, string ru) => Lang == GuiLang.Russian ? ru : en;

        public static string TabNavigate  => En("Navigate",    "Маршрут");
        public static string TabRoute     => En("Route",       "Путь");
        public static string TabFavs      => En("Favourites",  "Избранное");
        public static string TabHistory   => En("History",     "История");
        public static string TabSettings  => En("Settings",    "Настройки");

        public static string LblX         => "X:";
        public static string LblZ         => "Z:";
        public static string BtnFind      => En("Find Route",  "Найти путь");
        public static string BtnStop      => En("Stop",        "Стоп");
        public static string HintCoords   => En("Coordinates as shown on the in-game map.",
                                                "Координаты как на карте игры.");
        public static string LblSaveAs    => En("Save as:",    "Сохранить как:");
        public static string BtnSave      => En("Save",        "Сохранить");
        public static string MsgEnterName => En("[tl] Enter a name.", "[tl] Введите название.");
        public static string MsgBadCoord  => En("[tl] Invalid coordinates.", "[tl] Неверные координаты.");

        public static string RouteHeader(int n) => En($"Route — {n} waypoints", $"Маршрут — {n} точек");
        public static string RouteNone    => En("No active route. Use Navigate tab.",
                                               "Нет маршрута. Используйте вкладку Маршрут.");
        public static string BtnSkipTl   => En("Skip TL",   "Пропустить");
        public static string BtnRestore  => En("Restore",   "Вернуть");
        public static string BtnRecalc   => En("Recalculate", "Пересчитать");
        public static string BtnResetEx  => En("Reset skips", "Сбросить");
        public static string TagTl       => "[TL]";
        public static string TagSkip     => "[skip]";
        public static string TagCurrent  => "<<";

        public static string FavsHeader  => En("Saved routes:", "Сохранённые маршруты:");
        public static string FavsNone    => En("No favourites yet. Use Navigate > Save.",
                                              "Нет избранного. Используйте Маршрут > Сохранить.");
        public static string BtnGo       => En("Go",   "Идти");
        public static string BtnDel      => En("Del",  "Удал.");

        public static string HistHeader  => En("Recent destinations:", "Недавние цели:");
        public static string HistNone    => En("No history yet.", "История пуста.");
        public static string BtnSaveFav  => En("Save", "Сохр.");
        public static string BtnClearHist => En("Clear history", "Очистить");

        public static string LblWalk     => En("Walk radius (game units):", "Радиус ходьбы (ед.):");
        public static string LblUrl      => En("GeoJSON URL:", "Ссылка GeoJSON:");
        public static string LblIdle     => En("Compass when idle:", "Компас без маршрута:");
        public static string LblLang     => En("Language:", "Язык:");
        public static string BtnApply    => En("Apply",   "Принять");
        public static string BtnSet      => En("Set",     "Сохр.");
        public static string BtnRefresh  => En("Refresh TL data now", "Обновить данные TL");
        public static string ToggleOn    => En("ON",  "ВКЛ");
        public static string ToggleOff   => En("OFF", "ВЫКЛ");
        public static string Footer      => "TLPath — navigation via Translocators";

        public static string PageOf(int a, int b, int tot) => $"{a}–{b} / {tot}";
        public static string ScrollUp   => "/\\";
        public static string ScrollDown => "\\/";
    }

    // ── GUI ──────────────────────────────────────────────────────────────────

    public class TlPathGui : GuiDialog
    {
        readonly TlPathService svc;

        int activeTab = 0;

        string inputX       = "";
        string inputZ       = "";
        string inputFavName = "";
        string inputUrl     = "";
        string inputWalk    = "";

        int scrollFavs  = 0;
        int scrollHist  = 0;
        int scrollRoute = 0;

        List<RoutePointInfo> routePoints = new();

        // ── Layout constants ─────────────────────────────────────────────────
        // Wider window to fit Russian labels comfortably
        const double W      = 680;
        const double H      = 530;
        const double TAB_H  = 30;
        const double BODY_Y = 62;
        const double BODY_H = H - BODY_Y - 10;

        const int MAX_ROUTE_ROWS = 9;
        const int MAX_FAV_ROWS   = 8;
        const int MAX_HIST_ROWS  = 8;

        // Right margin reserved for scroll arrows
        const double SCROLL_W = 34;

        string[] TabLabels => new[] {
            L.TabNavigate, L.TabRoute, L.TabFavs, L.TabHistory, L.TabSettings
        };

        public override string ToggleKeyCombinationCode => "tlpathgui";

        public TlPathGui(ICoreClientAPI capi, TlPathService svc) : base(capi)
        {
            this.svc = svc;
        }

        public void Open()
        {
            if (IsOpened()) { TryClose(); return; }
            RefreshState();
            Compose();
            TryOpen();
        }

        void RefreshState()
        {
            routePoints = svc.GetRoutePointInfos();
            inputUrl    = svc.GetRemoteUrl();
            inputWalk   = ((int)svc.WalkRadius).ToString();
        }

        void Compose()
        {
            var dlgBounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, W, H);
            var bgBounds  = ElementBounds.Fixed(0, 0, W, H);
            double tabW   = W / TabLabels.Length;

            var comp = capi.Gui
                .CreateCompo("tlpath-main", dlgBounds)
                .AddDialogBG(bgBounds, true, 0.95f)
                .AddDialogTitleBar("TLPath Navigator", () => TryClose());

            for (int i = 0; i < TabLabels.Length; i++)
            {
                int idx = i;
                comp.AddButton(TabLabels[i],
                    () => { activeTab = idx; Recompose(); return true; },
                    ElementBounds.Fixed(i * tabW, 28, tabW - 2, TAB_H),
                    CairoFont.WhiteSmallText(),
                    activeTab == i ? EnumButtonStyle.Normal : EnumButtonStyle.Small,
                    "tab-" + i);
            }

            var body = ElementBounds.Fixed(0, BODY_Y, W, BODY_H);

            switch (activeTab)
            {
                case 0: ComposeNavigate(comp, body); break;
                case 1: ComposeRoute(comp, body);    break;
                case 2: ComposeFavs(comp, body);     break;
                case 3: ComposeHistory(comp, body);  break;
                case 4: ComposeSettings(comp, body); break;
            }

            SingleComposer = comp.Compose();

            SetInput("input-x",       inputX);
            SetInput("input-z",       inputZ);
            SetInput("input-url",     inputUrl);
            SetInput("input-walk",    inputWalk);
            SetInput("input-favname", inputFavName);
        }

        void Recompose() { RefreshState(); Compose(); }

        void SetInput(string key, string val)
        {
            try { SingleComposer?.GetTextInput(key)?.SetValue(val); } catch { }
        }

        // ── Helper: scroll arrows ────────────────────────────────────────────
        // Draws /\ and \/ arrows; returns content area width (W minus scroll column)
        double AddScrollArrows(GuiComposer comp, int scroll, int maxScroll,
                               double listStartY, int visibleRows, double rowH,
                               System.Action onUp, System.Action onDn,
                               string keyUp, string keyDn)
        {
            double ax = W - SCROLL_W;
            if (scroll > 0)
                comp.AddSmallButton(L.ScrollUp, () => { onUp(); Recompose(); return true; },
                    ElementBounds.Fixed(ax, listStartY, 28, 26),
                    EnumButtonStyle.Small, keyUp);
            if (scroll < maxScroll)
                comp.AddSmallButton(L.ScrollDown, () => { onDn(); Recompose(); return true; },
                    ElementBounds.Fixed(ax, listStartY + visibleRows * rowH - 28, 28, 26),
                    EnumButtonStyle.Small, keyDn);
            return W - SCROLL_W - 10;   // usable content width
        }

        // ── Tab 0: Navigate ──────────────────────────────────────────────────

        void ComposeNavigate(GuiComposer comp, ElementBounds body)
        {
            double x = 24, y = body.fixedY + 18;
            double lw = 42, fw = 160;

            comp.AddStaticText(L.LblX, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lw, 24));
            comp.AddTextInput(ElementBounds.Fixed(x + lw, y, fw, 30),
                v => inputX = v, CairoFont.WhiteSmallText(), "input-x");

            comp.AddStaticText(L.LblZ, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x + lw + fw + 24, y + 6, lw, 24));
            comp.AddTextInput(ElementBounds.Fixed(x + lw + fw + 24 + lw, y, fw, 30),
                v => inputZ = v, CairoFont.WhiteSmallText(), "input-z");

            y += 44;

            comp.AddButton(L.BtnFind, () =>
            {
                if (TryParseCoord(inputX, out double px) && TryParseCoord(inputZ, out double pz))
                {
                    svc.FindRouteTo(px, pz);
                    svc.AddToHistory((int)Math.Round(px), (int)Math.Round(pz));
                    activeTab = 1; Recompose();
                }
                else capi.ShowChatMessage(L.MsgBadCoord);
                return true;
            }, ElementBounds.Fixed(x, y, 140, 32));

            comp.AddButton(L.BtnStop, () => { svc.ClearCompass(); Recompose(); return true; },
                ElementBounds.Fixed(x + 150, y, 100, 32));

            y += 52;

            comp.AddStaticText(L.HintCoords,
                CairoFont.WhiteSmallText().WithFontSize(14),
                ElementBounds.Fixed(x, y, W - 40, 24));

            y += 42;

            double lblSaveW = 130;
            comp.AddStaticText(L.LblSaveAs, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lblSaveW, 24));
            comp.AddTextInput(ElementBounds.Fixed(x + lblSaveW + 8, y, 230, 30),
                v => inputFavName = v, CairoFont.WhiteSmallText(), "input-favname");
            comp.AddButton(L.BtnSave, () =>
            {
                string name = inputFavName.Trim();
                if (string.IsNullOrEmpty(name)) { capi.ShowChatMessage(L.MsgEnterName); return true; }
                if (TryParseCoord(inputX, out double px) && TryParseCoord(inputZ, out double pz))
                    svc.SaveFavouriteEntry((int)Math.Round(px), (int)Math.Round(pz), name);
                else
                    svc.SaveFavourite(name);
                activeTab = 2; Recompose();
                return true;
            }, ElementBounds.Fixed(x + lblSaveW + 8 + 230 + 10, y, 110, 32));
        }

        static bool TryParseCoord(string s, out double v) =>
            double.TryParse(s?.Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // ── Tab 1: Route (scrollable) ─────────────────────────────────────────

        void ComposeRoute(GuiComposer comp, ElementBounds body)
        {
            double x = 24, y = body.fixedY + 12;

            if (routePoints.Count == 0)
            {
                comp.AddStaticText(L.RouteNone, CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(x, y + 40, W - 48, 30));
                return;
            }

            comp.AddStaticText(L.RouteHeader(routePoints.Count),
                CairoFont.WhiteSmallText().WithFontSize(16),
                ElementBounds.Fixed(x, y, W - 48, 24));
            y += 32;

            const double ROW_H  = 32;
            const double BTN_W  = 110;
            const double BTN_H  = 26;

            int total     = routePoints.Count;
            int maxScroll = Math.Max(0, total - MAX_ROUTE_ROWS);
            scrollRoute   = Math.Clamp(scrollRoute, 0, maxScroll);

            double contentW = AddScrollArrows(comp, scrollRoute, maxScroll,
                y, MAX_ROUTE_ROWS, ROW_H,
                () => scrollRoute--,
                () => scrollRoute++,
                "rt-up", "rt-dn");

            double textW = contentW - BTN_W - 16 - x;

            int start = scrollRoute;
            int end   = Math.Min(total, start + MAX_ROUTE_ROWS);

            for (int i = start; i < end; i++)
            {
                var pt          = routePoints[i];
                int capturedIdx = pt.OriginalIndex;
                double ry = y + (i - start) * ROW_H;

                string kind  = pt.IsTeleport ? L.TagTl : "walk";
                string label = $"{i + 1}. {kind}  {pt.X:0}, {pt.Z:0}";
                if (pt.IsExcluded) label += "  " + L.TagSkip;
                if (pt.IsCurrent)  label += "  " + L.TagCurrent;

                var font = CairoFont.WhiteSmallText().WithFontSize(14);
                font.Color = pt.IsExcluded ? new double[] { 0.55, 0.55, 0.55, 1 }
                           : pt.IsCurrent  ? new double[] { 0.35, 1.0,  0.35, 1 }
                                           : new double[] { 1,    1,    1,    1 };

                comp.AddStaticText(label, font,
                    ElementBounds.Fixed(x, ry + 8, textW, 18));

                if (pt.IsTeleport)
                {
                    string btnLabel = pt.IsExcluded ? L.BtnRestore : L.BtnSkipTl;
                    comp.AddSmallButton(btnLabel, () =>
                    {
                        if (pt.IsExcluded) svc.RestoreWaypoint(capturedIdx);
                        else               svc.ExcludeWaypoint(capturedIdx);
                        Recompose(); return true;
                    }, ElementBounds.Fixed(contentW - BTN_W, ry + 3, BTN_W, BTN_H),
                       EnumButtonStyle.Small, "rt-wp-" + i);
                }
            }

            // Page indicator
            if (total > MAX_ROUTE_ROWS)
            {
                double py = y + MAX_ROUTE_ROWS * ROW_H + 4;
                comp.AddStaticText(L.PageOf(start + 1, end, total),
                    CairoFont.WhiteSmallText().WithFontSize(13),
                    ElementBounds.Fixed(x, py, 130, 20));
            }

            // Bottom action buttons
            double btnY = BODY_Y + BODY_H - 40;
            comp.AddButton(L.BtnRecalc,
                () => { svc.RecalculateWithExcludes(); Recompose(); return true; },
                ElementBounds.Fixed(x, btnY, 180, 30));
            comp.AddButton(L.BtnResetEx,
                () => { svc.ResetExcludes(); Recompose(); return true; },
                ElementBounds.Fixed(x + 190, btnY, 150, 30));
        }

        // ── Tab 2: Favourites (scrollable) ────────────────────────────────────

        void ComposeFavs(GuiComposer comp, ElementBounds body)
        {
            double x = 24, y = body.fixedY + 12;
            var favs = svc.GetFavourites();

            comp.AddStaticText(L.FavsHeader,
                CairoFont.WhiteSmallText().WithFontSize(16),
                ElementBounds.Fixed(x, y, W - 48, 24));
            y += 30;

            if (favs.Count == 0)
            {
                comp.AddStaticText(L.FavsNone, CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(x, y + 12, W - 48, 30));
                return;
            }

            int total     = favs.Count;
            int maxScroll = Math.Max(0, total - MAX_FAV_ROWS);
            scrollFavs    = Math.Clamp(scrollFavs, 0, maxScroll);

            const double ROW_H = 34;

            // Columns (right-aligned, fixed widths):
            // [name ........] [Go 60] [Del 65]  then scroll arrow
            double colDel  = W - SCROLL_W - 12 - 65;
            double colGo   = colDel - 8 - 60;
            double nameW   = colGo - x - 12;

            double contentW = AddScrollArrows(comp, scrollFavs, maxScroll,
                y, MAX_FAV_ROWS, ROW_H,
                () => scrollFavs--,
                () => scrollFavs++,
                "fv-up", "fv-dn");

            int start = scrollFavs;
            int end   = Math.Min(total, start + MAX_FAV_ROWS);

            for (int i = start; i < end; i++)
            {
                var fav = favs[i]; int idx = i;
                double ry = y + (i - start) * ROW_H;

                string label = string.IsNullOrEmpty(fav.Name)
                    ? $"{fav.DestX}, {fav.DestZ}"
                    : $"{fav.Name}  ({fav.DestX}, {fav.DestZ})";

                comp.AddStaticText(label, CairoFont.WhiteSmallText().WithFontSize(14),
                    ElementBounds.Fixed(x, ry + 9, nameW, 18));

                comp.AddSmallButton(L.BtnGo, () =>
                {
                    svc.FindRouteTo(fav.DestX, fav.DestZ);
                    svc.AddToHistory(fav.DestX, fav.DestZ);
                    activeTab = 1; Recompose(); return true;
                }, ElementBounds.Fixed(colGo, ry + 4, 60, 26), EnumButtonStyle.Small, $"fv-go-{i}");

                comp.AddSmallButton(L.BtnDel, () =>
                {
                    svc.DeleteFavourite(idx);
                    scrollFavs = Math.Max(0, scrollFavs - 1);
                    Recompose(); return true;
                }, ElementBounds.Fixed(colDel, ry + 4, 65, 26), EnumButtonStyle.Small, $"fv-dl-{i}");
            }

            if (total > MAX_FAV_ROWS)
            {
                double py = y + MAX_FAV_ROWS * ROW_H + 4;
                comp.AddStaticText(L.PageOf(start + 1, end, total),
                    CairoFont.WhiteSmallText().WithFontSize(13),
                    ElementBounds.Fixed(x, py, 130, 20));
            }
        }

        // ── Tab 3: History (scrollable) ───────────────────────────────────────

        void ComposeHistory(GuiComposer comp, ElementBounds body)
        {
            double x = 24, y = body.fixedY + 12;
            var hist = svc.GetHistory();

            comp.AddStaticText(L.HistHeader,
                CairoFont.WhiteSmallText().WithFontSize(16),
                ElementBounds.Fixed(x, y, W - 48, 24));
            y += 30;

            if (hist.Count == 0)
            {
                comp.AddStaticText(L.HistNone, CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(x, y + 12, W - 48, 28));
                return;
            }

            int total     = hist.Count;
            int maxScroll = Math.Max(0, total - MAX_HIST_ROWS);
            scrollHist    = Math.Clamp(scrollHist, 0, maxScroll);

            const double ROW_H = 32;

            // Columns: [name .......] [Go 60] [Save 75]  then scroll
            double colSave = W - SCROLL_W - 12 - 75;
            double colGo   = colSave - 8 - 60;
            double nameW   = colGo - x - 12;

            double contentW = AddScrollArrows(comp, scrollHist, maxScroll,
                y, MAX_HIST_ROWS, ROW_H,
                () => scrollHist--,
                () => scrollHist++,
                "ht-up", "ht-dn");

            int start = scrollHist;
            int end   = Math.Min(total, start + MAX_HIST_ROWS);

            for (int i = start; i < end; i++)
            {
                var h  = hist[i];
                double ry = y + (i - start) * ROW_H;

                string label = string.IsNullOrEmpty(h.Name)
                    ? $"#{i + 1}  {h.DestX}, {h.DestZ}"
                    : $"#{i + 1}  {h.Name}  ({h.DestX}, {h.DestZ})";

                comp.AddStaticText(label, CairoFont.WhiteSmallText().WithFontSize(14),
                    ElementBounds.Fixed(x, ry + 7, nameW, 18));

                comp.AddSmallButton(L.BtnGo, () =>
                {
                    svc.FindRouteTo(h.DestX, h.DestZ);
                    activeTab = 1; Recompose(); return true;
                }, ElementBounds.Fixed(colGo, ry + 3, 60, 26), EnumButtonStyle.Small, $"ht-go-{i}");

                comp.AddSmallButton(L.BtnSaveFav, () =>
                {
                    string name = string.IsNullOrEmpty(h.Name)
                        ? $"{h.DestX},{h.DestZ}" : h.Name;
                    svc.SaveFavouriteEntry(h.DestX, h.DestZ, name);
                    activeTab = 2; Recompose(); return true;
                }, ElementBounds.Fixed(colSave, ry + 3, 75, 26), EnumButtonStyle.Small, $"ht-sv-{i}");
            }

            if (total > MAX_HIST_ROWS)
            {
                double py = y + MAX_HIST_ROWS * ROW_H + 4;
                comp.AddStaticText(L.PageOf(start + 1, end, total),
                    CairoFont.WhiteSmallText().WithFontSize(13),
                    ElementBounds.Fixed(x, py, 130, 20));
            }

            double clrY = BODY_Y + BODY_H - 36;
            comp.AddSmallButton(L.BtnClearHist,
                () => { svc.ClearHistory(); scrollHist = 0; Recompose(); return true; },
                ElementBounds.Fixed(x, clrY, 150, 28), EnumButtonStyle.Small, "ht-clr");
        }

        // ── Tab 4: Settings ──────────────────────────────────────────────────

        void ComposeSettings(GuiComposer comp, ElementBounds body)
        {
            double x  = 24;
            double y  = body.fixedY + 18;
            double lw = 220;   // label width — enough for Russian
            double fw = 200;   // input field width
            double rh = 44;    // row height

            // ── Walk radius ──────────────────────────────────────────────────
            comp.AddStaticText(L.LblWalk, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lw, 24));
            comp.AddTextInput(ElementBounds.Fixed(x + lw + 8, y, fw, 30),
                v => inputWalk = v, CairoFont.WhiteSmallText(), "input-walk");
            comp.AddSmallButton(L.BtnApply, () =>
            {
                if (double.TryParse(inputWalk.Trim().Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                { svc.SetWalkRadius(r); capi.ShowChatMessage($"[tl] Walk radius = {r:0}"); }
                else capi.ShowChatMessage("[tl] Invalid value.");
                return true;
            }, ElementBounds.Fixed(x + lw + fw + 16, y, 90, 30), EnumButtonStyle.Small, "st-walk");
            y += rh;

            // ── GeoJSON URL ──────────────────────────────────────────────────
            comp.AddStaticText(L.LblUrl, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lw, 24));
            comp.AddTextInput(ElementBounds.Fixed(x + lw + 8, y, fw, 30),
                v => inputUrl = v, CairoFont.WhiteSmallText(), "input-url");
            comp.AddSmallButton(L.BtnSet, () => { svc.SetRemoteUrl(inputUrl); return true; },
                ElementBounds.Fixed(x + lw + fw + 16, y, 90, 30), EnumButtonStyle.Small, "st-url");
            y += rh;

            // ── Idle compass ─────────────────────────────────────────────────
            comp.AddStaticText(L.LblIdle, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lw, 24));
            bool idleOn = svc.IsIdleCompassOn;
            comp.AddSmallButton(L.ToggleOn, () =>
            {
                svc.SetIdleCompass(true); Recompose(); return true;
            }, ElementBounds.Fixed(x + lw + 8, y, 70, 30),
               idleOn ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "st-idle-on");
            comp.AddSmallButton(L.ToggleOff, () =>
            {
                svc.SetIdleCompass(false); Recompose(); return true;
            }, ElementBounds.Fixed(x + lw + 86, y, 70, 30),
               idleOn ? EnumButtonStyle.Small : EnumButtonStyle.Normal, "st-idle-off");
            y += rh;

            // ── Language ─────────────────────────────────────────────────────
            comp.AddStaticText(L.LblLang, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y + 6, lw, 24));
            bool isEn = L.Lang == GuiLang.English;
            comp.AddSmallButton("English", () =>
            {
                L.Lang = GuiLang.English; Recompose(); return true;
            }, ElementBounds.Fixed(x + lw + 8, y, 80, 30),
               isEn ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "st-en");
            comp.AddSmallButton("Русский", () =>
            {
                L.Lang = GuiLang.Russian; Recompose(); return true;
            }, ElementBounds.Fixed(x + lw + 96, y, 80, 30),
               isEn ? EnumButtonStyle.Small : EnumButtonStyle.Normal, "st-ru");
            y += rh;

            // ── Refresh ──────────────────────────────────────────────────────
            comp.AddButton(L.BtnRefresh, () => { svc.ForceRefresh(); return true; },
                ElementBounds.Fixed(x, y, 240, 32));
            y += rh + 4;

            comp.AddStaticText(L.Footer,
                CairoFont.WhiteSmallText().WithFontSize(13),
                ElementBounds.Fixed(x, y, W - 48, 22));
        }

        // ─────────────────────────────────────────────────────────────────────
        public override bool DisableMouseGrab => true;
        public override bool ShouldReceiveKeyboardEvents() => true;
    }

    // ── Shared data types ────────────────────────────────────────────────────

    public class RoutePointInfo
    {
        public int    OriginalIndex;
        public double X, Z;
        public bool   IsTeleport;
        public bool   IsCurrent;
        public bool   IsExcluded;
    }

    public class FavouriteEntry
    {
        public string Name  { get; set; }
        public int    DestX { get; set; }
        public int    DestZ { get; set; }
    }

    public class HistoryEntry
    {
        public int    DestX { get; set; }
        public int    DestZ { get; set; }
        public string Name  { get; set; } = "";
    }
}