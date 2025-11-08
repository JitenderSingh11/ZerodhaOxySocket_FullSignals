using System;
using System.Linq;

namespace ZerodhaOxySocket
{
    public static class OptionMapper
    {
        public static int RoundStrike(double price, int step = 50)
            => (int)(Math.Round(price / step) * step);

        // returns InstrumentInfo (null if not found) - uses snapshot or tick-discovery fallback
        public static InstrumentInfo ChooseATMOption(DateTime signalTime, double underlyingPrice, string ceOrPe, int strikeStep = 50)
        {
            int strike = RoundStrike(underlyingPrice, strikeStep);
            var snap = InstrumentHelper.LoadSnapshot(signalTime.Date);
            if (snap != null && snap.Count > 0)
            {
                var candidates = snap.Where(i => (i.InstrumentType?.ToUpperInvariant() == ceOrPe)
                                            && (Math.Abs((int)i.Strike - strike) == 0))
                                     .OrderBy(i => i.Expiry ?? DateTime.MaxValue)
                                     .ToList();
                if (candidates.Any()) return candidates.First();
            }

            var found = InstrumentHelper.FindOptionTokenFromTicks(signalTime.Date, strike, ceOrPe);
            if (found != null) return found;

            if (snap != null && snap.Count > 0)
            {
                var alt = snap.Where(i => (i.InstrumentType?.ToUpperInvariant() == ceOrPe))
                              .OrderBy(i => Math.Abs((int)i.Strike - strike))
                              .ThenBy(i => i.Expiry ?? DateTime.MaxValue)
                              .FirstOrDefault();
                return alt;
            }
            return null;
        }
    }
}
