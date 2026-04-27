using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FlameStreamBackend.Services
{
    public class TranscodeRegistry : BackgroundService
    {
        private readonly ConcurrentDictionary<string, Process> _procs = new();
        private readonly SemaphoreSlim _limit;
        private readonly WinJobObject? _job = OperatingSystem.IsWindows() ? new WinJobObject() : null;

        public TranscodeRegistry(int maxConcurrent = 2) => _limit = new(maxConcurrent);

        public async Task<Process> StartAsync(string key, ProcessStartInfo psi)
        {
            await _limit.WaitAsync();
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = false;
            var p = Process.Start(psi)!;
            if (OperatingSystem.IsWindows()) _job?.Assign(p);
            p.EnableRaisingEvents = true;
            _procs[key] = p;
            p.Exited += (_, __) =>
            {
                if (_procs.TryRemove(key, out var removed))
                    _limit.Release();
            };
            _ = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = p.StandardError.ReadLine()) != null)
                        Console.Error.WriteLine($"[ffmpeg:{key}] {line}");
                }
                catch { }
            });
            return p;
        }

        public bool IsRunning(string key) =>
            _procs.TryGetValue(key, out var p) && p != null && !p.HasExited;

        public void Stop(string key)
        {
            if (_procs.TryRemove(key, out var p))
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                _limit.Release();
            }
        }

        public void StopAll()
        {
            foreach (var key in _procs.Keys) Stop(key);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(StopAll);
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (OperatingSystem.IsWindows()) _job?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class WinJobObject : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref ExtendedLimitInfo info, int cbInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInfo
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags, MinWorkingSet, MaxWorkingSet, ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInfo
        {
            public BasicLimitInfo BasicInfo;
            public IoCounters Io;
            public UIntPtr ProcessMemLimit, JobMemLimit, PeakProcMem, PeakJobMem;
        }

        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedInfoClass = 9;
        private readonly IntPtr _handle;

        public WinJobObject()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimitInfo();
            info.BasicInfo.LimitFlags = KillOnJobClose;
            SetInformationJobObject(_handle, ExtendedInfoClass, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
        }

        public void Assign(Process p)
        {
            try { AssignProcessToJobObject(_handle, p.Handle); } catch { }
        }

        public void Dispose() => CloseHandle(_handle);
    }
}
