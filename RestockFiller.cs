using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace KitsunePricer
{
    /// <summary>
    /// A "Restock Low" button in the restock app that fills the game's own cart
    /// with boxes for every item running low. Checkout is left to the player.
    ///
    /// Low is measured against shelf space. An item's target is the total
    /// capacity of the for-sale shelf compartments labelled with it, times
    /// RestockTargetShelves. Against that it counts the same physical stock the
    /// restock cards show (StockCount): shelves, racked boxes, and loose boxes,
    /// which covers boxes on the floor, in the player's or a stocker's hands, and
    /// paid for but still waiting to spawn. Boxes already in the cart count too,
    /// so pressing twice adds nothing the second time. The shortfall is rounded
    /// up to whole boxes, big boxes preferred.
    ///
    /// Only items with a labelled for-sale shelf are considered. A compartment
    /// that sells out drops its label unless the game's lock item label setting
    /// is on, and the item then drops out of the fill with it.
    ///
    /// Boxes go in through RestockItemScreen.AddToCartForCheckout, the call the
    /// card's own add-to-cart dialog makes, so the cart total, badge and checkout
    /// all behave as if they had been added by hand. Each app only fills the items
    /// it sells: the board game app its board games, the main app the rest.
    /// </summary>
    internal static class RestockFiller
    {
        private const string ButtonName = "KitsuneRestockLowBtn";
        private const string Caption = "Restock Low";

        /// <summary>Harmony postfix on RestockItemScreen.Init. Also runs for the board game app.</summary>
        public static void AfterInit(RestockItemScreen __instance)
        {
            try
            {
                var bar = FindDeep(__instance.transform, "WebsiteTopBar");
                if (bar == null) return;

                var existing = bar.Find(ButtonName);
                bool show = Plugin.Ready && Plugin.RestockLowButton.Value;
                if (existing != null)
                {
                    existing.gameObject.SetActive(show);
                    return;
                }
                if (show) CreateButton(__instance, bar);
            }
            catch (Exception ex)
            {
                Plugin.DiagOnce("hook-restockscreen", "restock low button threw (suppressed): " + ex);
            }
        }

        // ---------------------------------------------------------------- filling

        private static void OnPress(RestockItemScreen screen, TextMeshProUGUI label)
        {
            string result;
            try
            {
                result = FillCart(screen);
            }
            catch (Exception ex)
            {
                result = "Failed";
                Plugin.DiagOnce("restock-fill", "restock low fill threw: " + ex);
            }

            if (label != null) screen.StartCoroutine(Flash(label, result));

            // A clicked Button stays selected, and so tinted, until something else
            // is clicked. The game's tabs don't linger like that; neither should this.
            if (UnityEngine.EventSystems.EventSystem.current != null)
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        }

        private static string FillCart(RestockItemScreen screen)
        {
            if (!Plugin.Ready || !GameScene.Ready) return "Not ready";

            var data = CSingleton<InventoryBase>.Instance.m_StockItemData_SO.m_RestockDataList;
            var shown = ShownItemTypes(screen);
            var capacity = ShelfCapacity();
            if (capacity.Count == 0) return "No shelves";

            float multiple = Plugin.RestockTargetShelves.Value;
            var done = new HashSet<EItemType>();
            int added = 0;
            bool full = false;

            // In restock list order, so the cart reads like the app does.
            for (int i = 0; i < data.Count; i++)
            {
                var itemType = data[i].itemType;
                if (!done.Add(itemType) || !shown.Contains(itemType)) continue;
                if (!capacity.TryGetValue(itemType, out int shelfSpace)) continue;

                int need = Mathf.CeilToInt(shelfSpace * multiple)
                           - StockCount.Owned(itemType).Total - InCart(screen, data, itemType);
                if (need <= 0) continue;

                int index = PickBox(data, itemType);
                if (index < 0) continue;

                int perBox = RestockManager.GetMaxItemCountInBox(itemType, data[index].isBigBox);
                if (perBox <= 0) continue;
                int boxes = (need + perBox - 1) / perBox;

                // A new cart line needs a free checkout row, or the checkout
                // screen indexes past its list.
                if ((screen.GetBoxCountInCart(index) == 0 && !screen.HasEnoughCartSlot()) ||
                    !screen.CanAddToCart(boxes))
                {
                    full = true;
                    continue;
                }

                screen.AddToCartForCheckout(index, boxes);
                added += boxes;
                if (Plugin.DebugLogging.Value)
                    Plugin.Diag($"restock low: {itemType} target {Mathf.CeilToInt(shelfSpace * multiple)}, " +
                                $"short {need}, +{boxes} x{perBox}");
            }

            if (added > 0) return "+" + added + (added == 1 ? " box" : " boxes") + (full ? ", cart full" : "");
            return full ? "Cart full" : "Nothing low";
        }

        /// <summary>Shelf space per item over every labelled for-sale compartment.</summary>
        private static Dictionary<EItemType, int> ShelfCapacity()
        {
            var capacity = new Dictionary<EItemType, int>();
            foreach (var compartments in Pricer.ItemCompartmentLists())
            {
                for (int i = 0; i < compartments.Count; i++)
                {
                    var c = compartments[i];
                    if (c == null) continue;

                    var itemType = c.GetItemType();
                    if (itemType == EItemType.None) continue;

                    capacity.TryGetValue(itemType, out int sum);
                    capacity[itemType] = sum + c.GetMaxItemCount();
                }
            }
            return capacity;
        }

        private static HashSet<EItemType> ShownItemTypes(RestockItemScreen screen)
        {
            var so = CSingleton<InventoryBase>.Instance.m_StockItemData_SO;
            var shown = new HashSet<EItemType>();
            if (screen is RestockItemBoardGameScreen)
            {
                shown.UnionWith(so.m_ShownBoardGameItemType);
            }
            else
            {
                shown.UnionWith(so.m_ShownItemType);
                shown.UnionWith(so.m_ShownAccessoryItemType);
                shown.UnionWith(so.m_ShownFigurineItemType);
            }
            shown.Remove(EItemType.None);
            return shown;
        }

        /// <summary>Items of this type already in the cart, over both box sizes.</summary>
        private static int InCart(RestockItemScreen screen, List<RestockData> data, EItemType itemType)
        {
            int items = 0;
            for (int i = 0; i < data.Count; i++)
            {
                if (data[i].itemType != itemType) continue;
                int boxes = screen.GetBoxCountInCart(i);
                if (boxes > 0) items += boxes * RestockManager.GetMaxItemCountInBox(itemType, data[i].isBigBox);
            }
            return items;
        }

        /// <summary>
        /// The restock entry to buy from: unlocked, not hidden by the prologue,
        /// big box first. -1 if the item cannot be bought yet.
        /// </summary>
        private static int PickBox(List<RestockData> data, EItemType itemType)
        {
            bool prologue = CSingleton<CGameManager>.Instance.m_IsPrologue;
            int small = -1;
            for (int i = 0; i < data.Count; i++)
            {
                var d = data[i];
                if (d.itemType != itemType) continue;
                if (prologue && !d.prologueShow) continue;
                if (!CPlayerData.GetIsItemLicenseUnlocked(i)) continue;

                if (d.isBigBox) return i;
                if (small < 0) small = i;
            }
            return small;
        }

        private static IEnumerator Flash(TextMeshProUGUI label, string message)
        {
            label.text = message;
            yield return new WaitForSecondsRealtime(2f);
            if (label != null) label.text = Caption;
        }

        // ----------------------------------------------------------------- button

        /// <summary>
        /// A copy of the app's own Sort button, so it matches the bar. The copy is
        /// made while the source is inactive and only switched on once cleaned up:
        /// the label's localisation component would otherwise put "Sort" back, and
        /// the copied click and controller events would open the sort screen.
        ///
        /// The board game app's bar has no Sort button. It does carry the main
        /// app's category tabs, switched off, so the Booster Packs tab is copied
        /// there instead.
        /// </summary>
        private static void CreateButton(RestockItemScreen screen, Transform bar)
        {
            var sort = (bar.Find("SortBtn") ?? bar.Find("PackBtn")) as RectTransform;
            if (sort == null)
            {
                Plugin.DiagOnce("restock-button-template", "restock low button: no button in the top bar to copy");
                return;
            }

            GameObject go;
            bool wasActive = sort.gameObject.activeSelf;
            sort.gameObject.SetActive(false);
            try
            {
                go = Object.Instantiate(sort.gameObject, bar, false);
            }
            finally
            {
                sort.gameObject.SetActive(wasActive);
            }
            go.name = ButtonName;

            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == "Localize") Object.DestroyImmediate(mb);

            var label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null)
            {
                Object.Destroy(go);
                Plugin.DiagOnce("restock-button-text", "restock low button: Sort button has no text to relabel");
                return;
            }
            label.text = Caption;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.fontSizeMax = label.fontSize;
            label.fontSizeMin = label.fontSize * 0.5f;
            label.enableAutoSizing = true;

            foreach (var cb in go.GetComponentsInChildren<ControllerButton>(true))
            {
                cb.m_OnSelectedEvent = new UnityEvent();
                cb.m_OnDeselectedEvent = new UnityEvent();
                cb.m_OnPressLeftEvent = new UnityEvent();
                cb.m_OnPressRightEvent = new UnityEvent();
            }

            foreach (var button in go.GetComponentsInChildren<Button>(true))
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => OnPress(screen, label));
            }

            Place((RectTransform)go.transform, sort, bar);
            go.SetActive(true);
        }

        /// <summary>
        /// Makes room in the top bar and puts the button after the last tab.
        ///
        /// The bar has no free slot: the tabs run from the logo almost to the cart
        /// total. So the tabs are packed left against the logo with a tighter gap,
        /// and the button takes the space that frees.
        ///
        /// Rects here are not what is drawn. The cart total's white box is a child
        /// image that reaches about 70 units left of TotalPriceBG's own rect, and
        /// the tab sprites and logo carry a transparent margin. The clearance is
        /// measured on build 26358084 so the gap before the price matches the
        /// gaps between tabs.
        /// </summary>
        private static void Place(RectTransform button, RectTransform sort, Transform bar)
        {
            const float spacing = 12f;
            const float priceClearance = 95f;

            var cart = (bar.Find("TotalPriceBG") ?? bar.Find("CheckoutBtn")) as RectTransform;
            float right = cart != null ? Edge(cart, -1) - priceClearance : sort.anchoredPosition.x + 600f;

            var tabs = new List<RectTransform>();
            foreach (Transform child in bar)
            {
                if (child == button || child == cart || !child.gameObject.activeSelf) continue;
                if (!child.name.EndsWith("Btn") || child.name == "CheckoutBtn") continue;
                if (!(child is RectTransform r) || r.anchorMin != r.anchorMax) continue;
                if (r.anchoredPosition.x < right) tabs.Add(r);
            }
            tabs.Sort((a, b) => a.anchoredPosition.x.CompareTo(b.anchoredPosition.x));

            if (tabs.Count == 0)
            {
                PlaceBeforePrice(button, sort, bar, cart, right, spacing);
                return;
            }

            var logo = bar.Find("Logo") as RectTransform;
            float cursor = logo != null && logo.gameObject.activeSelf && logo.anchorMin == logo.anchorMax
                ? Edge(logo, 1)
                : tabs.Count > 0 ? Edge(tabs[0], -1) : right - sort.rect.width * sort.localScale.x;

            foreach (var tab in tabs)
            {
                float shift = Mathf.Min(0f, cursor - Edge(tab, -1));
                tab.anchoredPosition += new Vector2(shift, 0f);
                cursor = Edge(tab, 1) + spacing;
            }

            Fit(button, sort, cursor, right, alignRight: false);
        }

        /// <summary>
        /// For a bar with no tabs, such as the board game app's: the button sits
        /// against the price and keeps clear of whatever is to its left. The title
        /// there is a centred text in a box far wider than its words, so a text
        /// counts by the width of what it says rather than by its rect.
        /// </summary>
        private static void PlaceBeforePrice(RectTransform button, RectTransform template, Transform bar,
            RectTransform cart, float right, float spacing)
        {
            float left = float.MinValue;
            foreach (Transform child in bar)
            {
                if (child == button || child == cart || !child.gameObject.activeSelf) continue;
                if (!(child is RectTransform r) || r.anchorMin != r.anchorMax) continue;

                float l = Edge(r, -1), rr = Edge(r, 1);
                if (r.GetComponent<TextMeshProUGUI>() is TextMeshProUGUI text && text.text.Length > 0)
                {
                    float half = Mathf.Min(r.sizeDelta.x, text.preferredWidth) * r.localScale.x * 0.5f;
                    l = r.anchoredPosition.x - half;
                    rr = r.anchoredPosition.x + half;
                }

                if (l < right && rr > left) left = rr;
            }

            float cursor = left == float.MinValue ? right - template.rect.width * template.localScale.x : left + spacing;
            Fit(button, template, cursor, right, alignRight: true);
        }

        /// <summary>
        /// Sizes the button into [left, right]. It keeps the template's height and
        /// narrows to fit; only if that would leave it under half as wide does it
        /// shrink as a whole.
        /// </summary>
        private static void Fit(RectTransform button, RectTransform template, float left, float right, bool alignRight)
        {
            float gap = right - left;
            float scale = template.localScale.x;
            float width = Mathf.Min(template.rect.width, gap / scale);
            if (width < template.rect.width * 0.5f)
            {
                width = template.rect.width * 0.5f;
                scale = Mathf.Max(0.05f, gap / width);
            }

            float half = width * scale * 0.5f;
            button.localScale = new Vector3(scale, scale, 1f);
            button.sizeDelta = new Vector2(width, template.sizeDelta.y);
            button.anchoredPosition = new Vector2(alignRight ? right - half : left + half, template.anchoredPosition.y);
        }

        /// <summary>Left (-1) or right (1) edge of a centre-anchored rect in its parent's space.</summary>
        private static float Edge(RectTransform r, int side)
        {
            float width = r.sizeDelta.x * r.localScale.x;
            return side < 0
                ? r.anchoredPosition.x - width * r.pivot.x
                : r.anchoredPosition.x + width * (1f - r.pivot.x);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
