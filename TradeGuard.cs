using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Fat-finger protection when buying a card from a customer.
    ///
    /// An extra zero in the offer box is an expensive and completely silent
    /// mistake: the game takes the money without comment. This blocks the first
    /// press when the offer is wildly above market and explains why. Pressing
    /// accept again puts the deal through, so a deliberate overpay still works.
    /// </summary>
    internal static class TradeGuard
    {
        private static float _armedForPrice = -1f;

        /// <summary>
        /// Harmony prefix. Returning false swallows the click.
        /// </summary>
        public static bool OnPressAccept(
            CustomerTradeCardScreen __instance,
            bool ___m_IsTrading,
            bool ___m_HasAccepted,
            float ___m_PriceSet,
            float ___m_SellCardMarketPrice,
            CardData ___m_CardData_L)
        {
            // Logged every press, not once: we need to see both that the hook runs
            // at all and what it actually reads, because a plausible-looking prefix
            // that silently sees zeroes is indistinguishable from one never called.
            Plugin.Diag($"trade accept: trading={___m_IsTrading} accepted={___m_HasAccepted} " +
                        $"offer={___m_PriceSet:0.00} marketField={___m_SellCardMarketPrice:0.00}");

            try { return Evaluate(__instance, ___m_IsTrading, ___m_HasAccepted, ___m_PriceSet, ___m_SellCardMarketPrice, ___m_CardData_L); }
            catch (System.Exception ex)
            {
                // Fail open. A prefix that throws would block the accept button
                // outright, leaving the player unable to complete any trade at all.
                Plugin.DiagOnce("tradeguard-threw", "TradeGuard threw (failing open): " + ex);
                return true;
            }
        }

        private static bool Evaluate(
            CustomerTradeCardScreen __instance,
            bool ___m_IsTrading,
            bool ___m_HasAccepted,
            float ___m_PriceSet,
            float ___m_SellCardMarketPrice,
            CardData ___m_CardData_L)
        {
            if (!Plugin.Ready || !Plugin.TradeGuardEnabled.Value) return true;

            // Already agreed, or a card-for-card swap: no cash at risk.
            if (___m_HasAccepted || ___m_IsTrading) return true;

            float offer = ___m_PriceSet;
            float market = ___m_SellCardMarketPrice;

            // The cached field is not always populated for every trade flow. Fall
            // back to asking the game directly, otherwise a zero market silently
            // disables the multiplier check and the guard waves everything through.
            if (market <= 0f && ___m_CardData_L != null)
            {
                try { market = CPlayerData.GetCardMarketPrice(___m_CardData_L); }
                catch { /* modded card with no price entry; absolute check still applies */ }
            }

            if (offer <= 0f) return true;

            if (!IsSuspicious(offer, market))
            {
                Plugin.Diag($"trade guard: allowed {offer:0.00} vs market {market:0.00}");
                _armedForPrice = -1f;
                return true;
            }

            // Second press on the same amount means they meant it.
            if (Mathf.Approximately(_armedForPrice, offer))
            {
                _armedForPrice = -1f;
                Plugin.Diag($"trade guard: confirmed overpay {offer:0.00} vs market {market:0.00}");
                return true;
            }

            _armedForPrice = offer;
            Warn(__instance, offer, market);
            return false;
        }

        private static bool IsSuspicious(float offer, float market)
        {
            if (Plugin.TradeGuardAbsolute.Value > 0f && offer >= Plugin.TradeGuardAbsolute.Value)
                return true;

            if (market > 0f && Plugin.TradeGuardMultiplier.Value > 0f
                && offer >= market * Plugin.TradeGuardMultiplier.Value)
                return true;

            return false;
        }

        private static void Warn(CustomerTradeCardScreen screen, float offer, float market)
        {
            Plugin.Log.LogInfo($"Trade guard blocked an offer of {offer:0.00} against market {market:0.00}.");
            Plugin.Diag($"trade guard: BLOCKED {offer:0.00} vs market {market:0.00}");

            try
            {
                SoundManager.GenericCancel();
            }
            catch { /* sound is optional */ }

            try
            {
                if (screen.m_CustomerTopText == null) return;

                string marketText = market > 0f ? $"{market:0.00}" : "unknown";
                screen.m_CustomerTopText.text =
                    $"<color=#FF5555>Hold on. You are offering {offer:0.00} for a card worth {marketText}. " +
                    $"Press accept again if you really mean it.</color>";

                if (screen.m_CustomerTopTextAnim != null)
                {
                    screen.m_CustomerTopTextAnim.Rewind();
                    screen.m_CustomerTopTextAnim.Play();
                }
            }
            catch
            {
                // If the UI shape changed, the block itself still protected the money.
            }
        }
    }
}
