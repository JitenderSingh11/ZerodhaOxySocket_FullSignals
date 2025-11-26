using System;

namespace ZerodhaOxySocket
{
    public static class SessionClock
    {
        private static readonly TimeSpan Start = new(9, 15, 0);
        private static readonly TimeSpan End   = new(15, 30, 0);

        private static readonly TimeZoneInfo IST =
            TimeZoneInfo.FindSystemTimeZoneById(
                Environment.OSVersion.Platform == PlatformID.Win32NT ? "India Standard Time" : "Asia/Kolkata");

        public static DateTime NowIst()
        {
            // return IST wall-clock time
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IST);
        }

        // Accept a DateTime in any Kind; normalizes to IST before checking session window
        public static bool IsRegularSessionAt(DateTime dt)
        {
            // normalize incoming time to UTC first, treating Unspecified as IST
            DateTime utc = ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(dt);
            DateTime ist = TimeZoneInfo.ConvertTimeFromUtc(utc, IST);
            var t = ist.TimeOfDay;
            return t >= Start && t <= End;
        }


        public static DateTime FloorToBucketIst(TimeSpan bucket)
        {
            var now = NowIst();
            long ticks = (long)Math.Floor(now.Ticks / (double)bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Unspecified);
        }
    }
}
