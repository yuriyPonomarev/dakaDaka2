using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Net.Sockets;
using System.Runtime.InteropServices;
#endif

namespace Emberdelve.Net
{
    /// <summary>
    /// Socket path "scan" of Docs/Tasks/UTP_RESET.md, Windows only.
    ///
    /// Windows reports an ICMP "port unreachable" (the peer's UDP port is closed) as WSAECONNRESET on the next receive of the
    /// UNCONNECTED UDP socket that sent the datagram, unless SIO_UDP_CONNRESET is turned off for that socket. A server that talks to
    /// many clients over one socket must never see that error: it is about one peer and says nothing about the socket. The leading
    /// hypothesis is that the transport's receive path does not survive it (the probe's baseline arm reproduces the fault, the
    /// scan arm tests the hypothesis).
    ///
    /// The transport owns the socket and exposes no handle, so this finds it from outside, on a BACKGROUND thread (never the main
    /// thread, never a time cap on the scan itself):
    ///   1. NtQuerySystemInformation(SystemExtendedHandleInformation) lists every handle of the system; the entries of THIS process are
    ///      kept. That covers handle values above 65536, which the first version of this guard (a blind scan of 4..65532 that timed out
    ///      after 3 s and found nothing) could not.
    ///   2. The object type index of a socket is learned from a throw-away managed UDP socket (sockets are "File" objects of the AFD
    ///      driver); only handles of that type are probed. Handles with an access mask known to hang name queries on synchronous
    ///      pipes are never touched. Probing runs on 4 worker threads so one handle that blocks cannot stop the scan.
    ///   3. Each probed handle: getsockopt(SO_TYPE) == SOCK_DGRAM, then getsockname for the port.
    ///   4. WSAIoctl(SIO_UDP_CONNRESET = 0x9800000C, FALSE) and WSAIoctl(SIO_UDP_NETRESET = 0x9800000F, FALSE) on EVERY datagram socket
    ///      of the process (not only the one on the server port): turning the notification off is harmless for any UDP socket, and it
    ///      makes the fix independent of whether the port could be read back. Server role: success = a socket bound to the server
    ///      port was changed. Client role (ephemeral port): success = any datagram socket was changed.
    /// Rounds repeat every 500 ms until success or the budget (15 s) is spent, because the socket may not exist yet.
    /// Log text goes through a queue the caller drains on its own thread. Off Windows nothing happens.
    /// </summary>
    public static class NetUdpResetGuard
    {
        /// <summary>The result of the whole attempt: <see cref="Line"/> is the log text, <see cref="Applied"/> the sockets changed.</summary>
        public sealed class Outcome
        {
            public int Applied;
            public string Line = "";
            public string Detail = "";
        }

        /// <summary>A running (or finished) scan. Thread-safe: the worker writes, the caller drains <see cref="TryDequeue"/>.</summary>
        public sealed class ScanJob
        {
            private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
            private volatile bool _done;
            private Outcome _outcome = new Outcome();

            public bool Done { get { return _done; } }
            public Outcome Result { get { return _outcome; } }

            public bool TryDequeue(out string line) { return _log.TryDequeue(out line); }
            internal void Post(string line) { _log.Enqueue(line); }
            internal void Finish(Outcome outcome) { _outcome = outcome; _done = true; }
        }

