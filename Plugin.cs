using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KitsunePricer
{
    [BepInPlugin(Guid, "Kitsune Shopkeeper", Version)]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// Kept from when this mod was Kitsune Pricer. BepInEx names the config file
        /// after it, so changing it would silently reset every user's settings.
        /// The DLL and diag log keep the old name for the same reason.
        /// </summary>
        public const string Guid = "gg.goodtimes.kitsunepricer";

        /// <summary>
        /// Bump this on every build that leaves this machine. Several builds went
        /// out all reporting 0.1.0, which made a user's log useless for telling us
        /// which one they were actually running.
        /// </summary>
        public const string Version = "0.3.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        /// <summary>
        /// Config is loaded and usable.
        ///
        /// Deliberately NOT an Instance null check. This game destroys the BepInEx
        /// manager GameObject, which takes this component with it, and Unity's
        /// overloaded == then reports the destroyed object as null. Every feature
        /// gated on "Instance != null" therefore switched itself off permanently a
        /// fraction of a second after startup. Config entries are plain objects and
        /// survive, so a static flag is the honest test.
        /// </summary>
        internal static bool Ready;

        private Harmony _harmony;
        private bool _listenersBound;

        // ---- Config: triggers -------------------------------------------------
        internal static ConfigEntry<bool> AutoPriceOnNewDay;
        internal static ConfigEntry<bool> PriceOnPlacement;
        internal static ConfigEntry<bool> RepriceAlreadyPriced;
        internal static ConfigEntry<float> SafetySweepSeconds;

        // ---- Config: markup ---------------------------------------------------
        internal static ConfigEntry<float> CardMarkup;
        internal static ConfigEntry<float> GradedCardMarkup;
        internal static ConfigEntry<float> ItemMarkup;
        internal static ConfigEntry<float> BulkBoxMarkup;

        internal static ConfigEntry<bool> HighValueTierEnabled;
        internal static ConfigEntry<float> HighValueThreshold;
        internal static ConfigEntry<float> HighValueMarkup;

        // ---- Config: rounding -------------------------------------------------
        internal static ConfigEntry<PricingEngine.RoundMode> RoundingMode;
        internal static ConfigEntry<float> CardRoundTo;
        internal static ConfigEntry<float> ItemRoundTo;
        internal static ConfigEntry<bool> NeverBelowMarket;
        internal static ConfigEntry<float> AbsoluteMinPrice;

        // ---- Config: hotkeys --------------------------------------------------
        internal static ConfigEntry<KeyboardShortcut> KeyPriceAll;
        internal static ConfigEntry<KeyboardShortcut> KeyPriceCards;
        internal static ConfigEntry<KeyboardShortcut> KeyPriceItems;
        internal static ConfigEntry<KeyboardShortcut> KeyConfigPanel;

        /// <summary>
        /// The config file, kept static for the settings panel. Same reasoning as
        /// Ready: the game destroys this component, so Instance.Config is not a
        /// safe way to reach it later. The ConfigFile itself is a plain object.
        /// </summary>
        internal static ConfigFile Cfg;

        // ---- Config: trade guard ----------------------------------------------
        internal static ConfigEntry<bool> TradeGuardEnabled;
        internal static ConfigEntry<float> TradeGuardMultiplier;
        internal static ConfigEntry<float> TradeGuardAbsolute;

        // ---- Config: lights ---------------------------------------------------
        internal static ConfigEntry<bool> AutoLights;
        internal static ConfigEntry<int> LightsOnHour;
        internal static ConfigEntry<int> LightsOffHour;

        // ---- Config: customers ------------------------------------------------
        internal static ConfigEntry<bool> NoSmellyCustomers;

        // ---- Config: workers --------------------------------------------------
        internal static ConfigEntry<bool> ReducedWages;
        internal static ConfigEntry<float> WagePercent;

        internal static ConfigEntry<bool> DebugLogging;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            BindConfig();
            Ready = true;

            _harmony = new Harmony(Guid);
            Patches.Apply(_harmony);

            Log.LogInfo($"Kitsune Shopkeeper v{Version} loaded.");

            Diag("Awake completed");

            // Do NOT bind event listeners here. CEventManager is a CSingleton too,
            // and AddListener resolves through CSingleton<CEventManager>.Instance.
            // Called this early there is no CEventManager in the scene yet, so that
            // getter manufactures a phantom event bus and caches it - the same trap
            // that blanked the clock via LightManager, except applied to every event
            // in the game. Binding is deferred to scene load, where a real one exists.

            Runner.Spawn();

            // Respawn the runner after every scene load. This game does not drive
            // Update on the BepInEx manager object, and the runner itself can be
            // torn down when a save loads, which silently kills hotkeys and lights.
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
            UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            Diag($"scene loaded: {scene.name}");

            GameClock.Invalidate();

            Runner.Spawn();
            Runner.AttachToGameHost();
            Lights.ResetForNewDay();

            // Rebind on every scene load, not just the first.
            //
            // The event manager that exists on the title screen is not necessarily
            // the one running once a save has loaded. Binding once and trusting it
            // leaves the listener attached to a manager the game has moved on from,
            // which looks identical to being subscribed and never hearing anything.
            _listenersBound = false;
            TryBindListeners();
        }

        private static readonly System.Collections.Generic.HashSet<string> _diagOnce
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>Logs once per key, so a per-frame failure cannot flood the file.</summary>
        internal static void DiagOnce(string key, string message)
        {
            if (!_diagOnce.Add(key)) return;
            Diag(message);
        }

        /// <summary>
        /// Unbuffered diagnostic trace. BepInEx's disk log buffers everything after
        /// chainloader startup, so anything logged from the Unity loop can vanish.
        /// </summary>
        internal static void Diag(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Paths.PluginPath, "KitsunePricer.diag.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { /* diagnostics must never break the game */ }
        }

        private void BindConfig()
        {
            Cfg = Config;

            AutoPriceOnNewDay = Config.Bind("Triggers", "AutoPriceOnNewDay", true,
                "Reprice everything on the shop floor when a new day starts, using that day's market values.");
            PriceOnPlacement = Config.Bind("Triggers", "PriceOnPlacement", true,
                "Price a compartment the moment stock is placed into it. Applies to employees as well as you, " +
                "which is the gap other pricing mods leave open.");
            SafetySweepSeconds = Config.Bind("Triggers", "SafetySweepSeconds", 15f,
                "Every this many seconds, price anything on the floor that has no price yet. " +
                "Existing prices are left alone. This is the backstop that stops unpriced stock " +
                "sitting on a shelf if a placement hook or the day-start event is missed. Set 0 to disable.");
            RepriceAlreadyPriced = Config.Bind("Triggers", "RepriceAlreadyPriced", true,
                "On a daily or hotkey run, also update compartments that already carry a price. " +
                "Turn off to only fill in unpriced stock and leave your manual prices alone.");

            CardMarkup = Config.Bind("Markup", "CardMarkup", 15f,
                "Percent over market price for ungraded single cards. Negative values undercut market.");
            GradedCardMarkup = Config.Bind("Markup", "GradedCardMarkup", 15f,
                "Percent over market price for graded cards.");
            ItemMarkup = Config.Bind("Markup", "ItemMarkup", 10f,
                "Percent over market price for standard items (packs, accessories, supplies).");
            BulkBoxMarkup = Config.Bind("Markup", "BulkBoxMarkup", 5f,
                "Percent over market price for bulk boxes. Detected by item type at runtime, so box types " +
                "added in future game updates are picked up automatically instead of being priced as plain items.");

            HighValueTierEnabled = Config.Bind("Markup", "HighValueTierEnabled", false,
                "Use a separate markup for cards at or above HighValueThreshold. Lets you run a thinner " +
                "margin on chase cards while keeping a fat one on bulk singles.");
            HighValueThreshold = Config.Bind("Markup", "HighValueThreshold", 100f,
                "Market price at which a card counts as high value.");
            HighValueMarkup = Config.Bind("Markup", "HighValueMarkup", 10f,
                "Percent over market price for cards at or above the threshold.");

            RoundingMode = Config.Bind("Rounding", "RoundingMode", PricingEngine.RoundMode.Up,
                "Up = always round up to the next increment. Down = always round down. " +
                "Nearest = closest increment. Off = no rounding. " +
                "Down plus a healthy markup gives clean whole-dollar tags; NeverBelowMarket " +
                "stops it cutting below market on cheap stock.");
            CardRoundTo = Config.Bind("Rounding", "CardRoundTo", 0.5f,
                "Rounding increment for cards. 0.5 = nearest half dollar, 1 = whole dollars.");
            ItemRoundTo = Config.Bind("Rounding", "ItemRoundTo", 0.5f,
                "Rounding increment for items and bulk boxes.");
            NeverBelowMarket = Config.Bind("Rounding", "NeverBelowMarket", true,
                "Never let rounding drop a price below market value. With Nearest mode this matters a lot; " +
                "without it a coarse increment can quietly eat your entire margin.");
            AbsoluteMinPrice = Config.Bind("Rounding", "AbsoluteMinPrice", 0.5f,
                "Nothing is ever priced below this. Set 0 to disable.");

            KeyPriceAll = Config.Bind("Hotkeys", "PriceAll", new KeyboardShortcut(KeyCode.F5),
                "Price every card and item on the floor.");
            KeyPriceCards = Config.Bind("Hotkeys", "PriceCards", new KeyboardShortcut(KeyCode.F7),
                "Price cards only.");
            KeyPriceItems = Config.Bind("Hotkeys", "PriceItems", new KeyboardShortcut(KeyCode.F6),
                "Price items only.");
            KeyConfigPanel = Config.Bind("Hotkeys", "ConfigPanel", new KeyboardShortcut(KeyCode.F8),
                "Open or close the in-game settings panel.");

            TradeGuardEnabled = Config.Bind("TradeGuard", "TradeGuardEnabled", true,
                "Block the first accept when you offer a customer far more than their card is worth. " +
                "Press accept a second time to push a deliberate overpay through.");
            TradeGuardMultiplier = Config.Bind("TradeGuard", "TradeGuardMultiplier", 3f,
                "Warn when the offer is at least this many times the card's market price. " +
                "3 catches a stray extra zero comfortably. Set 0 to disable this check.");
            TradeGuardAbsolute = Config.Bind("TradeGuard", "TradeGuardAbsolute", 0f,
                "Also warn on any offer at or above this flat amount, whatever the market price. " +
                "Set 0 to disable this check.");

            AutoLights = Config.Bind("Lights", "AutoLights", true,
                "Switch the shop lights on and off automatically at the hours below.");
            LightsOnHour = Config.Bind("Lights", "LightsOnHour", 17,
                new ConfigDescription(
                    "Hour (24h clock) to switch the shop lights on. 17 = 5pm.",
                    new AcceptableValueRange<int>(0, 23)));
            LightsOffHour = Config.Bind("Lights", "LightsOffHour", -1,
                "Hour (24h clock) to switch the shop lights back off. Set -1 to never switch them off automatically.");

            NoSmellyCustomers = Config.Bind("Customers", "NoSmellyCustomers", false,
                "Stop customers ever arriving smelly, and clear any already smelly in a loaded save. " +
                "Off by default since it changes gameplay: the shop never takes the smell review hit, " +
                "and the deodorant achievements stop progressing while it is on. Takes effect live.");
            NoSmellyCustomers.SettingChanged += (_, __) => SmellGuard.OnToggled();

            ReducedWages = Config.Bind("Workers", "ReducedWages", false,
                "Pay employees a percentage of their normal wage (WagePercent). Applies to wages billed " +
                "at the end of each day from now on; bills already owed are unchanged.");
            WagePercent = Config.Bind("Workers", "WagePercent", 50f,
                new ConfigDescription(
                    "Percent of normal wages paid while ReducedWages is on. 0 = employees work for free, " +
                    "100 = normal wages.",
                    new AcceptableValueRange<float>(0f, 100f)));

            DebugLogging = Config.Bind("Misc", "DebugLogging", false,
                "Verbose per-item logging. Leave off unless something looks wrong.");
        }

        private void TryBindListeners()
        {
            if (_listenersBound) return;

            try
            {
                // Only bind once a real event manager exists in the scene. Calling
                // AddListener before then makes CSingleton manufacture a phantom bus.
                if (UnityEngine.Object.FindObjectOfType<CEventManager>() == null)
                {
                    DiagOnce("no-eventmanager", "no CEventManager in scene yet, deferring listener bind");
                    return;
                }

                // Remove first so repeated binds cannot stack duplicate handlers on
                // a manager that did survive the scene change.
                try { CEventManager.RemoveListener<CEventPlayer_OnDayStarted>(OnDayStarted); }
                catch { /* not registered on this manager, which is fine */ }

                CEventManager.AddListener<CEventPlayer_OnDayStarted>(OnDayStarted);
                _listenersBound = true;
                Log.LogInfo("Subscribed to day-start event.");
                Diag("day-start listener bound");
            }
            catch (Exception ex)
            {
                Diag("listener bind failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Deliberately does NOT unpatch or unbind anything.
        ///
        /// This is not shutdown. The game destroys the BepInEx manager GameObject
        /// about a second after startup, and Unity calls OnDestroy on this component
        /// when it does. This used to call _harmony.UnpatchSelf(), which removed every
        /// patch moments after "patches applied" was logged: placement pricing, the
        /// trade guard and the old LightManager.Update attempt all looked applied and
        /// never once ran. Process exit tears patches down on its own.
        /// </summary>
        private void OnDestroy()
        {
            Diag("plugin component destroyed by the game (patches left in place)");
        }

        private void OnDayStarted(CEventPlayer_OnDayStarted evt)
        {
            // CEventManager dispatches listeners as one multicast delegate with no
            // exception guard, and an exception escaping OnNotify aborts the rest of
            // the queue for that frame. Throwing here would silently cancel every
            // listener registered after ours - deliveries, restocks, whatever else
            // hangs off day start. Nothing may escape.
            try { HandleDayStarted(); }
            catch (Exception ex) { DiagOnce("dayStarted-threw", "OnDayStarted threw (suppressed): " + ex); }
        }

        /// <summary>
        /// Set by the day-start event, consumed by the next tick.
        ///
        /// Eight classes listen for day start and the game dispatches them as one
        /// multicast delegate with no exception guard, so a listener that throws
        /// silently cancels every listener after it. RestockManager is one of them,
        /// and it is what queues the delivery boxes. Doing a whole-shop reprice
        /// inside that chain is both slow and risky, so the event only raises a
        /// flag and the work happens outside the dispatch.
        /// </summary>
        internal static bool DayStartRepricePending;

        private void HandleDayStarted()
        {
            // Traced unconditionally: this is the one hook we cannot otherwise
            // prove fired, and the BepInEx log loses it.
            Diag("day-start event received");

            // Re-attach here as well as on scene load. Both are engine-driven, so
            // they still fire if our own update loop has been killed - which is the
            // only way to recover, since a dead loop cannot heal itself.
            GameClock.Invalidate();
            Runner.AttachToGameHost();

            Lights.ResetForNewDay();

            if (!AutoPriceOnNewDay.Value)
            {
                Diag("AutoPriceOnNewDay is off, skipping");
                return;
            }

            // Deliberately does NOT reprice here. Queue it and get out of the
            // dispatch chain so the listeners behind us - RestockManager and the
            // delivery boxes among them - run promptly and unaffected.
            DayStartRepricePending = true;
            Diag("day-start reprice queued");
        }

    }
}
