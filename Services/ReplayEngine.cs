using System;
using System.Threading;
using System.Threading.Tasks;
using static ZerodhaOxySocket.ReplayWindow;

namespace ZerodhaOxySocket
{

    public enum ReplayMode { Tick, Candle }

    public class ReplayConfig
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public long[] Tokens { get; set; } = Array.Empty<long>();
        public double TimeScale { get; set; } = 10.0; // 1 = real-time, >1 = faster
        public Guid ReplayId { get; } = Guid.NewGuid();

        public ReplayMode Mode { get; set; } = ReplayMode.Candle; // default
        public int CandleTfMinutes { get; set; } = 5; // used when Mode == Candle
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

            TickHub.ReplayInit(_cfg);

            if (_cfg.Mode == ReplayMode.Tick)
            {
                RunAsTicks(_cfg);
            }
            else
            {
                RunAsCandles(_cfg);
            }

        }

        private void RunAsTicks(ReplayConfig cfg)
        {
            foreach (var token in cfg.Tokens)
            {
                DateTime? lastTickTime = null;

                var ticksData = DataAccess.GetTicksRange(_cfg.Start, _cfg.End);
                foreach (var tick in ticksData)
                {

                 

                    // Process tick through the same pipeline used by live feed.
                    // Important: ProcessReplayTick must treat the tick.TickTime as the "current time"
                    TickHub.ProcessReplayTick(tick, _cfg.ReplayId, IsActive);

                    lastTickTime = tick.TickTime;
                    OnReplayTimeAdvance?.Invoke(lastTickTime.Value);
                }
            }
        }

        private void RunAsCandles(ReplayConfig cfg)
        {
            foreach (var token in cfg.Tokens)
            {
                // load aggregated candles for this token
                var candles = DataAccess.LoadAggregatedCandles(token, cfg.Start, cfg.End, cfg.CandleTfMinutes);

                DateTime? lastTime = null;
                foreach (var candle in candles)
                {
               
                    // Call the TickHub's replay-candle handler
                    TickHub.ProcessReplayCandle((uint)token, candle, cfg.ReplayId);

                    lastTime = candle.Time;
                    OnReplayTimeAdvance?.Invoke(candle.Time);
                }
            }
        }

    }
}
