﻿using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlameStreamBackend.Services
{
    public class TranscodeRegistry : BackgroundService
    {
        private readonly ConcurrentDictionary<string, Process> _procs = new();
        private readonly SemaphoreSlim _limit; // e.g., max 2 concurrent jobs
        public TranscodeRegistry(int maxConcurrent = 2) => _limit = new(maxConcurrent);

        public async Task<Process> StartAsync(string key, ProcessStartInfo psi)
        {
            await _limit.WaitAsync();
            psi.RedirectStandardError = true;  // avoid deadlocks
            psi.RedirectStandardInput = false;
            psi.ArgumentList.Add("-nostdin");  // ffmpeg won’t wait for input
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            _procs[key] = p;
            p.Exited += (_, __) => {
                // Only release semaphore if the process was still tracked here
                if (_procs.TryRemove(key, out _))
                    _limit.Release();
            };
            // optional: read stderr asynchronously to keep buffer clear
            _ = Task.Run(() => { try { p.StandardError.ReadToEnd(); } catch { } });
            return p;
        }

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
            foreach (var kv in _procs.Keys) Stop(kv);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }
    }
}
