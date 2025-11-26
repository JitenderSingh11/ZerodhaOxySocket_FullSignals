using System;
using System.Linq;

namespace ZerodhaOxySocket.MultiSockets
{
    public class OptionSelectorService
    {
        private readonly int _strikeStep;

        public OptionSelectorService(int strikeStep = 50)
        {
            _strikeStep = strikeStep;
        }

        public int RoundStrike(double price)
            => (int)(Math.Round(price / _strikeStep) * _strikeStep);

        /// <summary>
        /// Choose the ATM option (CE/PE) given underlying price at a signal time.
        /// </summary>
        public InstrumentInfo ChooseATMOption(DateTime signalTime, double underlyingPrice, string ceOrPe)
        {
            int strike = RoundStrike(underlyingPrice);
            // Try loading from instrument snapshot (cached list) first
            var snap = InstrumentHelper.LoadSnapshot(signalTime.Date);
            if (snap != null && snap.Count > 0)
            {
                var candidates = snap.Where(i => i.InstrumentType?.Equals(ceOrPe, StringComparison.OrdinalIgnoreCase) == true
                                                 && Math.Abs(i.Strike - strike) < 0.001)
                                     .OrderBy(i => i.Expiry ?? DateTime.MaxValue)
                                     .ToList();
                if (candidates.Any())
                    return candidates.First();
            }
            // Fallback: search in tick database if snapshot didn't find
            var found = InstrumentHelper.FindOptionTokenFromTicks(signalTime.Date, strike, ceOrPe);
            if (found != null)
                return found;
            // Alternative: nearest strike if exact match not found
            if (snap != null && snap.Count > 0)
            {
                var alt = snap.Where(i => i.InstrumentType?.Equals(ceOrPe, StringComparison.OrdinalIgnoreCase) == true)
                              .OrderBy(i => Math.Abs(i.Strike - strike))
                              .ThenBy(i => i.Expiry ?? DateTime.MaxValue)
                              .FirstOrDefault();
                return alt;
            }
            return null;
        }
    }
}
