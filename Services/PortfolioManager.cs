using KiteConnect;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace ZerodhaOxySocket
{
    public enum PositionState { Flat, Long }

    public class PositionInfo
    {
        public PositionState State { get; set; } = PositionState.Flat;
        public double EntryPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public double TrailStop { get; set; }
        public DateTime LastExitTime { get; set; } = DateTime.MinValue;
        public string Group { get; set; } = "";
    }

    public static class PortfolioManager
    {
        public static bool OneAtATime { get; set; } = true;
        public static TimeSpan CooldownAfterExit { get; set; } = TimeSpan.FromMinutes(10);
        public static int MaxConcurrentPerGroup { get; set; } = 1;

        private static readonly ConcurrentDictionary<uint, PositionInfo> _pos = new();

        public static PositionInfo Get(uint token) => _pos.GetOrAdd(token, _ => new PositionInfo());

        public static void SetGroup(uint token, string group) => Get(token).Group = group ?? "";

        public static bool CanEnter(uint token)
        {
            var pi = Get(token);
            if (pi.State != PositionState.Flat) return false;
            if (pi.LastExitTime != DateTime.MinValue &&
                SessionClock.NowIst() - pi.LastExitTime < CooldownAfterExit) return false;

            if (OneAtATime && _pos.Values.Any(p => p.State == PositionState.Long)) return false;

            if (!string.IsNullOrEmpty(pi.Group))
            {
                int openInGroup = _pos.Values.Count(p => p.State == PositionState.Long && p.Group == pi.Group);
                if (openInGroup >= MaxConcurrentPerGroup) return false;
            }
            return true;
        }

        public static void EnterLong(uint token, double price, double trailStop)
        {
            var pi = Get(token);
            pi.State = PositionState.Long;
            pi.EntryPrice = price;
            pi.EntryTime = SessionClock.NowIst();
            pi.TrailStop = trailStop;
        }

        public static void ExitToFlat(uint token)
        {
            var pi = Get(token);
            pi.State = PositionState.Flat;
            pi.LastExitTime = SessionClock.NowIst();
        }

    }
}
