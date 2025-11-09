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
            // keep your existing impl if you already have it
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
        }

        public static bool IsRegularSessionAt(DateTime ist)
        {
            // 09:15–15:30 IST inclusive (adjust if you have pre/post rules)
            var t = ist.TimeOfDay;
            return t >= new TimeSpan(9, 15, 0) && t <= new TimeSpan(15, 30, 0);
        }


        public static DateTime FloorToBucketIst(TimeSpan bucket)
        {
            var now = NowIst();
            long ticks = (long)Math.Floor(now.Ticks / (double)bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Unspecified);
        }
    }
}
