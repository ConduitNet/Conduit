using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using ConduitNet;
using MessagePack;

namespace ConduitNet.Utility {
    public static class QueryParamBuilder {
        public static string BuildUrl(string baseUrl, IDictionary<string, object> parameters) {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;

            string cleanedUrl = baseUrl.TrimEnd('/');
            if (parameters == null || parameters.Count == 0) return cleanedUrl;

            var sb = new StringBuilder();

            foreach (var kvp in parameters) {
                if (kvp.Value == null) continue;

                // 배열 또는 리스트인지 확인 (문자열 제외)
                if (kvp.Value is IEnumerable enumerable && !(kvp.Value is string)) {
                    foreach (var item in enumerable) {
                        if (item == null) continue;
                        // 배열 요소이므로 isArray를 true로 전달
                        AppendParam(sb, kvp.Key, item, isArray: true);
                    }
                }
                else {
                    // 단일 값이므로 isArray를 false로 전달
                    AppendParam(sb, kvp.Key, kvp.Value, isArray: false);
                }
            }

            if (sb.Length == 0) return cleanedUrl;

            return $"{cleanedUrl}?{sb.ToString().TrimEnd('&')}";
        }

        private static void AppendParam(StringBuilder sb, string key, object value, bool isArray) {
            // 1. 키 처리: 배열이면 뒤에 []를 붙임
            string finalKey = isArray ? $"{key}[]" : key;

            // 2. 값 처리: 날짜 포맷팅 등
            string stringValue;
            if (value is DateTime dt) stringValue = dt.ToString("yyyy-MM-ddTHH:mm:ssZ");
            else if (value is DateTimeOffset dto) stringValue = dto.ToString("yyyy-MM-ddTHH:mm:ssK");
            else stringValue = value.ToString();

            // 3. 인코딩 및 추가
            sb.Append($"{WebUtility.UrlEncode(finalKey)}={WebUtility.UrlEncode(stringValue)}&");
        }
    }

    internal static class Utils {
        /// <summary>
        /// rawdata에서 offset 위치의 int 값을 배열 복사 없이 직접 읽습니다.
        /// </summary>
        public static int ReadInt32(byte[] rawdata, int offset) {
            return rawdata[offset]
                 | (rawdata[offset + 1] << 8)
                 | (rawdata[offset + 2] << 16)
                 | (rawdata[offset + 3] << 24);
        }

        /// <summary>
        /// rawdata에서 offset 위치의 long 값을 배열 복사 없이 직접 읽습니다.
        /// </summary>
        public static long ReadInt64(byte[] rawdata, int offset) {
            return (long)rawdata[offset]
                 | ((long)rawdata[offset + 1] << 8)
                 | ((long)rawdata[offset + 2] << 16)
                 | ((long)rawdata[offset + 3] << 24)
                 | ((long)rawdata[offset + 4] << 32)
                 | ((long)rawdata[offset + 5] << 40)
                 | ((long)rawdata[offset + 6] << 48)
                 | ((long)rawdata[offset + 7] << 56);
        }

        /// <summary>
        /// List&lt;byte&gt;에 int 값을 배열 할당 없이 직접 씁니다.
        /// </summary>
        private static void WriteInt32(List<byte> seq, int value) {
            seq.Add((byte)value);
            seq.Add((byte)(value >> 8));
            seq.Add((byte)(value >> 16));
            seq.Add((byte)(value >> 24));
        }

        public static object ParseData(Type type, ref int offset, byte[] rawdata) {
            int len = ReadInt32(rawdata, offset);
            offset += sizeof(int);
            var memory = new ReadOnlyMemory<byte>(rawdata, offset, len);
            offset += len;
            return MessagePackSerializer.Deserialize(type, memory);
        }

        public static void AppendData<T>(ref List<byte> seq, T data) {
            byte[] bytes = MessagePackSerializer.Serialize(data);
            WriteInt32(seq, bytes.Length);
            seq.AddRange(bytes);
        }

