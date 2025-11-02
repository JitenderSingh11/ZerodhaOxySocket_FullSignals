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

        public static DateTime NowIst() => TimeZoneInfo.ConvertTime(DateTime.UtcNow, IST);

        public static bool IsRegularSessionNow()
        {
            var t = NowIst().TimeOfDay;
            return t >= Start.Add(TimeSpan.FromSeconds(-10)) && t <= End.Add(TimeSpan.FromSeconds(10));
        }

        public static DateTime FloorToBucketIst(TimeSpan bucket)
        {
            var now = NowIst();
            long ticks = (long)Math.Floor(now.Ticks / (double)bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Unspecified);
        }
    }
}
