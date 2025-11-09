using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public class ReplayConfig
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public long[] Tokens { get; set; } = Array.Empty<long>();
        public double TimeScale { get; set; } = 10.0; // 1 = real-time, >1 = faster
        public Guid ReplayId { get; } = Guid.NewGuid();
    }

    public class ReplayEngine
    {
        private readonly ReplayConfig _cfg;
        private CancellationTokenSource _cts;
        private Task _task;

        public event Action<DateTime> OnReplayTimeAdvance; // optional for UI updates

        private bool IsRunning => _task != null && !_task.IsCompleted;

        public bool IsActive => IsRunning;

        public ReplayEngine(ReplayConfig cfg) { _cfg = cfg; }

        public void Start()
        {
            if (_task != null && !_task.IsCompleted) return;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunLoop(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _task?.Wait(2000); } catch { }
        }

        private void RunLoop(CancellationToken ct)
        {
            DateTime? lastTickTime = null;
            foreach (var tick in DataAccess.StreamTicksRange(_cfg.Tokens, _cfg.Start, _cfg.End))
            {
                if (ct.IsCancellationRequested) break;

                // time scaling/waiting
                if (lastTickTime.HasValue)
                {
                    var delta = tick.TickTime - lastTickTime.Value;
                    if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
                    if (_cfg.TimeScale <= 1.0) { Thread.Sleep(delta); }
                    else
                    {
                        var scaled = TimeSpan.FromTicks((long)(delta.Ticks / _cfg.TimeScale));
                        if (scaled > TimeSpan.Zero) Thread.Sleep(scaled);
                    }
                }

                // Process tick through the same pipeline used by live feed.
                // Important: ProcessReplayTick must treat the tick.TickTime as the "current time"
                TickHub.ProcessReplayTick(tick, _cfg.ReplayId, IsActive);

                lastTickTime = tick.TickTime;
                OnReplayTimeAdvance?.Invoke(lastTickTime.Value);
            }
        }
    }
}