        /// <summary>
        /// Starts the scan. <paramref name="port"/> is the server's UDP port (server role) or 0 (client). Returns at once.
        /// </summary>
        public static ScanJob StartAsync(int port, bool serverRole, int budgetMs)
        {
            ScanJob job = new ScanJob();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            Thread worker = new Thread(() => Worker(job, port, serverRole, budgetMs)) { IsBackground = true, Name = "ember-udp-reset-scan" };
            worker.Start();
#else
            job.Finish(new Outcome { Line = "udp-connreset skipped: not a Windows build" });
#endif
            return job;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private const uint SioUdpConnReset = 0x9800000Cu;
        private const uint SioUdpNetReset = 0x9800000Fu;
        private const int SolSocket = 0xFFFF;
        private const int SoType = 0x1008;
        private const int SockDgram = 2;
        private const int SystemExtendedHandleInformation = 0x40;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int ProbeThreads = 4;

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAStartup(ushort versionRequested, byte[] wsaData);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSACleanup();

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int getsockname(UIntPtr socket, byte[] name, ref int nameLength);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int getsockopt(UIntPtr socket, int level, int optionName, out int optionValue, ref int optionLength);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAIoctl(UIntPtr socket, uint controlCode, ref int inBuffer, int inBufferLength,
                                           IntPtr outBuffer, int outBufferLength, out int bytesReturned, IntPtr overlapped, IntPtr completion);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int bufferLength, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        private struct HandleEntry
        {
            public ulong Value;
            public uint Access;
            public ushort TypeIndex;
        }

        private sealed class UdpSocket
        {
            public ulong Handle;
            public int Port;
            public int ConnResetRc;     // WSAIoctl result: 0 ok, else the WSA error
            public int NetResetRc;
        }

        private sealed class RoundResult
        {
            public int TotalHandles;
            public int OwnHandles;
            public int Candidates;
            public int SkippedHangMask;
            public int Probed;
            public int Sockets;
            public int Stalled;
            public int FileTypeIndex = -1;
            public readonly List<UdpSocket> Udp = new List<UdpSocket>();
            public readonly Dictionary<int, int> ProbeErrors = new Dictionary<int, int>();
            public string EnumerateError = "";
        }

        private static void Worker(ScanJob job, int port, bool serverRole, int budgetMs)
        {
            Outcome outcome = new Outcome();
            bool started = false;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                started = WSAStartup(0x0202, new byte[512]) == 0;
                RoundResult last = null;
                int round = 0;
                while (true)
                {
                    round++;
                    last = RunRound(port, serverRole);
                    job.Post("udp-connreset scan round=" + round + " " + Describe(last, port) + " elapsedMs=" + clock.ElapsedMilliseconds);

                    int portMatch = 0, anyOk = 0;
                    foreach (UdpSocket s in last.Udp)
                    {
                        if (s.ConnResetRc == 0) anyOk++;
                        if (s.ConnResetRc == 0 && port > 0 && s.Port == port) portMatch++;
                    }

                    bool success = serverRole ? portMatch > 0 : anyOk > 0;
                    if (success)
                    {
                        outcome.Applied = serverRole ? portMatch : anyOk;
                        outcome.Line = "udp-connreset disabled sockets=" + outcome.Applied + " port=" + port + " role=" + (serverRole ? "server" : "client");
                        outcome.Detail = Describe(last, port) + " rounds=" + round + " elapsedMs=" + clock.ElapsedMilliseconds;
                        break;
                    }

                    if (clock.ElapsedMilliseconds >= budgetMs)
                    {
                        outcome.Line = "udp-connreset NOT applied: " + (last.Udp.Count == 0
                            ? "no UDP socket found in this process"
                            : "no UDP socket on port " + port + " could be changed (see detail)") + " port=" + port;
                        outcome.Detail = Describe(last, port) + " rounds=" + round + " elapsedMs=" + clock.ElapsedMilliseconds;
                        break;
                    }

                    Thread.Sleep(500);
                }
            }
            catch (Exception e)
            {
                outcome.Line = "udp-connreset NOT applied: " + e.GetType().Name + " " + e.Message;
            }
            finally
            {
                if (started)
                {
                    try { WSACleanup(); } catch (Exception) { /* nothing to do */ }
                }

                job.Finish(outcome);
            }
        }

