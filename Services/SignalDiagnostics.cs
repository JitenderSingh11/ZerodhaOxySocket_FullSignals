using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// SignalDiagnostics with optional file logging. Thread-safe, background writer, daily rotation.
    /// Usage: SignalDiagnostics.Info(...), Reject(...), Accept(...).
    /// Call SignalDiagnostics.StartFileLogging(path) to enable file logging.
    /// Call SignalDiagnostics.StopFileLoggingAndFlush() on shutdown (or at EOD).
    /// </summary>
    public static class SignalDiagnostics
    {
        public static bool Enabled { get; set; } = true;
        public static bool AlsoDebugWrite { get; set; } = true; // emits Debug.WriteLine
        public static bool AlsoConsoleWrite { get; set; } = false; // set true if you run as console app

        // File logging internals
        private static BlockingCollection<string> _queue;
        private static Task _writerTask;
        private static CancellationTokenSource _cts;
        private static string _logFolder = null;
        private static string _currentLogFile = null;
        private static DateTime _currentLogDate = DateTime.MinValue;
        private static readonly object _fileLock = new();

        /// <summary>
        /// Start background file logging. Path can be a directory. Files rotate daily: yyyy-MM-dd-signals.log.
        /// </summary>
        public static void StartFileLogging(string logFolder)
        {
            if (string.IsNullOrWhiteSpace(logFolder)) throw new ArgumentNullException(nameof(logFolder));
            if (_writerTask != null && !_writerTask.IsCompleted) return; // already running

            Directory.CreateDirectory(logFolder);
            _logFolder = logFolder;
            _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => WriterLoop(_cts.Token));
        }

        /// <summary>
        /// Flushes remaining messages and stops background writer.
        /// Call from App.Exit or at EOD.
        /// </summary>
        public static void StopFileLoggingAndFlush()
        {
            try
            {
                if (_queue != null)
                {
                    _queue.CompleteAdding();
                    _writerTask?.Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch { /* swallow */ }
            finally
            {
                _cts?.Cancel();
                _writerTask = null;
                _queue = null;
                _cts = null;
            }
        }

        private static void WriterLoop(CancellationToken ct)
        {
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable(ct))
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        EnsureLogFile();
                        lock (_fileLock)
                        {
                            File.AppendAllText(_currentLogFile, line + Environment.NewLine, Encoding.UTF8);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* drop single-line write failure, continue */ }
                }
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch { /* ignore */ }
        }

        private static void EnsureLogFile()
        {
            var today = DateTime.Now.Date;
            if (_currentLogFile == null || _currentLogDate != today)
            {
                _currentLogDate = today;
                var fileName = $"{today:yyyy-MM-dd}-signals.log";
                _currentLogFile = Path.Combine(_logFolder, fileName);
                // ensure file exists
                if (!File.Exists(_currentLogFile))
                {
                    try { File.WriteAllText(_currentLogFile, $"# SignalDiagnostics log start {DateTime.Now:O}\n"); }
                    catch { /* ignore */ }
                }
            }
        }

        private static void EnqueueFile(string text)
        {
            try
            {
                _queue?.Add(text);
            }
            catch { /* ignore if queue closed */ }
        }

        // core writer
        private static void WriteRaw(string tag, uint token, string instrument, DateTime time, string message)
        {
            if (!Enabled) return;

            Console.ForegroundColor = tag switch
            {
                "ACPT" => ConsoleColor.Green,
                "REJ " => ConsoleColor.Red,
                "WARN" => ConsoleColor.Yellow,
                _ => ConsoleColor.Gray
            };

            var thread = Thread.CurrentThread.ManagedThreadId;
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            var line = $"[{ts}] [{tag}] [T{thread:D2}] {instrument,-18} tok:{token,-8} | {time:O} | {message}";

            if (AlsoConsoleWrite)
            {
                Console.WriteLine(line);
                Console.ResetColor();
            }
            if (AlsoDebugWrite) Debug.WriteLine(line);

            if (!string.IsNullOrEmpty(_logFolder))
                EnqueueFile(line);
        }

        public static void Info(uint token, string instrument, DateTime time, string stage, string message)
            => WriteRaw("INFO", token, instrument, time, $"{stage}: {message}");

        public static void Reject(uint token, string instrument, DateTime time, string reason)
            => WriteRaw("REJ ", token, instrument, time, reason);

        public static void Accept(uint token, string instrument, DateTime time, string note = "")
            => WriteRaw("ACPT", token, instrument, time, note);

        public static void Warn(uint token, string instrument, DateTime time, string message)
            => WriteRaw("WARN", token, instrument, time, message);
    }
}
