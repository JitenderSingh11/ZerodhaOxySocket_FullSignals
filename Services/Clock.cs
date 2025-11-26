using System;

namespace ZerodhaOxySocket.Services
{
    public static class Clock
    {
        private static readonly TimeZoneInfo IST = TimeZoneInfo.FindSystemTimeZoneById(
            Environment.OSVersion.Platform == PlatformID.Win32NT ? "India Standard Time" : "Asia/Kolkata");

        // Internal canonical time
        public static DateTime UtcNow() => DateTime.UtcNow;

        // Return current IST wall-clock time (for UI/logging/DB write)
        public static DateTime NowIst() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IST);

        // Convert a DateTime that may be Utc/Local/Unspecified (assumed IST) into UTC
        public static DateTime ToUtcFromPossiblyIst(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            // Unspecified: assume the value represents IST local time
            return TimeZoneInfo.ConvertTimeToUtc(dt, IST);
        }

        // Convert a UTC DateTime (or any DateTime) to IST wall-clock time
        public static DateTime UtcToIst(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : ToUtcFromPossiblyIst(dt);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, IST);
        }

        // Floor the provided time to the timeframe bucket aligned to IST, but return the bucket start as UTC
        // Input 'dt' may be any kind; it will be normalized to UTC first.
        public static DateTime FloorToBucketIst(DateTime dt, TimeSpan bucket)
        {
            // Normalize incoming time to UTC
            var utc = ToUtcFromPossiblyIst(dt);
            // Convert to IST for bucketing
            var ist = TimeZoneInfo.ConvertTimeFromUtc(utc, IST);
            long bucketTicks = (ist.Ticks / bucket.Ticks) * bucket.Ticks;
            var bucketStartIst = new DateTime(bucketTicks, DateTimeKind.Unspecified); // represent IST wall time
            // Convert bucket start back to UTC for canonical internal storage
            var bucketStartUtc = TimeZoneInfo.ConvertTimeToUtc(bucketStartIst, IST);
            return DateTime.SpecifyKind(bucketStartUtc, DateTimeKind.Utc);
        }
    }
}
