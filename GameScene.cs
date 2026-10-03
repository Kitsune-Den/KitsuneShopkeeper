using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Guard for "is there actually a shop loaded".
    ///
    /// This exists because of a genuinely nasty failure. Nearly every static
    /// accessor in this game resolves through CSingleton&lt;T&gt;.Instance, whose
    /// getter MANUFACTURES the manager if it cannot find one, marks it
    /// DontDestroyOnLoad, and caches it forever. Calling ShelfManager.GetShelfList()
    /// on the title screen therefore creates a phantom ShelfManager that
    /// permanently shadows the real one.
    ///
    /// The game then spawns purchased furniture through ShelfManager, parenting the
    /// new package box to the phantom's null group. The box never appears, the money
    /// is already gone, and nothing is queued to recover - the player has simply
    /// lost the purchase.
    ///
    /// So: no call that touches a CSingleton may run until a real manager exists.
    /// FindObjectOfType is safe, since it returns null instead of conjuring one.
    /// </summary>
    internal static class GameScene
    {
        private static float _lastCheck = -999f;
        private static bool _ready;

        public static bool Ready
        {
            get
            {
                // Re-checked on a short interval rather than cached permanently:
                // FindObjectOfType is not free, but a stale "ready" would be worse.
                float now = Time.realtimeSinceStartup;
                if (now - _lastCheck < 1f) return _ready;

                _lastCheck = now;

                bool wasReady = _ready;
                _ready = Object.FindObjectOfType<ShelfManager>() != null;

                if (_ready != wasReady)
                    Plugin.Diag(_ready ? "game scene ready" : "game scene gone");

                return _ready;
            }
        }
    }
}
