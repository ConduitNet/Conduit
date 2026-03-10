using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ConduitNet;
using ConduitNet.Utility;
using Newtonsoft.Json;

namespace ConduitNet.Packets {
    /// <summary>Interface for packet serialization and deserialization.</summary>
    public interface IPacketSerializer {
        /// <summary>Serializes an object into a byte array.</summary>
        public byte[] Serialize(object obj, Type type);
        /// <summary>Deserializes a byte array into an object.</summary>
        public object Deserialize(ReadOnlyMemory<byte> bytes, Type type);
    }

    /// <summary>Default JSON-based packet serializer.</summary>
    public class JsonPacketSerializer : IPacketSerializer {

        public object Deserialize(ReadOnlyMemory<byte> bytes, Type type) {
            string json = Encoding.UTF8.GetString(bytes.Span);
            return JsonConvert.DeserializeObject(json, type);
        }

        public byte[] Serialize(object obj, Type type) {
            string json = JsonConvert.SerializeObject(obj);
            return Encoding.UTF8.GetBytes(json);
        }
    }

    /// <summary>
    /// Common base marker interface for all network packets.
    /// Use <see cref="IPacket"/> or <see cref="IAutoRelayPacket"/> on your packet types;
    /// this interface exists so that handler registration APIs can accept both.
    /// </summary>
    public interface INetworkPacket { }

    /// <summary>
    /// Marker interface for standard (non-relayed) network packets.
    /// Use with <see cref="PacketAttribute"/> for compile-time enforcement in
    /// <c>Conduit.SendPacket</c>, <c>BroadcastPacket</c>, etc.
    /// </summary>
    public interface IPacket : INetworkPacket { }

    /// <summary>
    /// Marker interface for packets that should be automatically relayed by the host to all peers.
    /// Implementing this interface (together with <see cref="PacketAttribute"/>) enables compile-time
    /// enforcement: only <c>IAutoRelayPacket</c> types are accepted by <c>Conduit.SendAutoRelayPacket</c>.
    /// </summary>
    public interface IAutoRelayPacket : INetworkPacket { }

    /// <summary>
    /// Automatically scans and manages ID-to-Type mappings for classes decorated with the [Packet] attribute.
    /// </summary>
    public static class PacketRegistry {
        private static readonly PairMap<string, Type> _packetTypes = new();
        private static readonly HashSet<Type> _autoRelayTypes = new();

        static PacketRegistry() {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes()).Where(t => t.IsDefined(typeof(PacketAttribute)));

            foreach (var type in types) {
                var packetAttr = type.GetCustomAttribute<PacketAttribute>();
                string id = packetAttr.Id ?? type.FullName;

                if (_packetTypes.ContainsFirst(id)) {
                    throw new InvalidOperationException(
                        $"Packet ID '{id}' is already registered by type '{_packetTypes[id].FullName}'. "
                    );
                }

                _packetTypes[id] = type;

                if (typeof(IAutoRelayPacket).IsAssignableFrom(type))
                    _autoRelayTypes.Add(type);
            }
        }

        /// <summary>Gets the packet type associated with the given ID.</summary>
        public static Type GetPacketType(string id)
            => _packetTypes.TryGetByFirst(id, out var type) ? type : null;

        /// <summary>Gets the packet ID associated with the given type.</summary>
        public static string GetPacketId(Type type)
            => _packetTypes.TryGetBySecond(type, out var id) ? id : null;

        /// <summary>Returns true if the given packet type implements <see cref="IAutoRelayPacket"/>.</summary>
        public static bool IsAutoRelay(Type type)
            => _autoRelayTypes.Contains(type);
    }

    /// <summary>
    /// Registers a class or struct as a network packet.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class PacketAttribute : Attribute {
        /// <summary>The packet identifier (falls back to FullName if null).</summary>
        public string Id { get; }
        public PacketAttribute(string id = null) {
            Id = id;
        }
    }
}