        /// <summary>
        /// 데이터를 MessagePack으로 직렬화하고 4바이트 길이 prefix를 붙인 byte[]를 반환합니다.
        /// </summary>
        public static byte[] SerializeHeader<T>(T data) {
            byte[] bytes = MessagePackSerializer.Serialize(data);
            byte[] header = new byte[sizeof(int) + bytes.Length];
            header[0] = (byte)bytes.Length;
            header[1] = (byte)(bytes.Length >> 8);
            header[2] = (byte)(bytes.Length >> 16);
            header[3] = (byte)(bytes.Length >> 24);
            Buffer.BlockCopy(bytes, 0, header, sizeof(int), bytes.Length);
            return header;
        }

        public static T ParseData<T>(ref int offset, byte[] rawdata) {
            int len = ReadInt32(rawdata, offset);
            offset += sizeof(int);
            var memory = new ReadOnlyMemory<byte>(rawdata, offset, len);
            offset += len;
            return MessagePackSerializer.Deserialize<T>(memory);
        }

        public static string GetMethodSignature(MethodInfo method) {
            var sb = new StringBuilder();
            sb.Append(method.ReturnType.Name);
            sb.Append(" (");
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++) {
                if (i > 0) sb.Append(", ");
                sb.Append(parameters[i].ParameterType.Name);
                sb.Append(' ');
                sb.Append(parameters[i].Name);
            }
            sb.Append(')');
            return sb.ToString();
        }
    }

    [MessagePackObject]
    public class TypeWrapper {
        [Key(0)]
        public string TypeName { get; set; }
        [IgnoreMember]
        public Type Type {
            get => TypeName == null ? null : Type.GetType(TypeName);
            set => TypeName = Conduit.Config.UseAssemblyQualifiedNameForTypes ? value.AssemblyQualifiedName : value.FullName;
        }

        public TypeWrapper() { }
        public TypeWrapper(Type type) => Type = type;
    }

    [Serializable]
    internal class NullableInt {
        public int value;
        public bool hasValue;

        public NullableInt(int? v) {
            if (v == null) {
                hasValue = false;
                value = 0;
            }
            else {
                value = v.Value;
                hasValue = true;
            }
        }

        public int? ToNullable() {
            return hasValue ? value : null;
        }
    }

    internal class PairMap<T1, T2> {
        private readonly Dictionary<T1, T2> _map1 = new();
        private readonly Dictionary<T2, T1> _map2 = new();
        private readonly object _lock = new();

        public T2 this[T1 key1] {
            get {
                lock (_lock)
                    return _map1[key1];
            }
            set {
                lock (_lock) {
                    // 기존 쌍 정리
                    if (_map1.TryGetValue(key1, out var oldKey2))
                        _map2.Remove(oldKey2);

                    _map1[key1] = value;
                    _map2[value] = key1;
                }
            }
        }

        public T1 this[T2 key2] {
            get {
                lock (_lock)
                    return _map2[key2];
            }
            set {
                lock (_lock) {
                    if (_map2.TryGetValue(key2, out var oldKey1))
                        _map1.Remove(oldKey1);

                    _map2[key2] = value;
                    _map1[value] = key2;
                }
            }
        }

        public bool TryAdd(T1 key1, T2 key2) {
            if (_map1.ContainsKey(key1) || _map2.ContainsKey(key2))
                return false;

            lock (_lock) {
                _map1[key1] = key2;
                _map2[key2] = key1;
            }
            return true;
        }

        public bool TryGetByFirst(T1 key1, out T2 value2) {
            lock (_lock) return _map1.TryGetValue(key1, out value2);
        }

        public bool TryGetBySecond(T2 key2, out T1 value1) {
            lock (_lock) return _map2.TryGetValue(key2, out value1);
        }

        public void RemoveByFirst(T1 key1) {
            lock (_lock) {
                if (_map1.TryGetValue(key1, out var key2)) {
                    _map1.Remove(key1);
                    _map2.Remove(key2);
                }
            }
        }

        public void RemoveBySecond(T2 key2) {
            lock (_lock) {
                if (_map2.TryGetValue(key2, out var key1)) {
                    _map2.Remove(key2);
                    _map1.Remove(key1);
                }
            }
        }

        public bool ContainsFirst(T1 key1) {
            lock (_lock) return _map1.ContainsKey(key1);
        }
        public bool ContainsSecond(T2 key2) {
            lock (_lock) return _map2.ContainsKey(key2);
        }
    }
}