using System;
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
    /// Automatically scans and manages ID-to-Type mappings for classes decorated with the [Packet] attribute.
    /// </summary>
    public static class PacketRegistry {
        private static readonly PairMap<string, Type> _packetTypes = new();

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
            }
        }

        /// <summary>Gets the packet type associated with the given ID.</summary>
        public static Type GetPacketType(string id)
            => _packetTypes.TryGetByFirst(id, out var type) ? type : null;

        /// <summary>Gets the packet ID associated with the given type.</summary>
        public static string GetPacketId(Type type)
            => _packetTypes.TryGetBySecond(type, out var id) ? id : null;
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