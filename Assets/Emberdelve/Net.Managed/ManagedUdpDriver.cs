// OPTIONAL assembly (defineConstraint EMBER_UTP_MANAGED_SOCKET), socket path "managed" of Docs/Tasks/UTP_RESET.md.
//
// A UTP INetworkInterface over System.Net.Sockets.Socket, installed as the NGO UnityTransport driver constructor. It exists to
// leave the package's baselib socket out of the picture: SIO_UDP_CONNRESET is switched off on a socket we own, and a stray
// WSAECONNRESET on receive is swallowed instead of ending the receive path.
//
// NOT COMPILED OR RUN where this was written. Every member of the Unity.Networking.Transport / NGO API used below is written from
// memory of transport 2.x / NGO 2.x and is marked "API?" where I am not sure. If this file does not compile, remove the define
// (Emberdelve/Net/Toggle managed UDP socket define): the rest of the project does not depend on this assembly.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine.Scripting;

namespace Emberdelve.Net.Managed
{
    /// <summary>
    /// The unmanaged handle UTP stores (NetworkDriver.Create wants an unmanaged INetworkInterface struct). The managed socket lives in
    /// <see cref="ManagedUdpDriver"/>'s static table under <see cref="Id"/>.
    /// </summary>
    public struct ManagedUdpInterface : INetworkInterface
    {
        public int Id;

        public NetworkEndpoint LocalEndpoint { get { return ManagedUdpDriver.LocalEndpointOf(Id); } }

        public int Initialize(ref NetworkSettings settings, ref int packetPadding)
        {
            return 0; // API? (ref NetworkSettings, ref int) as in transport 2.x. No padding needed by this interface.
        }

        public JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep)
        {
            // Managed sockets cannot run in a Burst job: finish what UTP queued before us, then work on the main thread.
            dep.Complete();
            ManagedUdpDriver.ReceiveInto(Id, ref arguments.ReceiveQueue); // API? ReceiveJobArguments.ReceiveQueue : PacketsQueue
            return default;
        }