        private static string Describe(RoundResult r, int port)
        {
            StringBuilder b = new StringBuilder();
            b.Append("system-handles=").Append(r.TotalHandles).Append(" own=").Append(r.OwnHandles)
             .Append(" file-type-index=").Append(r.FileTypeIndex).Append(" candidates=").Append(r.Candidates)
             .Append(" skipped-hangmask=").Append(r.SkippedHangMask).Append(" probed=").Append(r.Probed)
             .Append(" sockets=").Append(r.Sockets).Append(" stalled=").Append(r.Stalled);
            if (r.EnumerateError.Length > 0) b.Append(" enumerate-error=").Append(r.EnumerateError);
            b.Append(" udp=[");
            for (int i = 0; i < r.Udp.Count; i++)
            {
                UdpSocket s = r.Udp[i];
                if (i > 0) b.Append(',');
                b.Append(s.Handle.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s.Port)
                 .Append(":connreset=").Append(s.ConnResetRc == 0 ? "off" : "err" + s.ConnResetRc)
                 .Append(":netreset=").Append(s.NetResetRc == 0 ? "off" : "err" + s.NetResetRc);
                if (port > 0 && s.Port == port) b.Append(":SERVER");
            }

            b.Append("] probe-errors=[");
            bool first = true;
            foreach (KeyValuePair<int, int> kv in r.ProbeErrors)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append("wsa").Append(kv.Key).Append('x').Append(kv.Value);
            }

            b.Append(']');
            return b.ToString();
        }

        private static RoundResult RunRound(int port, bool serverRole)
        {
            RoundResult result = new RoundResult();
            List<HandleEntry> own = new List<HandleEntry>();

            // The type index of a socket: a throw-away managed UDP socket is an object of the same type as the transport's.
            ulong referenceHandle = 0;
            Socket reference = null;
            try
            {
                reference = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                referenceHandle = (ulong)reference.Handle.ToInt64();
                try { EnumerateOwnHandles(own, result); }
                catch (Exception e) { result.EnumerateError = e.GetType().Name; }
            }
            finally
            {
                if (reference != null) reference.Dispose();
            }

            foreach (HandleEntry h in own)
                if (h.Value == referenceHandle) { result.FileTypeIndex = h.TypeIndex; break; }

            // Candidates: sockets (File type; AFD handles carry FILE_ALL_ACCESS 0x1F01FF) first, the rest of the File type second.
            List<HandleEntry> first = new List<HandleEntry>();
            List<HandleEntry> second = new List<HandleEntry>();
            foreach (HandleEntry h in own)
            {
                if (h.Value == referenceHandle) continue;
                if (result.FileTypeIndex >= 0 && h.TypeIndex != result.FileTypeIndex) continue;
                if (IsHangMask(h.Access)) { result.SkippedHangMask++; continue; }
                if (h.Access == 0x001F01FFu) first.Add(h); else second.Add(h);
            }

            // Fallback when the table could not be read (or the reference was not in it): the old blind range, still off the main thread.
            if (own.Count == 0)
            {
                for (int v = 4; v < 0x10000; v += 4) first.Add(new HandleEntry { Value = (ulong)v });
            }

            first.AddRange(second);
            result.Candidates = first.Count;
            ProbeAll(first, result, port);
            return result;
        }

        /// <summary>Access masks that make NtQueryObject-style calls block on synchronous pipe / file handles (the usual "handle lister" skip list).</summary>
        private static bool IsHangMask(uint access)
        {
            return access == 0x0012019Fu || access == 0x001A019Fu || access == 0x00120189u || access == 0x00100000u;
        }

        private static void EnumerateOwnHandles(List<HandleEntry> own, RoundResult result)
        {
            int size = 4 << 20;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int status;
                while (true)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        size = Math.Max(size * 2, needed + (1 << 20));
                        if (size > (256 << 20)) { result.EnumerateError = "table>256MB"; return; }
                        continue;
                    }

