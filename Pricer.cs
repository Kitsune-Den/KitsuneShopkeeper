using System;
using System.Collections.Generic;
using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Game-facing side: walks the shop floor, classifies stock, applies
    /// <see cref="PricingEngine"/> and writes prices back.
    /// </summary>
    internal static class Pricer
    {
        public struct RunResult
        {
            public int CardsPriced;
            public int ItemsPriced;
            public int Skipped;

            public override string ToString()
                => $"priced {CardsPriced} card slot(s), {ItemsPriced} item slot(s), skipped {Skipped}.";
        }

        /// <summary>
        /// Item types whose name starts with one of these is treated as a bulk box.
        /// Matching on the name rather than a fixed set of enum members is deliberate:
        /// the game appends new item types over time (the graded BulkBox_*Grade** entries
        /// were added well after the originals), and anything hardcoded silently
        /// mis-prices the new ones as ordinary stock.
        /// </summary>
        private static readonly string[] BulkBoxPrefixes = { "BulkBox" };

        // ---------------------------------------------------------------- sweeps

        public static RunResult PriceEverything(bool includeCards, bool includeItems)
        {
            var result = new RunResult();

            // Never touch shelf data outside a loaded shop. See GameScene: doing so
            // spawns a phantom ShelfManager and breaks furniture purchases for the
            // rest of the session.
            if (!GameScene.Ready) return result;

            _skipReasons.Clear();

            if (includeItems) PriceAllItems(ref result);
            if (includeCards) PriceAllCards(ref result);

            if (Plugin.DebugLogging.Value && _skipReasons.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in _skipReasons) parts.Add(kv.Key + "=" + kv.Value);
                Plugin.Diag("skip reasons: " + string.Join(", ", parts.ToArray()));
            }

            return result;
        }

        /// <summary>
        /// Why slots were skipped on the current whole-shop run. A skip count on its
        /// own cannot distinguish an empty slot from a card we failed to read.
        /// </summary>
        private static readonly Dictionary<string, int> _skipReasons = new Dictionary<string, int>();

        private static bool Skip(string reason)
        {
            _skipReasons.TryGetValue(reason, out int n);
            _skipReasons[reason] = n + 1;
            return false;
        }

        /// <summary>
        /// True while this mod is writing a price, so the write probe on
        /// CPlayerData can tell our writes from the game's own.
        /// </summary>
        internal static bool Writing;

        /// <summary>
        /// Prices only slots that currently carry no price, and touches nothing
        /// else. Runs on a timer as a safety net so unpriced stock cannot sit on
        /// the shelf indefinitely if the day-start event or a hotkey never lands.
        /// </summary>
        public static RunResult SweepUnpriced()
        {
            var result = new RunResult();

            // Same guard as PriceEverything, and this is the one that actually bit:
            // the sweep runs on a timer, so it fires at the title screen if the
            // player lingers there longer than the interval.
            if (!GameScene.Ready) return result;

            try
            {
                foreach (var comps in CardCompartmentLists())
                {
                    for (int j = 0; j < comps.Count; j++)
                    {
                        var c = comps[j];
                        if (c == null || c.HasSetPrice()) continue;
                        if (c.m_StoredCardList == null || c.m_StoredCardList.Count == 0) continue;

                        if (PriceCardCompartment(c)) result.CardsPriced++;
                    }
                }

                foreach (var comps in ItemCompartmentLists())
                {
                    for (int j = 0; j < comps.Count; j++)
                    {
                        var c = comps[j];
                        if (c == null || c.HasSetPrice()) continue;
                        if (c.GetItemType() == EItemType.None) continue;

                        if (PriceItemCompartment(c)) result.ItemsPriced++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Diag("sweep failed: " + ex.Message);
            }

            return result;
        }

        private static void PriceAllItems(ref RunResult result)
        {
            try
            {
                foreach (var compartments in ItemCompartmentLists())
                {
                    for (int j = 0; j < compartments.Count; j++)
                    {
                        if (PriceItemCompartment(compartments[j])) result.ItemsPriced++;
                        else result.Skipped++;
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not read shelf list: " + ex.Message); }
        }

        private static void PriceAllCards(ref RunResult result)
        {
            try
            {
                foreach (var compartments in CardCompartmentLists())
                {
                    for (int j = 0; j < compartments.Count; j++)
                    {
                        if (PriceCardCompartment(compartments[j])) result.CardsPriced++;
                        else result.Skipped++;
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not read card shelf list: " + ex.Message); }
        }

        // ------------------------------------------------------- shelf walking

        /// <summary>
        /// Item compartments on every for-sale shelf, including card/item combo shelves.
        ///
        /// CardItemCombiShelf (game build 25381942+) registers only in its own
        /// ShelfManager list. It derives from CardShelf but its Awake skips
        /// CardShelf.Awake, so it is in neither GetShelfList nor GetCardShelfList,
        /// and walking just those two leaves every combo shelf unpriced after a load.
        /// TournamentPrizeShelf derives from it and lands in the same list; it is
        /// flagged not-for-sale, which the m_ItemNotForSale check already skips.
        /// </summary>
        private static IEnumerable<List<ShelfCompartment>> ItemCompartmentLists()
        {
            var shelves = ShelfManager.GetShelfList();
            if (shelves != null)
            {
                for (int i = 0; i < shelves.Count; i++)
                {
                    var shelf = shelves[i];
                    if (shelf == null || !shelf.IsValidObject() || shelf.m_ItemNotForSale) continue;

                    var compartments = shelf.GetItemCompartmentList();
                    if (compartments != null) yield return compartments;
                }
            }

            var combis = ShelfManager.GetCardItemCombiShelfList();
            if (combis != null)
            {
                for (int i = 0; i < combis.Count; i++)
                {
                    var shelf = combis[i];
                    if (shelf == null || !shelf.IsValidObject() || shelf.m_ItemNotForSale) continue;

                    var compartments = shelf.GetItemCompartmentList();
                    if (compartments != null) yield return compartments;
                }
            }
        }

        /// <summary>Card compartments on every for-sale card shelf, including combo shelves.</summary>
        private static IEnumerable<List<InteractableCardCompartment>> CardCompartmentLists()
        {
            var shelves = ShelfManager.GetCardShelfList();
            if (shelves != null)
            {
                for (int i = 0; i < shelves.Count; i++)
                {
                    var shelf = shelves[i];
                    if (shelf == null || !shelf.IsValidObject() || shelf.m_ItemNotForSale) continue;

                    var compartments = shelf.GetCardCompartmentList();
                    if (compartments != null) yield return compartments;
                }
            }

            var combis = ShelfManager.GetCardItemCombiShelfList();
            if (combis != null)
            {
                for (int i = 0; i < combis.Count; i++)
                {
                    var shelf = combis[i];
                    if (shelf == null || !shelf.IsValidObject() || shelf.m_ItemNotForSale) continue;

                    var compartments = shelf.GetCardCompartmentList();
                    if (compartments != null) yield return compartments;
                }
            }
        }

        // ------------------------------------------------------------ single slot

        public static bool PriceItemCompartment(ShelfCompartment compartment, bool force = false)
        {
            if (compartment == null) return Skip("item-null");

            EItemType itemType;
            try { itemType = compartment.GetItemType(); }
            catch { return Skip("item-type-threw"); }

            if (itemType == EItemType.None) return Skip("item-empty");

            if (!force && compartment.HasSetPrice() && !Plugin.RepriceAlreadyPriced.Value)
                return Skip("item-already-priced");

            float market;
            try { market = CPlayerData.GetItemMarketPrice(itemType); }
            catch (Exception ex)
            {
                Debug("Market price lookup failed for " + itemType + ": " + ex.Message);
                return Skip("item-market-threw");
            }

            if (market <= 0f) return Skip("item-market-zero");
            float markup = IsBulkBox(itemType) ? Plugin.BulkBoxMarkup.Value : Plugin.ItemMarkup.Value;

            float price = PricingEngine.Compute(market, new PricingEngine.Rules
            {
                MarkupPercent = markup,
                RoundToNearest = Plugin.ItemRoundTo.Value,
                Mode = Plugin.RoundingMode.Value,
                NeverBelowMarket = Plugin.NeverBelowMarket.Value,
                AbsoluteMinPrice = Plugin.AbsoluteMinPrice.Value
            });

            if (price <= 0f) return false;

            price = NormalizeForCurrency(price);

            try
            {
                Writing = true;
                CPlayerData.SetItemPrice(itemType, price);
            }
            catch (Exception ex)
            {
                Debug("SetItemPrice failed for " + itemType + ": " + ex.Message);
                return Skip("item-set-threw");
            }
            finally { Writing = false; }

            MarkPriced(compartment.m_InteractablePriceTagList);

            // Same as cards: show the new price now rather than when the queued
            // price-changed event gets round to this tag.
            try { compartment.SetPriceTagItemPriceText(); } catch { /* cosmetic */ }

            Debug($"{itemType}: market {market:0.00} -> {price:0.00} (markup {markup}%)");
            return true;
        }

        public static bool PriceCardCompartment(InteractableCardCompartment compartment, bool force = false)
        {
            if (compartment == null) return Skip("card-null");
            if (compartment.m_StoredCardList == null || compartment.m_StoredCardList.Count == 0) return Skip("card-empty");

            if (!force && compartment.HasSetPrice() && !Plugin.RepriceAlreadyPriced.Value)
                return Skip("card-already-priced");

            CardData cardData = ResolveCardData(compartment, out string why);
            if (cardData == null) return Skip("card-no-data:" + why);

            float market;
            try { market = CPlayerData.GetCardMarketPrice(cardData); }
            catch (Exception ex)
            {
                // Modded or not-yet-generated cards can throw here. Skipping is
                // always better than writing a wrong price onto real stock.
                Debug("Card market lookup failed: " + ex.Message);
                return Skip("card-market-threw");
            }

            if (market <= 0f) return Skip("card-market-zero");
            bool graded = cardData.cardGrade > 0;

            float markup = graded ? Plugin.GradedCardMarkup.Value : Plugin.CardMarkup.Value;
            markup = PricingEngine.SelectCardMarkup(
                market,
                markup,
                Plugin.HighValueTierEnabled.Value,
                Plugin.HighValueThreshold.Value,
                Plugin.HighValueMarkup.Value);

            float price = PricingEngine.Compute(market, new PricingEngine.Rules
            {
                MarkupPercent = markup,
                RoundToNearest = Plugin.CardRoundTo.Value,
                Mode = Plugin.RoundingMode.Value,
                NeverBelowMarket = Plugin.NeverBelowMarket.Value,
                AbsoluteMinPrice = Plugin.AbsoluteMinPrice.Value
            });

            if (price <= 0f) return false;

            price = NormalizeForCurrency(price);

            try
            {
                Writing = true;
                CPlayerData.SetCardPrice(cardData, price);
            }
            catch (Exception ex)
            {
                Debug("SetCardPrice failed: " + ex.Message);
                return Skip("card-set-threw");
            }
            finally { Writing = false; }

            MarkPriced(compartment.m_InteractablePriceTagList);

            // Re-read the price we just wrote onto the tag. RefreshPriceTagItemPriceText
            // looked right and was a no-op: it re-renders the tag's cached price, which
            // SetCardOnShelf filled with the OLD price a moment before our postfix ran.
            // The queued price-changed event would correct it a few frames later.
            try { compartment.SetPriceTagItemPriceText(cardData); } catch { /* cosmetic */ }

            Debug($"card (grade {cardData.cardGrade}): market {market:0.00} -> {price:0.00} (markup {markup}%)");
            return true;
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Marks a slot's tags as priced the moment we write the price.
        ///
        /// SetItemPrice/SetCardPrice only QUEUE the price-changed event; the tag is
        /// marked when UI_PriceTag handles it, a frame or more later. In that gap the
        /// slot still reads unpriced, and an employee restocking it prices it at
        /// market x their own multiplier (1.0 by default), silently replacing ours.
        /// The game also clears every tag at day start (UI_PriceTag.OnDayStarted),
        /// which reopens the same gap for the whole shop. Marking here is exactly
        /// what the event handler would do, just without the delay.
        /// </summary>
        private static void MarkPriced(List<InteractablePriceTag> tags)
        {
            if (tags == null) return;
            for (int i = 0; i < tags.Count; i++)
            {
                try { if (tags[i] != null) tags[i].SetPriceChecked(true); }
                catch { /* a missing tag must not undo a price already written */ }
            }
        }

        private static void MarkPriced(List<InteractableCardPriceTag> tags)
        {
            if (tags == null) return;
            for (int i = 0; i < tags.Count; i++)
            {
                try { if (tags[i] != null) tags[i].SetPriceChecked(true); }
                catch { /* as above */ }
            }
        }

        private static CardData ResolveCardData(InteractableCardCompartment compartment, out string why)
        {
            why = "";
            try
            {
                var card = compartment.m_StoredCardList[0];
                if (card == null) { why = "card"; return null; }
                if (card.m_Card3dUI == null) { why = "3dui"; return null; }
                if (card.m_Card3dUI.m_CardUI == null) { why = "cardui"; return null; }

                var data = card.m_Card3dUI.m_CardUI.GetCardData();
                if (data == null) why = "getcarddata";
                return data;
            }
            catch (Exception ex)
            {
                why = "threw-" + ex.GetType().Name;
                return null;
            }
        }

        private static bool IsBulkBox(EItemType itemType)
        {
            string name = itemType.ToString();

            for (int i = 0; i < BulkBoxPrefixes.Length; i++)
            {
                if (name.StartsWith(BulkBoxPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Mirrors the rounding the game applies for non-USD currencies. Without this,
        /// a price that looks clean in dollars turns into an unsellable fraction once
        /// the conversion rate is applied.
        /// </summary>
        private static float NormalizeForCurrency(float price)
        {
            try
            {
                float rate = GameInstance.GetCurrencyConversionRate();
                if (Mathf.Approximately(rate, 1f)) return price;

                float divide = GameInstance.GetCurrencyRoundDivideAmount();
                if (divide <= 0f) return price;

                return Mathf.RoundToInt(price * divide) / divide;
            }
            catch
            {
                return price;
            }
        }

        private static void Debug(string message)
        {
            if (!Plugin.Ready || !Plugin.DebugLogging.Value) return;

            Plugin.Log.LogInfo(message);

            // Also written unbuffered: the BepInEx disk log drops anything emitted
            // after chainloader startup unless the game exits cleanly, which makes
            // it useless for watching a live session.
            Plugin.Diag(message);
        }
    }
}
