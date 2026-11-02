using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KitsunePricer
{
    /// <summary>
    /// Shows how many of each item you already own on its restock card.
    ///
    /// The game does keep a per-item total (CPlayerData.GetCurrentTotalItemCount),
    /// but it is bookkeeping for the average-cost figure, not a stock count: it
    /// goes up on purchase and down only on customer sales, so packs you open
    /// yourself or stock you bin never come off it. The count here is physical
    /// instead, walked from where stock actually sits:
    ///
    /// - for-sale shelf compartments (including card/item combo shelves)
    /// - live item boxes, wherever they are: floor, held, or racked live
    /// - boxes racked as data only. A box put on a warehouse rack is often
    ///   destroyed and kept as a StoredBoxRecord on the compartment, so it is in
    ///   neither of the lists above
    /// - boxes paid for and still waiting to spawn, which otherwise look like a
    ///   reason to buy again
    ///
    /// Items in a customer's basket, a worker's hands or a machine hopper are not
    /// counted.
    ///
    /// Alongside the item total it counts the boxes that stock sits in (every
    /// source above except the shelves), so the card shows both how much you
    /// have and how much of it is still boxed up. A box counts once whatever its
    /// fill; empty boxes are not counted. Boxes are counted per size, since the
    /// same item is usually sold in a small and a big box on separate cards, and
    /// each card shows only the boxes of its own size.
    /// </summary>
    internal static class StockCount
    {
        private const string LabelName = "KitsuneStockText";

        private static readonly FieldInfo WarehouseCompartments =
            AccessTools.Field(typeof(RestockManager), "m_WarehouseCompartmentList");

        // One count per frame. Opening a category initialises every card in it in
        // the same frame, and the walk is the same for all of them.
        private static readonly Dictionary<EItemType, int> _items = new Dictionary<EItemType, int>();
        private static readonly Dictionary<EItemType, int> _smallBoxes = new Dictionary<EItemType, int>();
        private static readonly Dictionary<EItemType, int> _bigBoxes = new Dictionary<EItemType, int>();
        private static int _countedFrame = -1;

        /// <summary>Harmony postfix on RestockItemPanelUI.Init.</summary>
        public static void AfterInit(RestockItemPanelUI __instance, int index, EItemType ___m_ItemType)
        {
            // A label left over from when the option was on, or from a card that
            // has since been pooled onto a locked licence, would show a stale count.
            TextMeshProUGUI label = null;
            try
            {
                label = FindLabel(__instance);
                if (index < 0 || !Plugin.Ready || !Plugin.ShowStockOnRestock.Value || !GameScene.Ready)
                {
                    if (label != null) label.gameObject.SetActive(false);
                    return;
                }

                if (label == null) label = CreateLabel(__instance);
                if (label == null) return;

                bool bigBox = InventoryBase.GetRestockData(index).isBigBox;
                int owned = Owned(___m_ItemType, bigBox, out int boxes);
                int perBox = RestockManager.GetMaxItemCountInBox(___m_ItemType, bigBox);
                label.text = "In stock: " + owned + "\n<size=85%>Boxes of " + perBox + ": " + boxes + "</size>";
                label.alpha = owned > 0 ? 1f : 0.55f;
                label.gameObject.SetActive(true);
            }
            catch (Exception ex)
            {
                // Init also sets up the buy button. Nothing may escape into it.
                if (label != null) label.gameObject.SetActive(false);
                Plugin.DiagOnce("hook-restockinit", "restock stock label threw (suppressed): " + ex);
            }
        }

        internal static int Owned(EItemType itemType, bool bigBox, out int boxes)
        {
            boxes = 0;
            if (itemType == EItemType.None) return 0;

            if (_countedFrame != Time.frameCount)
            {
                Recount();
                _countedFrame = Time.frameCount;
            }

            (bigBox ? _bigBoxes : _smallBoxes).TryGetValue(itemType, out boxes);
            return _items.TryGetValue(itemType, out int n) ? n : 0;
        }

        private static void Recount()
        {
            _items.Clear();
            _smallBoxes.Clear();
            _bigBoxes.Clear();

            foreach (var compartments in Pricer.ItemCompartmentLists())
            {
                for (int i = 0; i < compartments.Count; i++)
                {
                    var c = compartments[i];
                    if (c != null) Add(c.GetItemType(), c.GetItemCount(), 0, false);
                }
            }

            var boxes = CSingleton<RestockManager>.Instance.m_ItemPackagingBoxList;
            if (boxes != null)
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    var box = boxes[i];
                    if (box != null && box.m_ItemCompartment != null)
                        Add(box.GetItemType(), box.m_ItemCompartment.GetItemCount(), 1, box.m_IsBigBox);
                }
            }

            if (WarehouseCompartments?.GetValue(CSingleton<RestockManager>.Instance) is List<ShelfCompartment> racks)
            {
                for (int i = 0; i < racks.Count; i++)
                {
                    var rack = racks[i];
                    if (rack == null) continue;

                    for (int r = 0; r < rack.GetStoredBoxRecordCount(); r++)
                    {
                        var record = rack.PeekStoredBoxRecord(r);
                        Add(record.itemType, record.amount, 1, record.isBigBox);
                    }
                }
            }

            var pendingIndex = CPlayerData.m_SpawnBoxRestockIndexWaitingList;
            var pendingBoxes = CPlayerData.m_SpawnBoxItemCountWaitingList;
            for (int i = 0; i < pendingIndex.Count && i < pendingBoxes.Count; i++)
            {
                var data = InventoryBase.GetRestockData(pendingIndex[i]);
                Add(data.itemType, pendingBoxes[i] * RestockManager.GetMaxItemCountInBox(data.itemType, data.isBigBox),
                    pendingBoxes[i], data.isBigBox);
            }
        }

        private static void Add(EItemType itemType, int amount, int boxes, bool bigBox)
        {
            if (itemType == EItemType.None || amount <= 0) return;
            _items.TryGetValue(itemType, out int n);
            _items[itemType] = n + amount;
            if (boxes <= 0) return;
            var sized = bigBox ? _bigBoxes : _smallBoxes;
            sized.TryGetValue(itemType, out int b);
            sized[itemType] = b + boxes;
        }

        // ------------------------------------------------------------------ label

        private static TextMeshProUGUI FindLabel(RestockItemPanelUI card)
        {
            if (card.m_UIGrp == null) return null;
            var t = card.m_UIGrp.transform.Find(LabelName);
            return t == null ? null : t.GetComponent<TextMeshProUGUI>();
        }

        /// <summary>
        /// A fresh text object styled after the card's own name text, pinned to the
        /// top-left of the card face. Built from scratch rather than cloned: a clone
        /// would bring along the name text's localisation component, which rewrites
        /// the text whenever the language is applied.
        ///
        /// UIGrp fills the whole grid cell, not the visible card: the cream frame is
        /// drawn inset inside a 691x956 sprite. The grey face is UIGrp's "Mask"
        /// child, stretched and inset (91 units each side, 98 from the top on build
        /// 22936874). The label is placed against that rect, read at runtime, so it
        /// follows the card if the layout changes. It stays a child of UIGrp rather
        /// than of Mask so it draws above the product icons, which come after Mask.
        /// </summary>
        private static TextMeshProUGUI CreateLabel(RestockItemPanelUI card)
        {
            var source = card.m_ItemNameText;
            if (card.m_UIGrp == null || source == null) return null;

            var go = new GameObject(LabelName, typeof(RectTransform));
            go.layer = card.m_UIGrp.layer;
            go.transform.SetParent(card.m_UIGrp.transform, false);

            // The card face may be laid out by a layout group; stay out of it.
            go.AddComponent<LayoutElement>().ignoreLayout = true;

            // Insets of the grey face within UIGrp, measured from its edges.
            float left = 91f, right = 91f, top = 98f;
            if (card.m_UIGrp.transform.Find("Mask") is RectTransform face &&
                face.anchorMin == Vector2.zero && face.anchorMax == Vector2.one)
            {
                left = face.offsetMin.x;
                right = -face.offsetMax.x;
                top = -face.offsetMax.y;
            }

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(left + 16f, -(top + 96f));
            rect.offsetMax = new Vector2(-(right + 16f), -(top + 12f));

            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = source.font;
            label.fontSharedMaterial = source.fontSharedMaterial;
            label.color = source.color;
            label.fontStyle = source.fontStyle;
            label.fontSize = source.fontSize * 0.75f;
            label.enableAutoSizing = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.raycastTarget = false;

            go.transform.SetAsLastSibling();
            return label;
        }
    }
}
