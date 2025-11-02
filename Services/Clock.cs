using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZerodhaOxySocket.Services
{
    public static class Clock
    {
        private static readonly TimeZoneInfo IST = TimeZoneInfo.FindSystemTimeZoneById(
            Environment.OSVersion.Platform == PlatformID.Win32NT ? "India Standard Time" : "Asia/Kolkata");
        public static DateTime NowIst() => TimeZoneInfo.ConvertTime(DateTime.UtcNow, IST);
        public static DateTime FloorToBucketIst(DateTime dt, TimeSpan bucket)
        {
            var ticks = (long)Math.Floor(dt.Ticks / (double)bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Unspecified); // interpret as IST local time
        }
    }

}
