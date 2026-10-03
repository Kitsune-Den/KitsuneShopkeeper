using System;

namespace KitsunePricer
{
    /// <summary>
    /// Pure pricing math. No game types in here on purpose, so the rules can be
    /// reasoned about (and unit tested) without booting Unity.
    /// </summary>
    public static class PricingEngine
    {
        public enum RoundMode
        {
            /// <summary>Always round up to the next increment. Never leaves money on the table.</summary>
            Up = 0,
            /// <summary>Round to whichever increment is closest.</summary>
            Nearest = 1,
            /// <summary>No rounding, just markup.</summary>
            Off = 2,
            /// <summary>
            /// Always round down to the increment below. Gives clean whole-dollar
            /// tags when paired with a generous markup, and NeverBelowMarket keeps
            /// it from cutting into cost on cheap stock.
            /// </summary>
            Down = 3
        }

        public struct Rules
        {
            public float MarkupPercent;
            public float RoundToNearest;
            public RoundMode Mode;
            public bool NeverBelowMarket;
            public float AbsoluteMinPrice;
        }

        /// <summary>
        /// Applies markup to <paramref name="marketPrice"/>, then rounding, then the
        /// safety floors. Returns 0 for a non-positive market price so callers can
        /// skip the item rather than pricing it at nonsense.
        /// </summary>
        public static float Compute(float marketPrice, Rules rules)
        {
            if (marketPrice <= 0f || float.IsNaN(marketPrice) || float.IsInfinity(marketPrice))
                return 0f;

            double price = marketPrice * (1.0 + rules.MarkupPercent / 100.0);

            double increment = rules.RoundToNearest;
            if (rules.Mode != RoundMode.Off && increment > 0.0)
            {
                price = ApplyRounding(price, increment, rules.Mode);

                // Rounding to a coarse increment can land under market and quietly
                // eat the whole margin. Push back up to the first increment at or
                // above market when asked to.
                if (rules.NeverBelowMarket && price < marketPrice)
                    price = Math.Ceiling(marketPrice / increment) * increment;
            }
            else if (rules.NeverBelowMarket && price < marketPrice)
            {
                price = marketPrice;
            }

            if (rules.AbsoluteMinPrice > 0f && price < rules.AbsoluteMinPrice)
                price = rules.AbsoluteMinPrice;

            // The game stores currency as float; keep it to cents so the price tag
            // does not render something like 4.4999998.
            return (float)Math.Round(price, 2, MidpointRounding.AwayFromZero);
        }

        private static double ApplyRounding(double value, double increment, RoundMode mode)
        {
            double steps = value / increment;

            switch (mode)
            {
                case RoundMode.Up:
                    // Guard against float dust pushing an already-exact value up a
                    // full increment (e.g. 3.0000001 / 0.5 should stay 3.00).
                    if (Math.Abs(steps - Math.Round(steps)) < 1e-6)
                        steps = Math.Round(steps);
                    else
                        steps = Math.Ceiling(steps);
                    break;

                case RoundMode.Nearest:
                    steps = Math.Round(steps, MidpointRounding.AwayFromZero);
                    break;

                case RoundMode.Down:
                    // Same float-dust guard as Up, so an exact 25.0 does not slide
                    // down to 24.0 on a rounding artefact.
                    if (Math.Abs(steps - Math.Round(steps)) < 1e-6)
                        steps = Math.Round(steps);
                    else
                        steps = Math.Floor(steps);
                    break;
            }

            return steps * increment;
        }

        /// <summary>
        /// Picks the markup for a card, honouring the high-value tier if enabled.
        /// Lets you run thin margins on chase cards and fat ones on bulk singles.
        /// </summary>
        public static float SelectCardMarkup(
            float marketPrice,
            float baseMarkup,
            bool tierEnabled,
            float tierThreshold,
            float tierMarkup)
        {
            if (tierEnabled && tierThreshold > 0f && marketPrice >= tierThreshold)
                return tierMarkup;

            return baseMarkup;
        }
    }
}
