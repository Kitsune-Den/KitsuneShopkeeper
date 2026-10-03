using System;
using System.Reflection;
using HarmonyLib;

namespace KitsunePricer
{
    /// <summary>
    /// Placement hooks.
    ///
    /// These target methods on the compartment itself, so they run no matter who
    /// filled it. SetCardOnShelf is called both from the player's OnMouseButtonUp
    /// and from the worker's restock routine, which is why hooking it catches
    /// employee-placed stock that input-event hooks miss.
    ///
    /// Patches are applied explicitly rather than via PatchAll so that a target
    /// going missing is reported instead of silently doing nothing.
    /// </summary>
    internal static class Patches
    {
        public static int Applied { get; private set; }

        public static void Apply(Harmony harmony)
        {
            Applied = 0;

            Bind(harmony, typeof(InteractableCardCompartment), "SetCardOnShelf", nameof(CardPlaced));
            Bind(harmony, typeof(ShelfCompartment), "AddItem", nameof(ItemAdded));
            Bind(harmony, typeof(ShelfCompartment), "SpawnItem", nameof(ItemSpawned));

            BindPrefix(harmony, typeof(CustomerTradeCardScreen), "OnPressAccept",
                typeof(TradeGuard), nameof(TradeGuard.OnPressAccept));

            // Probe: a postfix on the same method. If this fires and the prefix does
            // not, the fault is in the prefix signature rather than the patch target.
            Bind(harmony, typeof(CustomerTradeCardScreen), "OnPressAccept", nameof(TradeAcceptProbe));

            // Probes: who else writes prices. Build 25381942 restockers price any slot
            // whose tag reads unpriced at market x their own multiplier, which
            // replaces ours without a trace. These log the first such write per
            // caller and item so an overwrite is visible instead of silent.
            BindPrefix(harmony, typeof(CPlayerData), "SetItemPrice", typeof(Patches), nameof(ItemPriceWrite));
            BindPrefix(harmony, typeof(CPlayerData), "SetCardPrice", typeof(Patches), nameof(CardPriceWrite));

            // No smelly customers. Applied unconditionally and gated at runtime, so
            // the toggle works live from a config manager without a restart.
            BindPrefix(harmony, typeof(Customer), "SetSmelly", typeof(SmellGuard), nameof(SmellGuard.BlockSetSmelly));
            Bind(harmony, typeof(Customer), "LoadCustomerSaveData", typeof(SmellGuard), nameof(SmellGuard.AfterLoad));

            // Reduced wages. Same pattern: always applied, gated at runtime.
            Bind(harmony, typeof(WorkerManager), "GetTotalSalaryCost", typeof(WageGuard), nameof(WageGuard.ScaleTotal));
            Bind(harmony, typeof(WorkerData), "GetSalaryCostText", typeof(WageGuard), nameof(WageGuard.ScaleText));

            // DO NOT patch LightManager.Update.
            //
            // It was tried as a per-frame driver and it broke the game: the clock
            // stopped displaying in the HUD and "press to start next day" stopped
            // responding, stranding the save at the end of the day. The patch
            // reported as applied yet the postfix never fired once, which is the
            // signature of the wrapper failing and taking EvaluateTimeClock and the
            // day transition down with it. The tick is driven instead by a component
            // attached to the light manager's GameObject, which needs no patching.

            Plugin.Diag($"patches applied: {Applied}/11");
        }

        private static void Bind(Harmony harmony, Type target, string methodName, string postfixName)
            => Bind(harmony, target, methodName, typeof(Patches), postfixName);

        private static void Bind(Harmony harmony, Type target, string methodName,
            Type patchHost, string postfixName)
        {
            try
            {
                MethodInfo original = AccessTools.Method(target, methodName);
                if (original == null)
                {
                    Plugin.Diag($"PATCH MISS: {target.Name}.{methodName} not found");
                    return;
                }

                MethodInfo postfix = patchHost.GetMethod(
                    postfixName, BindingFlags.Public | BindingFlags.Static);

                harmony.Patch(original, postfix: new HarmonyMethod(postfix));

                Applied++;
                Plugin.Diag($"patched {target.Name}.{methodName}");
            }
            catch (Exception ex)
            {
                Plugin.Diag($"PATCH FAIL {target.Name}.{methodName}: {ex.Message}");
            }
        }

