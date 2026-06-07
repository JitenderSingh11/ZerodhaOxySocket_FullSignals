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
            try
            {
                // Normalize incoming time to UTC
                var utc = ToUtcFromPossiblyIst(dt);
                
                // VALIDATION: Check if UTC is valid
                if (utc < DateTime.MinValue.AddDays(1) || utc > DateTime.MaxValue.AddDays(-1))
                {
                    // Return a safe default (current time floored)
                    utc = DateTime.UtcNow;
                }
                
                // Convert to IST for bucketing
                var ist = TimeZoneInfo.ConvertTimeFromUtc(utc, IST);
                
                // Calculate bucket ticks with overflow protection
                long bucketTicks = (ist.Ticks / bucket.Ticks) * bucket.Ticks;
                
                // VALIDATION: Ensure bucketTicks is within valid DateTime range
                if (bucketTicks < DateTime.MinValue.Ticks || bucketTicks > DateTime.MaxValue.Ticks)
                {
                    // Fallback to current IST floored
                    var nowIst = NowIst();
                    bucketTicks = (nowIst.Ticks / bucket.Ticks) * bucket.Ticks;
                }
                
                // Return bucket start as IST wall time
                return new DateTime(bucketTicks, DateTimeKind.Unspecified); // IST wall time
            }
            catch (Exception ex)
            {
                // Last resort: return current time floored
                var nowIst = NowIst();
                long safeTicks = (nowIst.Ticks / bucket.Ticks) * bucket.Ticks;
                System.Diagnostics.Debug.WriteLine($"[Clock.FloorToBucketIst] Error: {ex.Message}. Using fallback time.");
                return new DateTime(safeTicks, DateTimeKind.Unspecified);
            }
        }
    }
}
