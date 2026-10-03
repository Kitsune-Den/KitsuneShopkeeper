using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Reads the shop clock from the LightManager that is actually running.
    ///
    /// The game's own static helpers all go through CSingleton&lt;LightManager&gt;.Instance,
    /// which can still point at the title screen's manager after a save loads. That
    /// object persists but never advances its clock, so those helpers report a
    /// permanent 08:00 and ToggleShopLight acts on scenery nobody can see.
    ///
    /// Fields are read by reflection because they are private on the manager.
    /// </summary>
    internal static class GameClock
    {
        private static readonly AccessTools.FieldRef<LightManager, int> HourRef =
            AccessTools.FieldRefAccess<LightManager, int>("m_TimeHour");

        private static readonly AccessTools.FieldRef<LightManager, int> MinuteRef =
            AccessTools.FieldRefAccess<LightManager, int>("m_TimeMin");

        private static readonly AccessTools.FieldRef<LightManager, bool> ShopLightRef =
            AccessTools.FieldRefAccess<LightManager, bool>("m_IsShopLightOn");

        private static LightManager[] _managers;
        private static float _lastScan = -999f;

        /// <summary>Force a rescan, e.g. after a scene load.</summary>
        public static void Invalidate()
        {
            _managers = null;
            _lastScan = -999f;
        }

        /// <summary>
        /// The manager whose clock is furthest along, re-evaluated on every call.
        ///
        /// Caching the choice was a mistake: before the shop opens every manager
        /// reads 08:00, so the pick is arbitrary, and a cached dead one never
        /// reveals itself once the real clock starts moving. The object list is
        /// cached briefly (FindObjectsOfType is not cheap) but which one wins is
        /// decided fresh each time.
        /// </summary>
        public static LightManager Resolve()
        {
            if (_managers == null || Time.realtimeSinceStartup - _lastScan > 5f)
            {
                _managers = Object.FindObjectsOfType<LightManager>();
                _lastScan = Time.realtimeSinceStartup;
            }

            if (_managers == null || _managers.Length == 0) return null;

            LightManager best = null;
            int bestTime = -1;

            for (int i = 0; i < _managers.Length; i++)
            {
                var m = _managers[i];
                if (m == null) continue;

                int t = HourRef(m) * 60 + MinuteRef(m);
                if (t > bestTime)
                {
                    bestTime = t;
                    best = m;
                }
            }

            return best;
        }

        /// <summary>Every manager and what it currently reads, for diagnosis.</summary>
        public static string Describe()
        {
            var all = Object.FindObjectsOfType<LightManager>();
            if (all == null || all.Length == 0) return "no LightManager";

            return string.Join(" | ", all.Select(m =>
                $"{m.gameObject.scene.name}:{HourRef(m):00}:{MinuteRef(m):00}" +
                $"{(m.isActiveAndEnabled ? "" : " (disabled)")}"));
        }

        public static bool TryRead(out int hour, out int minute, out bool shopLightOn)
        {
            hour = 0;
            minute = 0;
            shopLightOn = false;

            var m = Resolve();
            if (m == null) return false;

            hour = HourRef(m);
            minute = MinuteRef(m);
            shopLightOn = ShopLightRef(m);
            return true;
        }

        public static bool ToggleShopLight()
        {
            var m = Resolve();
            if (m == null) return false;

            m.ToggleShopLight();
            return true;
        }
    }
}
