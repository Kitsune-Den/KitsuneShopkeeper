namespace KitsunePricer
{
    /// <summary>
    /// Optional: reduced or no employee wages.
    ///
    /// The daily employee bill is accrued in exactly one place: RentBillScreen,
    /// at day end, adds WorkerManager.GetTotalSalaryCost() to the Employee bill.
    /// Scaling that return value scales every wage that will ever be billed, with
    /// no save data touched, so switching the option off restores normal wages
    /// from the next day on.
    ///
    /// WorkerData.costPerDay itself is left alone. It also prices the one-off
    /// salary bonus, and the worker data lives on a shared asset, so writing to it
    /// would compound every time the setting changed.
    /// </summary>
    internal static class WageGuard
    {
        /// <summary>Multiplier applied to wages, 1 when the option is off.</summary>
        internal static float Scale
        {
            get
            {
                if (!Plugin.Ready || !Plugin.ReducedWages.Value) return 1f;
                return UnityEngine.Mathf.Clamp(Plugin.WagePercent.Value, 0f, 100f) / 100f;
            }
        }

        /// <summary>Harmony postfix on WorkerManager.GetTotalSalaryCost.</summary>
        public static void ScaleTotal(ref float __result)
        {
            try
            {
                float scale = Scale;
                if (scale >= 1f) return;

                float full = __result;
                __result = full * scale;

                // Only called when the day-end bill is worked out, so this logs
                // once per day: the one place a wage change can be checked.
                Plugin.Diag($"wages: {full:0.00} -> {__result:0.00} ({scale * 100f:0}%)");
            }
            catch { /* fail to normal wages rather than break the day-end bill */ }
        }

        /// <summary>
        /// Harmony postfix on WorkerData.GetSalaryCostText, so the hire screen and
        /// the worker panel show what will actually be billed.
        /// </summary>
        public static void ScaleText(WorkerData __instance, ref string __result)
        {
            try
            {
                float scale = Scale;
                if (scale >= 1f) return;

                __result = GameInstance.GetPriceString(__instance.costPerDay * scale) + "/" +
                           I2.Loc.LocalizationManager.GetTranslation("day");
            }
            catch { /* keep the game's own text */ }
        }
    }
}