        private static void BindPrefix(Harmony harmony, Type target, string methodName,
            Type patchHost, string prefixName)
        {
            try
            {
                MethodInfo original = AccessTools.Method(target, methodName);
                if (original == null)
                {
                    Plugin.Diag($"PATCH MISS: {target.Name}.{methodName} not found");
                    return;
                }

                MethodInfo prefix = patchHost.GetMethod(
                    prefixName, BindingFlags.Public | BindingFlags.Static);

                harmony.Patch(original, prefix: new HarmonyMethod(prefix));

                Applied++;
                Plugin.Diag($"patched (prefix) {original.DeclaringType?.FullName}.{original.Name} " +
                            $"[params={original.GetParameters().Length}, token={original.MetadataToken}]");
            }
            catch (Exception ex)
            {
                Plugin.Diag($"PATCH FAIL {target.Name}.{methodName}: {ex.Message}");
            }
        }

        public static void TradeAcceptProbe()
        {
            Plugin.Diag("PROBE: OnPressAccept postfix fired");
        }

        public static void ItemPriceWrite(EItemType itemType, float price)
        {
            if (Pricer.Writing) return;
            try
            {
                string caller = ExternalCaller();
                Plugin.DiagOnce("extwrite-item-" + caller + "-" + itemType,
                    $"EXTERNAL price write: {itemType} = {price:0.00} by {caller}");
            }
            catch { /* a probe must never block the game's own price write */ }
        }

        public static void CardPriceWrite(CardData cardData, float priceSet)
        {
            if (Pricer.Writing) return;
            try
            {
                string caller = ExternalCaller();
                string card = cardData == null ? "?" : $"{cardData.monsterType}/{cardData.expansionType}/g{cardData.cardGrade}";
                Plugin.DiagOnce("extwrite-card-" + caller + "-" + card,
                    $"EXTERNAL card price write: {card} = {priceSet:0.00} by {caller}");
            }
            catch { /* as above */ }
        }

        /// <summary>First game method on the stack that is not the patched setter itself.</summary>
        private static string ExternalCaller()
        {
            var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
            if (frames == null) return "?";

            foreach (var f in frames)
            {
                var m = f.GetMethod();
                var t = m?.DeclaringType;
                if (t == null || t == typeof(Patches) || t == typeof(CPlayerData)) continue;
                if (m.Name.Contains("DMD")) continue;
                return t.Name + "." + m.Name;
            }
            return "?";
        }

        public static void CardPlaced(InteractableCardCompartment __instance)
        {
            if (!Plugin.Ready || !Plugin.PriceOnPlacement.Value) return;

            // Nothing may escape a postfix. An exception here propagates into the
            // game's own SetCardOnShelf and stops cards being placed at all, which
            // is a far worse outcome than a card going unpriced. Content from other
            // mods is exactly what throws in unexpected places.
            try
            {
                bool priced = Pricer.PriceCardCompartment(__instance);
                Trace("card placed -> priced=" + priced);
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("hook-cardplaced", "CardPlaced hook threw (suppressed): " + ex);
            }
        }

        public static void ItemAdded(ShelfCompartment __instance)
        {
            if (!Plugin.Ready || !Plugin.PriceOnPlacement.Value) return;

            try
            {
                bool priced = Pricer.PriceItemCompartment(__instance);
                Trace("item added -> priced=" + priced);
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("hook-itemadded", "ItemAdded hook threw (suppressed): " + ex);
            }
        }

        /// <summary>
        /// Restocking spawns items straight into the compartment rather than going
        /// through AddItem, so it needs its own hook.
        /// </summary>
        public static void ItemSpawned(ShelfCompartment __instance)
        {
            if (!Plugin.Ready || !Plugin.PriceOnPlacement.Value) return;

            try
            {
                bool priced = Pricer.PriceItemCompartment(__instance);
                Trace("item spawned -> priced=" + priced);
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("hook-itemspawned", "ItemSpawned hook threw (suppressed): " + ex);
            }
        }

        /// <summary>
        /// Traces that a hook fired at all, separately from whether it produced a
        /// price. Without this a silent decline is indistinguishable from the
        /// patch never running.
        /// </summary>
        private static void Trace(string message)
        {
            if (Plugin.Ready && Plugin.DebugLogging.Value)
                Plugin.Diag(message);
        }
    }
}
