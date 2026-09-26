using System.Runtime.InteropServices;

namespace Unisono;

/// <summary>
/// Looks for content that changed without anyone changing it (disk or transfer damage): every synced file whose size
/// and date are as recorded at its last sync is read and its MD5 compared with the recorded one; the remote side's
/// checksum (Drive) or file (folder pairs) is compared too. Only reads, so it runs beside the sync cycles on a
/// snapshot of the state; online-only files are skipped. It pauses while the computer runs on battery.
/// </summary>
public sealed class ContentCheck
{
    public sealed record Problem(string Rel, string Side, string Detail);

    public sealed class Report
    {
        public DateTime Finished { get; set; }
        public int Checked { get; set; }
        public long Bytes { get; set; }
        public List<Problem> Problems { get; } = new();
        public bool Cancelled { get; set; }
    }

    public static async Task<Report> RunAsync(SyncPair pair, IReadOnlyList<StateEntry> entries, IReadOnlyDictionary<string, RemoteNode> remote,
        Log log, CancellationToken ct)
    {
        var report = new Report();
        var root = pair.LocalFullPath;
        foreach (var e in entries)
        {
            if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
            while (OnBattery() && !ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromMinutes(1), ct).ContinueWith(_ => { });
            var path = Path.Combine(root, e.Path.Replace('/', '\\'));
            try
            {
                var fi = new FileInfo(path);
                // Changed since the sync (the next cycle takes care of it), online-only, or gone: nothing to compare.
                if (!fi.Exists || CloudFiles.IsDehydrated(fi.Attributes) || fi.Length != e.LocalSize || fi.LastWriteTimeUtc.Ticks != e.LocalMtimeTicks) continue;
                var md5 = await Task.Run(() => PathUtil.Md5Of(path), ct);
                fi.Refresh();
                if (fi.Length != e.LocalSize || fi.LastWriteTimeUtc.Ticks != e.LocalMtimeTicks) continue; // written meanwhile
                report.Checked++;
                report.Bytes += fi.Length;
                if (e.Md5 != null && !md5.Equals(e.Md5, StringComparison.OrdinalIgnoreCase))
                    report.Problems.Add(new(e.Path, "here", "the content differs from what was synced, although size and date are unchanged"));

                if (!remote.TryGetValue(e.Path, out var node)) continue;
                var remoteMd5 = node.Md5;
                if (remoteMd5 == null && pair.Kind == RemoteKind.Folder && !pair.Encrypted) // encrypted: the other file is ciphertext
                {
                    var other = Path.Combine(pair.RemoteFullPath, e.Path.Replace('/', '\\'));
                    var oi = new FileInfo(other);
                    if (oi.Exists && oi.Length == e.Size && oi.LastWriteTimeUtc == e.DriveModifiedUtc)
                        remoteMd5 = await Task.Run(() => PathUtil.Md5Of(other), ct);
                }
                if (remoteMd5 != null && e.Md5 != null && node.ModifiedUtc == e.DriveModifiedUtc && !remoteMd5.Equals(e.Md5, StringComparison.OrdinalIgnoreCase))
                    report.Problems.Add(new(e.Path, "remote", "the other side's content differs from what was synced, although its date is unchanged"));
            }
            catch (OperationCanceledException) { report.Cancelled = true; break; }
            catch (Exception ex) { log.Debug($"[{pair.Name}] content check {e.Path}: {ex.Message}"); } // open elsewhere etc.
        }
        report.Finished = DateTime.Now;
        return report;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>True when running on battery (ACLineStatus 0); desktops and unknown states count as mains power.</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
}
