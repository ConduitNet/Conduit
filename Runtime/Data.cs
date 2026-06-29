using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using ConduitNet.Packets;
using ConduitNet.Utility;
using MessagePack;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ConduitNet {
    /// <summary>Data channel send options combining ordering and reliability guarantees.</summary>
    public enum SendOption : byte {
        /// <summary>Guarantees both order and reliability.</summary>
        OrderedReliable = 0b00,
        /// <summary>Guarantees order but not reliability.</summary>
        OrderedUnreliable = 0b01,
        /// <summary>Guarantees reliability but not order.</summary>
        UnorderedReliable = 0b10,
        /// <summary>Guarantees neither order nor reliability.</summary>
        UnorderedUnreliable = 0b11
    }

    public enum TargetGroup {
        Others,
        All
    }

    public readonly struct BytesContext {
        public readonly IUser Sender;
        public readonly ReadOnlyMemory<byte> Data;
        public readonly SendOption Option;

        public BytesContext(IUser sender, ReadOnlyMemory<byte> data, SendOption option) {
            Sender = sender;
            Data = data;
            Option = option;
        }
    }

    public readonly struct SignalContext {
        public readonly IUser Sender;
        public readonly long Timestamp;
        public readonly SendOption Option;

        public SignalContext(IUser sender, long timestamp, SendOption option) {
            Sender = sender;
            Timestamp = timestamp;
            Option = option;
        }
    }

    public readonly struct PacketContext<T> {
        public readonly IUser Sender;
        public readonly long Timestamp;
        public readonly T Packet;
        public readonly SendOption Option;

        public PacketContext(IUser sender, long timestamp, T packet, SendOption option) {
            Sender = sender;
            Timestamp = timestamp;
            Packet = packet;
            Option = option;
        }
    }

    /// <summary>Handler for receiving byte array data.</summary>
    public delegate void BytesHandler(BytesContext context);
    /// <summary>Handler for receiving named signals.</summary>
    public delegate void SignalHandler(SignalContext context);
    /// <summary>Handler for receiving strongly-typed packets.</summary>
    public delegate void PacketHandler<T>(PacketContext<T> context);

    /// <summary>Marks a method as a byte data handler.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class BytesHandlerAttribute : Attribute { }

    /// <summary>Marks a method as a signal handler for a specific signal name.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class SignalHandlerAttribute : Attribute {
        public string SignalName { get; }
        public SignalHandlerAttribute(string signalName) {
            SignalName = signalName;
        }
    }

    /// <summary>Marks a method as a packet handler for a specific packet type.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class PacketHandlerAttribute : Attribute {
        public Type PacketType { get; }
        public PacketHandlerAttribute(Type packetType) {
            PacketType = packetType;
        }
    }

    /// <summary>Role options used by RequireRoleAttribute for sender/receiver filtering.</summary>
    public enum Role {
        /// <summary>Match any role (default).</summary>
        Any,
        /// <summary>Match only the host.</summary>
        Host,
        /// <summary>Match only non-host members.</summary>
        Member
    }

    /// <summary>
    /// Filters handler invocations by sender and/or receiver (self) role.
    /// Can be combined with any handler attribute ([BytesHandler], [SignalHandler], [PacketHandler]).
    /// <code>[RequireRole(sender: Role.Host)]</code>
    /// <code>[RequireRole(receiver: Role.Member)]</code>
    /// <code>[RequireRole(sender: Role.Host, receiver: Role.Member)]</code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class RequireRoleAttribute : Attribute {
        public Role Sender   { get; }
        public Role Receiver { get; }
        public RequireRoleAttribute(Role sender = Role.Any, Role receiver = Role.Any) {
            Sender   = sender;
            Receiver = receiver;
        }
    }

    internal enum DataType : byte {
        Byte = 0b00,
        Signal = 0b01,
        Packet = 0b10,
        Error = 0b11,
    }

    /// <summary>Internal system message flag. When bit 0 of the prefix byte is set, the message is an internal system message.</summary>
    internal static class InternalFlag {
        public const byte Mask = 0b0000_0001;
    }

    /// <summary>Sub-types for internal system messages (stored in upper 2 bits of the prefix byte, same position as DataType).</summary>
    internal enum InternalMsgType : byte {
        TimeSyncRequest = 0b00,
        TimeSyncResponse = 0b01,
    }

    [MessagePackObject]
    public struct DataEndPoint {
        [Key(0)]
        public string sender;
        [Key(1)]
        public string receiver;
    }

    [Serializable]
    internal class SignalingMessage {
        public SignalingMsgType type;
        public string from;
        public string to;
        public string body;
    }

    [Serializable]
    internal class OfferAnswerData {
        public string sdp;
        public string type;
    }

    [Serializable]
    internal class IceCandidateData {
        public string candidate;
        public string sdpMid;
        public NullableInt sdpMLineIndex;
    }

    /// <summary>
    /// Global configuration for Conduit. Must be set before calling Init.
    /// 'serverUrl' and 'stunServers' are required.
    /// </summary>
    public class ConduitConfig {
        /// <summary>URL of the signaling/API server.</summary>
        public string ServerUrl { get; }
        /// <summary>List of STUN servers (required).</summary>
        public StunServer[] StunServers { get; }
        /// <summary>List of TURN servers (optional).</summary>
        public TurnServer[] TurnServers { get; }

        /// <summary>Whether to use HTTPS for API requests.</summary>
        public bool UseHttps { get; set; } = false;
        /// <summary>Whether to use WSS for WebSocket connections.</summary>
        public bool UseWss { get; set; } = false;
        /// <summary>Enable debug logging.</summary>
        public bool DebugLog { get; set; } = false;
        /// <summary>Query key for user ID.</summary>
        public string UserIdQueryKey { get; set; } = "userId";
        /// <summary>Query key for lobby ID.</summary>
        public string LobbyIdQueryKey { get; set; } = "lobbyId";
        public string LobbyCodeQueryKey { get; set; } = "lobbyCode";
        /// <summary>Lobby join timeout in seconds.</summary>
        public float JoinTimeout { get; set; } = 10f;
        /// <summary>API path for syncing user. {0} is API URL, {1} is user ID.</summary>
        public string SyncUserPath { get; set; } = "{0}/user/sync/{1}";
        /// <summary>API path for health check. {0} is API URL.</summary>
        public string HealthPath { get; set; } = "{0}/health";
        /// <summary>API path for server status check. {0} is API URL.</summary>
        public string StatusPath { get; set; } = "{0}/status";
        /// <summary>Packet serializer to use. Defaults to JsonPacketSerializer.</summary>
        public IPacketSerializer PacketSerializer { get; set; } = new JsonPacketSerializer();

        /// <summary>Whether to use AssemblyQualifiedName for type serialization (default: false -> FullName).</summary>
        public bool UseAssemblyQualifiedNameForTypes = false;
        /// <summary>Maximum time(ms) allowed to process data channel messages per frame.</summary>
        public float MaxDataChannelProcessingTimeMs { get; set; } = 5f;

        /// <summary>Number of samples to collect during time synchronization (default: 5).</summary>
        public int TimeSyncSamples { get; set; } = 5;
        /// <summary>Interval between time sync samples in seconds (default: 0.1).</summary>
        public float TimeSyncInterval { get; set; } = 0.1f;
        /// <summary>Timeout for time sync response in seconds (default: 5).</summary>
        public float TimeSyncTimeout { get; set; } = 5f;

        public ConduitConfig(string serverUrl, StunServer[] stunServers, TurnServer[] turnServers = null) {
            ServerUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
            StunServers = stunServers ?? throw new ArgumentNullException(nameof(stunServers));
            TurnServers = turnServers;
        }
    }

    /// <summary>STUN server configuration.</summary>
    public struct StunServer {
        public string[] urls;
    }

    /// <summary>TURN server configuration.</summary>
    public struct TurnServer {
        public string[] urls;
        public string username;
        public string credential;
    }

    /// <summary>
    /// Exception received from a remote peer. Thrown if the original exception type
    /// cannot be resolved or instantiated on the receiving side.
    /// </summary>
    public class RemoteException : Exception {
        /// <summary>The user who sent the exception.</summary>
        public IUser Sender { get; }
        /// <summary>The type name of the original exception thrown remotely.</summary>
        public string RemoteTypeName { get; }

        public RemoteException(IUser sender, string message, string remoteTypeName)
            : base($"[{remoteTypeName}] {message}") {
            Sender = sender;
            RemoteTypeName = remoteTypeName;
        }

        public RemoteException(IUser sender, Exception inner)
            : base($"Remote exception from {sender.Id}: {inner.Message}", inner) {
            Sender = sender;
            RemoteTypeName = inner.GetType().FullName;
        }
    }
}