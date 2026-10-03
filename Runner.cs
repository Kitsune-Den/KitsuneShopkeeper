using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Secondary tick driver.
    ///
    /// Kept because it works on the menu scenes, but it cannot be relied on: once
    /// the main scene loads this component stops receiving Update without being
    /// destroyed. The primary driver is the Harmony postfix on the game's own
    /// LightManager.Update. Ticker guards against both firing in one frame.
    /// </summary>
    internal class Runner : MonoBehaviour
    {
        private static Runner _instance;

        public static void Spawn()
        {
            if (_instance != null)
            {
                // Re-enable rather than no-op: the object surviving is not the same
                // as the object still ticking.
                if (!_instance.gameObject.activeSelf) _instance.gameObject.SetActive(true);
                if (!_instance.enabled) _instance.enabled = true;
                return;
            }

            var go = new GameObject("KitsunePricer_Runner");
            DontDestroyOnLoad(go);

            _instance = go.AddComponent<Runner>();
            Plugin.Diag("runner spawned");
        }

        /// <summary>
        /// Attaches a second copy to a GameObject the game owns and keeps alive.
        /// Our own object gets destroyed and later disabled by this game; the light
        /// manager's cannot be, without breaking the day/night cycle.
        /// </summary>
        public static void AttachToGameHost()
        {
            try
            {
                // FindObjectOfType, never CSingleton<LightManager>.Instance.
                // That getter MANUFACTURES a phantom LightManager when none exists
                // yet (e.g. on the title screen), marks it DontDestroyOnLoad and
                // caches it forever. The game's HUD clock, its time and its
                // day-start logic all read through that same cached singleton, so
                // touching it early permanently blanks the in-game clock and stops
                // the day from advancing.
                var host = Object.FindObjectOfType<LightManager>();
                if (host == null) return;

                var existing = host.gameObject.GetComponent<Runner>();
                if (existing != null)
                {
                    // Present but switched off is the failure mode this game
                    // produces, and it looks identical to healthy from the outside.
                    if (!existing.enabled)
                    {
                        existing.enabled = true;
                        Plugin.Diag("re-enabled runner on LightManager host");
                    }
                    return;
                }

                host.gameObject.AddComponent<Runner>();
                Plugin.Diag("runner attached to LightManager host object");
            }
            catch (System.Exception ex)
            {
                Plugin.Diag("host attach failed: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            Plugin.Diag("runner DESTROYED");
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void Update()
        {
            Plugin.DiagOnce("runnerupdate", "runner Update REACHED");
            Ticker.Tick("runner");
        }

        private void OnGUI()
        {
            if (!ConfigPanel.IsOpen || !ConfigPanel.ClaimDraw(this)) return;

            try { ConfigPanel.Draw(); }
            catch (System.Exception ex) { Plugin.DiagOnce("panel-draw", "config panel draw threw: " + ex); }
        }
    }
}