        public JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep)
        {
            dep.Complete();
            ManagedUdpDriver.SendFrom(Id, ref arguments.SendQueue); // API? SendJobArguments.SendQueue : PacketsQueue
            return default;
        }

        public int Bind(NetworkEndpoint endpoint)
        {
            return ManagedUdpDriver.BindSocket(Id, endpoint);
        }

        public int Listen()
        {
            return 0; // UDP: nothing to do
        }

        public void Dispose()
        {
            ManagedUdpDriver.CloseSocket(Id);
        }
    }

    [Preserve]
    public sealed class ManagedUdpDriver : INetworkStreamDriverConstructor // API? public interface of NGO 2.x UnityTransport
    {
        private sealed class State
        {
            public Socket Socket;
            public IPEndPoint Local;
            public readonly byte[] Buffer = new byte[2048];
            public EndPoint Remote = new IPEndPoint(IPAddress.Any, 0);
            public string BindInfo = "not bound";
            public long Received, Sent, ResetsSwallowed, RecvErrors, SendErrors, QueueFull;
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        private static int _nextId;
        private static string _last = "managed-udp: never created";

        /// <summary>One-line state of the most recent interface (bind, ioctl, counters). Safe to call at any time.</summary>
        public static string Describe()
        {
            lock (States)
            {
                State s = null;
                foreach (KeyValuePair<int, State> kv in States) s = kv.Value; // the newest
                if (s == null) return _last;
                return "managed-udp " + s.BindInfo + " rx=" + s.Received + " tx=" + s.Sent + " connreset-swallowed=" + s.ResetsSwallowed +
                       " rx-errors=" + s.RecvErrors + " tx-errors=" + s.SendErrors + " rx-queue-full=" + s.QueueFull;
            }
        }

        /// <summary>
        /// Makes <paramref name="transport"/> build its driver with the managed interface. Returns null on success, else the reason
        /// (the caller then stays on the stock socket). Reflection is used for the hook because I am unsure whether it is the
        /// instance property DriverConstructor or the static field s_DriverConstructor in the installed NGO.
        /// </summary>
        public static string Install(UnityTransport transport)
        {
            ManagedUdpDriver constructor = new ManagedUdpDriver();
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                       System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.PropertyInfo property = typeof(UnityTransport).GetProperty("DriverConstructor", all);
            if (property != null && property.CanWrite)
            {
                property.SetValue(transport, constructor, null);
                return null;
            }

            System.Reflection.FieldInfo field = typeof(UnityTransport).GetField("s_DriverConstructor", all);
            if (field != null && field.IsStatic)
            {
                field.SetValue(null, constructor);
                return null;
            }

            return "no DriverConstructor property or s_DriverConstructor field on UnityTransport";
        }

        // ---- INetworkStreamDriverConstructor ---------------------------------------------------

        // API? signature as given by the task owner: (UnityTransport, out NetworkDriver, out NetworkPipeline x3).
        public void CreateDriver(UnityTransport transport, out NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline)
        {
            NetworkSettings settings = new NetworkSettings(Allocator.Temp);
            try
            {
                // API? named parameters of WithNetworkConfigParameters (transport 2.x).
                settings.WithNetworkConfigParameters(
                    connectTimeoutMS: transport.ConnectTimeoutMS,
                    maxConnectAttempts: transport.MaxConnectAttempts,
                    disconnectTimeoutMS: transport.DisconnectTimeoutMS,
                    heartbeatTimeoutMS: transport.HeartbeatTimeoutMS);
                settings.WithFragmentationStageParameters(payloadCapacity: transport.MaxPayloadSize); // API? as NGO's default does
                settings.WithReliableStageParameters(windowSize: 64);                                  // API? NGO's default window

                int id;
                lock (States)
                {
                    id = ++_nextId;
                    States[id] = new State();
                }

                driver = NetworkDriver.Create(new ManagedUdpInterface { Id = id }, settings); // API? Create<N>(N, NetworkSettings)

                // API? pipelines as I remember NGO's default constructor builds them; reliable gets fragmentation in front so a
                // message larger than one datagram cannot be refused.
                unreliableFragmentedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage));
                unreliableSequencedFragmentedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(UnreliableSequencedPipelineStage));
                reliableSequencedPipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
            }
            finally
            {
                settings.Dispose();
            }
        }

        // ---- the socket ------------------------------------------------------------------------

        private static State Get(int id)
        {
            lock (States)
            {
                State s;
                return States.TryGetValue(id, out s) ? s : null;
            }
        }

        internal static NetworkEndpoint LocalEndpointOf(int id)
        {
            State s = Get(id);
            if (s == null || s.Local == null) return default;
            return ToNative(s.Local);
        }

        internal static int BindSocket(int id, NetworkEndpoint endpoint)
        {
            State s = Get(id);
            if (s == null) return -1;
            try
            {
                IPEndPoint target = ToManaged(endpoint);
                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Blocking = false;
                socket.ReceiveBufferSize = 1 << 20;
                socket.SendBufferSize = 1 << 20;
                string ioctl = "ioctl=n/a";
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                try
                {
                    socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);   // SIO_UDP_CONNRESET off
                    ioctl = "connreset=off";
                    socket.IOControl(unchecked((int)0x9800000F), new byte[] { 0, 0, 0, 0 }, null); // SIO_UDP_NETRESET off
                    ioctl += " netreset=off";
                }
                catch (Exception e) { ioctl = "ioctl-failed " + e.GetType().Name; }
#endif
                socket.Bind(target);
                s.Socket = socket;
                s.Local = (IPEndPoint)socket.LocalEndPoint;
                s.BindInfo = "bound=" + s.Local + " " + ioctl;
                return 0;
            }
            catch (Exception e)
            {
                s.BindInfo = "bind-failed " + e.GetType().Name + " " + e.Message;
                return -1;
            }
        }

        internal static void CloseSocket(int id)
        {
            lock (States)
            {
                State s;
                if (!States.TryGetValue(id, out s)) return;
                _last = "managed-udp(closed) " + s.BindInfo + " rx=" + s.Received + " tx=" + s.Sent + " connreset-swallowed=" + s.ResetsSwallowed;
                States.Remove(id);
                try { if (s.Socket != null) s.Socket.Close(); } catch (Exception) { /* closing */ }
            }
        }

        internal static unsafe void ReceiveInto(int id, ref PacketsQueue queue)
        {
            State s = Get(id);
            if (s == null || s.Socket == null) return;
            for (int guard = 0; guard < 512; guard++)
            {
                if (queue.Count >= queue.Capacity) { s.QueueFull++; return; } // API? Count / Capacity. Leave the rest in the kernel.
                int n;
                try
                {
                    if (!s.Socket.Poll(0, SelectMode.SelectRead)) return;
                    s.Remote = new IPEndPoint(IPAddress.Any, 0);
                    n = s.Socket.ReceiveFrom(s.Buffer, 0, s.Buffer.Length, SocketFlags.None, ref s.Remote);
                }
                catch (SocketException e)
                {
                    // The point of this path: a connection reset (ICMP port unreachable) about ONE peer must not end the socket.
                    if (e.SocketErrorCode == SocketError.ConnectionReset) { s.ResetsSwallowed++; continue; }
                    if (e.SocketErrorCode == SocketError.WouldBlock) return;
                    s.RecvErrors++;
                    return;
                }
                catch (ObjectDisposedException) { return; }

                if (n <= 0) continue;
                PacketProcessor packet;
                if (!queue.EnqueuePacket(out packet)) { s.QueueFull++; return; } // API?
                if (n > packet.Capacity) { s.RecvErrors++; packet.DropPacket(); continue; } // API? Capacity / DropPacket
                fixed (byte* src = s.Buffer)
                    UnsafeUtility.MemCpy(packet.GetUnsafePayloadPtr(), src, n); // API? GetUnsafePayloadPtr()
                packet.EndpointRef = ToNative((IPEndPoint)s.Remote);            // API? EndpointRef (ref property)
                packet.SetUnsafeMetadata(n);                                     // API? SetUnsafeMetadata(int length)
                s.Received++;
            }
        }

        internal static unsafe void SendFrom(int id, ref PacketsQueue queue)
        {
            State s = Get(id);
            if (s == null || s.Socket == null) return;
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                PacketProcessor packet = queue[i]; // API? indexer
                int length = packet.Length;         // API?
                if (length <= 0 || length > s.Buffer.Length) continue;
                try
                {
                    Marshal.Copy((IntPtr)packet.GetUnsafePayloadPtr(), s.Buffer, 0, length);
                    s.Socket.SendTo(s.Buffer, 0, length, SocketFlags.None, ToManaged(packet.EndpointRef)); // API? EndpointRef
                    s.Sent++;
                }
                catch (SocketException) { s.SendErrors++; }
                catch (ObjectDisposedException) { return; }
            }
            // API? The driver clears the send queue itself (as I remember the baselib interface not doing it either).
        }

        // ---- endpoints (IPv4 only) -------------------------------------------------------------

        private static NetworkEndpoint ToNative(IPEndPoint ep)
        {
            NetworkEndpoint native;
            if (NetworkEndpoint.TryParse(ep.Address.ToString(), (ushort)ep.Port, out native)) return native; // API? TryParse(string, ushort, out)
            return default;
        }

        private static IPEndPoint ToManaged(NetworkEndpoint ep)
        {
            string text = ep.ToString(); // API? "a.b.c.d:port" in transport 2.x
            int colon = text.LastIndexOf(':');
            string address = colon > 0 ? text.Substring(0, colon) : text;
            return new IPEndPoint(IPAddress.Parse(address), ep.Port); // API? ep.Port : ushort
        }
    }
}
