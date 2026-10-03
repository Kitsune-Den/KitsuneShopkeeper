using HarmonyLib;
using UnityEngine;

namespace KitsunePricer
{
    /// <summary>
    /// Optional: no smelly customers.
    ///
    /// Every customer goes smelly through Customer.SetSmelly - the random roll in
    /// ActivateCustomer and the play table restoring a seated customer both call
    /// it - so blocking that one method stops new ones at the source. The only
    /// path that bypasses it is LoadCustomerSaveData, which writes m_IsSmelly
    /// straight from the save, so customers already smelly when the game was
    /// saved are scrubbed after they load.
    ///
    /// Scrubbing mirrors the game's own deodorant clean minus the stat and
    /// achievement bump: blocked customers were never cleaned, so they should not
    /// count towards ACH_SmellyClean.
    /// </summary>
    internal static class SmellGuard
    {
        private static AccessTools.FieldRef<Customer, bool> _isSmelly;
        private static AccessTools.FieldRef<Customer, int> _smellyMeter;
        private static bool _resolved;

        /// <summary>Harmony prefix on Customer.SetSmelly. Returning false skips it.</summary>
        public static bool BlockSetSmelly()
        {
            return !(Plugin.Ready && Plugin.NoSmellyCustomers.Value);
        }

        /// <summary>Harmony postfix on Customer.LoadCustomerSaveData.</summary>
        public static void AfterLoad(Customer __instance)
        {
            if (!Plugin.Ready || !Plugin.NoSmellyCustomers.Value) return;

            // A postfix that throws would abort loading the rest of the customers.
            try
            {
                if (__instance.IsSmelly())
                    Scrub(__instance, Object.FindObjectOfType<CustomerManager>());
            }
            catch (System.Exception ex)
            {
                Plugin.DiagOnce("smellguard-load-threw", "SmellGuard load scrub threw (suppressed): " + ex);
            }
        }

        /// <summary>
        /// Toggled on mid-game: clear whoever is already smelly instead of waiting
        /// for them to leave.
        /// </summary>
        public static void OnToggled()
        {
            if (!Plugin.NoSmellyCustomers.Value) return;

            try
            {
                // FindObjectOfType, never CSingleton: on the title screen there is
                // no CustomerManager, and the singleton getter would conjure one.
                var manager = Object.FindObjectOfType<CustomerManager>();
                if (manager == null) return;

                var smelly = new System.Collections.Generic.List<Customer>(manager.GetSmellyCustomerList());
                foreach (var customer in smelly)
                    if (customer != null) Scrub(customer, manager);

                Plugin.Diag($"no smelly customers: scrubbed {smelly.Count} on toggle");
            }
            catch (System.Exception ex)
            {
                Plugin.DiagOnce("smellguard-toggle-threw", "SmellGuard toggle scrub threw (suppressed): " + ex);
            }
        }

        private static void Scrub(Customer customer, CustomerManager manager)
        {
            if (!Resolve()) return;

            _isSmelly(customer) = false;
            _smellyMeter(customer) = 0;
            if (customer.m_SmellyFX != null) customer.m_SmellyFX.SetActive(false);
            if (manager != null) manager.RemoveFromSmellyCustomerList(customer);
        }

        /// <summary>
        /// Resolved lazily rather than in a static initialiser: a renamed field in a
        /// game update would otherwise throw TypeInitializationException on every
        /// call, including from inside the game's own load path.
        /// </summary>
        private static bool Resolve()
        {
            if (_resolved) return _isSmelly != null;
            _resolved = true;

            try
            {
                _isSmelly = AccessTools.FieldRefAccess<Customer, bool>("m_IsSmelly");
                _smellyMeter = AccessTools.FieldRefAccess<Customer, int>("m_SmellyMeter");
            }
            catch (System.Exception ex)
            {
                _isSmelly = null;
                Plugin.Diag("SmellGuard: smelly fields not found, scrubbing disabled: " + ex.Message);
            }

            return _isSmelly != null;
        }
    }
}
