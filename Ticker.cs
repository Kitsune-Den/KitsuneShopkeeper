using System;
using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// The per-frame work, deliberately decoupled from who calls it.
    ///
    /// This game is hostile to plugin-owned update loops: BaseUnityPlugin never
    /// gets Update at all, and a plugin-created GameObject gets disabled once the
    /// main scene loads (it is not destroyed, so OnDestroy never fires and the
    /// failure is completely silent). The reliable driver is a postfix on a game
    /// component that has to keep running for the game to work.
    ///
    /// Both drivers call in; the frame guard keeps the work happening once.
    /// </summary>
    internal static class Ticker
    {
        private static int _lastFrame = -1;

        private static float _heartbeatTimer;
        private static float _sweepTimer;
        private static string _lastDriver = "";

        public static void Tick(string driver)
        {
            Plugin.DiagOnce("tick-entry-" + driver, "Ticker.Tick entered via " + driver);

            int frame = Time.frameCount;
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            if (!Plugin.Ready)
            {
                Plugin.DiagOnce("tick-notready", "Ticker.Tick: config not ready");
                return;
            }

            if (_lastDriver != driver)
            {
                _lastDriver = driver;
                Plugin.Diag("tick driver: " + driver);
            }

            float dt = Time.deltaTime;

            Safely(DayStartReprice, "dayreprice");
            Safely(() => Heartbeat(dt), "heartbeat");
            Safely(() => Lights.Tick(dt), "lights");
            Safely(() => SafetySweep(dt), "sweep");
            Safely(() => Hotkeys(), "hotkeys");
        }

        private static void Safely(Action action, string label)
        {
            try { action(); }
            catch (Exception ex) { Plugin.DiagOnce("tick-" + label, $"{label} threw: {ex.Message}"); }
        }

        /// <summary>
        /// Runs the whole-shop reprice the day-start event asked for, on a normal
        /// frame rather than inside the game's event dispatch.
        /// </summary>
        private static void DayStartReprice()
        {
            if (!Plugin.DayStartRepricePending) return;
            Plugin.DayStartRepricePending = false;

            var result = Pricer.PriceEverything(includeCards: true, includeItems: true);

            Plugin.Log.LogInfo("New day: " + result);
            Plugin.Diag("day-start reprice -> " + result);
        }

        private static void Heartbeat(float dt)
        {
            if (!Plugin.DebugLogging.Value) return;

            _heartbeatTimer += dt;
            if (_heartbeatTimer < 30f) return;
            _heartbeatTimer = 0f;

            string clock;
            try
            {
                clock = GameClock.TryRead(out int h, out int m, out bool on)
                    ? $"{h:00}:{m:00} shopLightOn={on}"
                    : "no LightManager resolved";

                // List every manager and what each reads. If one advances and another
                // does not, that tells us which object the game actually drives.
                try { clock += "  [" + GameClock.Describe() + "]"; }
                catch { clock += "  [describe failed]"; }

                // Deliberately NOT calling LightManager.GetHasDayStarted() or any
                // other static helper here: they resolve through
                // CSingleton<LightManager>.Instance, and touching that before the
                // game scene exists spawns a phantom manager that permanently
                // shadows the real one.
            }
            catch (Exception ex)
            {
                clock = "clock unavailable (" + ex.GetType().Name + ")";
            }

            Plugin.Diag("heartbeat: " + clock);
        }

        private static void SafetySweep(float dt)
        {
            float interval = Plugin.SafetySweepSeconds.Value;
            if (interval <= 0f) return;

            _sweepTimer += dt;
            if (_sweepTimer < interval) return;
            _sweepTimer = 0f;

            var result = Pricer.SweepUnpriced();
            if (result.CardsPriced > 0 || result.ItemsPriced > 0)
            {
                Plugin.Log.LogInfo("Safety sweep: " + result);
                Plugin.Diag("safety sweep -> " + result);
            }
        }

        private static void Hotkeys()
        {
            if (Plugin.KeyConfigPanel.Value.IsDown()) { ConfigPanel.Toggle(); return; }

            // Pricing hotkeys stay quiet while the panel is open, so typing into
            // one of its boxes cannot kick off a reprice.
            if (ConfigPanel.IsOpen) return;

            if (Plugin.KeyPriceAll.Value.IsDown()) Report(Pricer.PriceEverything(true, true));
            else if (Plugin.KeyPriceCards.Value.IsDown()) Report(Pricer.PriceEverything(true, false));
            else if (Plugin.KeyPriceItems.Value.IsDown()) Report(Pricer.PriceEverything(false, true));
        }

        private static void Report(Pricer.RunResult result)
        {
            Plugin.Log.LogInfo("Hotkey: " + result);
            Plugin.Diag("hotkey run -> " + result);
        }
    }
}
