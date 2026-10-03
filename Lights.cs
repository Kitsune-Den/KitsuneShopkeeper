using System;

namespace KitsunePricer
{
    /// <summary>
    /// Turns the shop lights on and off at configured hours.
    ///
    /// Deliberately edge triggered: it acts only on the tick where the clock
    /// crosses a configured hour, never continuously. If it enforced the desired
    /// state every frame you could not flip the switch yourself without the mod
    /// immediately flipping it back.
    /// </summary>
    internal static class Lights
    {
        private const float CheckInterval = 0.5f;

        private static float _timer;
        private static int _lastHour = -1;
        private static bool _reportedClockFailure;

        public static void Tick(float deltaTime)
        {
            if (!Plugin.Ready || !Plugin.AutoLights.Value) return;

            _timer += deltaTime;
            if (_timer < CheckInterval) return;
            _timer = 0f;

            int hour;
            try
            {
                if (!GameClock.TryRead(out hour, out _, out _))
                {
                    Plugin.DiagOnce("lights-noclock", "lights: no LightManager resolved yet");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!_reportedClockFailure)
                {
                    _reportedClockFailure = true;
                    Plugin.Diag("lights: clock unavailable - " + ex.Message);
                }
                return;
            }

            // First observation of a day or session. Apply the correct state once
            // rather than only seeding, otherwise loading a save that is already
            // past the switch-on hour leaves the lights off until tomorrow.
            if (_lastHour < 0)
            {
                _lastHour = hour;
                Trace($"lights: seeded at hour {hour}, shopLightOn={SafeIsOn()}");

                if (ShouldBeOn(hour)) SetLights(true);
                return;
            }

            if (hour == _lastHour) return;

            int previous = _lastHour;
            _lastHour = hour;

            Trace($"lights: hour {previous} -> {hour}, shopLightOn={SafeIsOn()}");

            if (CrossedHour(previous, hour, Plugin.LightsOnHour.Value))
                SetLights(true);
            else if (Plugin.LightsOffHour.Value >= 0 && CrossedHour(previous, hour, Plugin.LightsOffHour.Value))
                SetLights(false);
        }

        /// <summary>Whether the lights should be on at this hour, handling a window that wraps midnight.</summary>
        private static bool ShouldBeOn(int hour)
        {
            int on = Plugin.LightsOnHour.Value;
            int off = Plugin.LightsOffHour.Value;

            if (off < 0) return hour >= on;

            return on <= off ? (hour >= on && hour < off) : (hour >= on || hour < off);
        }

        private static string SafeIsOn()
        {
            try
            {
                return GameClock.TryRead(out _, out _, out bool on) ? on.ToString() : "?";
            }
            catch { return "?"; }
        }

        private static void Trace(string message)
        {
            if (Plugin.Ready && Plugin.DebugLogging.Value)
                Plugin.Diag(message);
        }

        /// <summary>
        /// True when the clock moved onto <paramref name="target"/>. Handles the
        /// clock skipping an hour (time can move fast) and wrapping past midnight.
        /// </summary>
        private static bool CrossedHour(int previous, int current, int target)
        {
            if (current == target) return true;

            // Walk forward from previous to current; if target is in that span the
            // transition was skipped over rather than landed on.
            int cursor = previous;
            for (int guard = 0; guard < 24; guard++)
            {
                cursor = (cursor + 1) % 24;
                if (cursor == target) return true;
                if (cursor == current) break;
            }

            return false;
        }

        private static void SetLights(bool on)
        {
            try
            {
                if (!GameClock.TryRead(out int hour, out _, out bool currentlyOn)) return;
                if (currentlyOn == on) return;

                if (!GameClock.ToggleShopLight()) return;

                Plugin.Log.LogInfo($"Shop lights {(on ? "on" : "off")} at {hour:00}:00.");
                Plugin.Diag($"lights {(on ? "on" : "off")} at hour {hour}");
            }
            catch (Exception ex)
            {
                Plugin.Diag("light toggle failed: " + ex.Message);
            }
        }

        /// <summary>Called on day rollover so a new day re-seeds cleanly.</summary>
        public static void ResetForNewDay()
        {
            _lastHour = -1;
        }
    }
}