                    break;
                }

                if (status < 0) { result.EnumerateError = "status0x" + status.ToString("X8", CultureInfo.InvariantCulture); return; }

                // SYSTEM_HANDLE_INFORMATION_EX: ULONG_PTR count, ULONG_PTR reserved, then entries of
                //   PVOID Object; ULONG_PTR Pid; ULONG_PTR Handle; ULONG Access; USHORT BackTrace; USHORT TypeIndex; ULONG Attributes; ULONG Reserved
                int p = IntPtr.Size;
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                int entrySize = 3 * p + 16;
                int header = 2 * p;
                uint self = GetCurrentProcessId();
                result.TotalHandles = (int)Math.Min(count, int.MaxValue);
                for (long i = 0; i < count; i++)
                {
                    long offset = header + i * entrySize;
                    if (offset + entrySize > size) break;
                    IntPtr entry = IntPtr.Add(buffer, (int)offset);
                    ulong pid = (ulong)Marshal.ReadIntPtr(entry, p).ToInt64();
                    if (pid != self) continue;
                    own.Add(new HandleEntry
                    {
                        Value = (ulong)Marshal.ReadIntPtr(entry, 2 * p).ToInt64(),
                        Access = (uint)Marshal.ReadInt32(entry, 3 * p),
                        TypeIndex = (ushort)Marshal.ReadInt16(entry, 3 * p + 6)
                    });
                }

                result.OwnHandles = own.Count;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static void ProbeAll(List<HandleEntry> candidates, RoundResult result, int port)
        {
            int next = -1;
            int probed = 0;
            object sync = new object();
            Thread[] threads = new Thread[ProbeThreads];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    while (true)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= candidates.Count) return;
                        HandleEntry h = candidates[i];
                        UIntPtr handle = new UIntPtr(h.Value);
                        int type;
                        int typeLength = sizeof(int);
                        int rc = getsockopt(handle, SolSocket, SoType, out type, ref typeLength);
                        int err = rc == 0 ? 0 : Marshal.GetLastWin32Error();
                        Interlocked.Increment(ref probed);
                        if (rc != 0)
                        {
                            lock (sync)
                            {
                                int n; result.ProbeErrors.TryGetValue(err, out n); result.ProbeErrors[err] = n + 1;
                            }

                            continue;
                        }

                        lock (sync) { result.Sockets++; }
                        if (type != SockDgram) continue;

                        UdpSocket udp = new UdpSocket { Handle = h.Value };
                        byte[] address = new byte[128];
                        int length = address.Length;
                        if (getsockname(handle, address, ref length) == 0 && length >= 4) udp.Port = (address[2] << 8) | address[3];

                        int off = 0, returned;
                        udp.ConnResetRc = WSAIoctl(handle, SioUdpConnReset, ref off, sizeof(int), IntPtr.Zero, 0, out returned, IntPtr.Zero, IntPtr.Zero) == 0
                            ? 0 : Marshal.GetLastWin32Error();
                        int off2 = 0;
                        udp.NetResetRc = WSAIoctl(handle, SioUdpNetReset, ref off2, sizeof(int), IntPtr.Zero, 0, out returned, IntPtr.Zero, IntPtr.Zero) == 0
                            ? 0 : Marshal.GetLastWin32Error();
                        lock (sync) { result.Udp.Add(udp); }
                    }
                }) { IsBackground = true, Name = "ember-udp-reset-probe" };
                threads[t].Start();
            }

            // Wait for the queue to drain. A thread stuck in a blocking call is abandoned after 10 s without progress (the others carry on).
            int lastProbed = -1;
            Stopwatch noProgress = Stopwatch.StartNew();
            while (true)
            {
                bool alive = false;
                foreach (Thread t in threads) if (t.IsAlive) { alive = true; break; }
                if (!alive) break;
                int now = Volatile.Read(ref probed);
                if (now != lastProbed) { lastProbed = now; noProgress.Restart(); }
                else if (noProgress.ElapsedMilliseconds > 10000)
                {
                    foreach (Thread t in threads) if (t.IsAlive) result.Stalled++;
                    break;
                }

                Thread.Sleep(10);
            }

            result.Probed = Volatile.Read(ref probed);
        }
#endif
    }
}
