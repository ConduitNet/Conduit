using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ConduitNet.DTO;
using ConduitNet.Http;
using ConduitNet.Packets;
using ConduitNet.Utility;
using NativeWebSocket;
using Newtonsoft.Json;
using Unity.WebRTC;
using UnityEngine;

namespace ConduitNet {
    public class Conduit : MonoBehaviour {

        #region Public Static API

        // ── Properties ──────────────────────────────────────────────

        /// <summary>Global configuration for Conduit. Must be set before calling Init.</summary>
        public static ConduitConfig Config { get; set; }

        /// <summary>Service for handling user-related server requests.</summary>
        public static IUserService UserService {
            get => Instance._userService;
            set => Instance._userService = value;
        }

        /// <summary>Service for handling lobby-related server requests.</summary>
        public static ILobbyService LobbyService {
            get => Instance._lobbyService;
            set => Instance._lobbyService = value;
        }

        /// <summary>The currently joined lobby.</summary>
        public static ILobby Lobby {
            get => Instance._lobby;
            internal set => Instance._lobby = value;
        }

        /// <summary>The host of the current lobby.</summary>
        public static IUser Host => Lobby?.Host;
        /// <summary>Whether the local user is the host.</summary>
        public static bool IsHost => Lobby != null && Host.Id == LocalUser.Id;
        /// <summary>Whether connected to both the signaling server and at least one peer (or is host).</summary>
        public static bool IsConnected => IsConnectedToServer && (IsHost || Instance._peerConnectionMap.Count > 0);
        /// <summary>List of lobby members (excluding host).</summary>
        public static IReadOnlyList<IUser> Members => Lobby?.Members;

        /// <summary>List of all participants including host and members.</summary>
        public static IEnumerable<IUser> Participants {
            get {
                if (Lobby == null) yield break;
                yield return Host;
                foreach (var m in Members)
                    yield return m;
            }
        }

        /// <summary>Array of currently connected peer IDs.</summary>
        public static string[] Peers => Instance._peerConnectionMap.Keys.ToArray();

        /// <summary>Local user information.</summary>
        public static IUser LocalUser {
            get => Instance._user;
            internal set => Instance._user = value;
        }

        /// <summary>Whether connected to the signaling server.</summary>
        public static bool IsConnectedToServer {
            get => Instance._isConnectedToServer;
            internal set => Instance._isConnectedToServer = value;
        }

        /// <summary>The evaluated HTTP/HTTPS API URL currently in use by the engine.</summary>
        public static string ApiUrl => Instance?._apiUrl;

        /// <summary>The evaluated WS/WSS WebSocket URL currently in use by the engine.</summary>
        public static string SocketUrl => Instance?._socketUrl;

        // ── Events ──────────────────────────────────────────────────

        /// <summary>Invoked when a user joins the lobby. Parameter: The newly joined user.</summary>
        public static event Action<IUser> OnUserJoined;
        /// <summary>Invoked when a user leaves the lobby. Parameters: (The leaving user, Whether the host changed).</summary>
        public static event Action<IUser, bool> OnUserLeft;
        /// <summary>Invoked when the lobby host changes. Parameters: (Previous host, New host).</summary>
        public static event Action<IUser, IUser> OnHostChanged;
        /// <summary>Invoked when a P2P connection to a peer is established. Parameter: Peer ID.</summary>
        public static event Action<string> OnPeerConnected;
        /// <summary>Invoked when a P2P connection to a peer is lost. Parameter: Peer ID.</summary>
        public static event Action<string> OnPeerDisconnected;
        /// <summary>Invoked when a connection attempt fails. Parameter: Failure reason.</summary>
        public static event Action<ConnectionFailedReason> OnConnectionFailed;
        /// <summary>Invoked when lobby initialization completes successfully.</summary>
        public static event Action OnLobbyInitialized;
        /// <summary>Invoked when lobby metadata (e.g., name, max players) changes.</summary>
        public static event Action OnLobbyMetadataUpdated;
        /// <summary>Invoked when the custom lobby state (TLobbyState) changes.</summary>
        public static event Action OnLobbyStateUpdated;
        /// <summary>Invoked when the user account state (TAccountState) changes.</summary>
        public static event Action OnUserAccountStateUpdated;
        /// <summary>Invoked when JoinLobby is cancelled.</summary>
        public static event Action OnJoinCancelled;

        // ── Initialization ──────────────────────────────────────────

        /// <summary>
        /// Initializes Conduit. Ensure Config is set prior to calling this.
        /// </summary>
        /// <param name="userId">Local user ID (max 256 chars).</param>
        /// <param name="userProfile">Local user profile data.</param>
        /// <param name="onUserSynced">Callback invoked after syncing local user state with the server.</param>
        public static void Init<TLobbyState, TUserProfile, TAccountState>(string userId, TUserProfile userProfile, Action onUserSynced = null)
            where TLobbyState : class where TUserProfile : class where TAccountState : class {
            Instance._Init<TLobbyState, TUserProfile, TAccountState>(userId, userProfile, onUserSynced);
        }

        // ── Connection ──────────────────────────────────────────────

        /// <summary>Whether a lobby join operation is currently in progress.</summary>
        public static bool IsJoining => Instance._isJoining;

        /// <summary>Joins a lobby. Establishes connection to the signaling server and connects to peers.</summary>
        /// <param name="lobby">The lobby to join.</param>
        /// <param name="headers">Optional HTTP headers to send with the WebSocket connection request.</param>
        public static IEnumerator JoinLobby(ILobby lobby, Dictionary<string, string> headers = null) => Instance._JoinLobby(lobby, headers);

        /// <summary>Cancels an ongoing lobby join operation.</summary>
        public static void CancelJoin() {
            if (!Instance._isJoining) {
                Debug.LogWarning("Not joining lobby");
                return;
            }
            Instance._isJoining = false;
            Instance._CancelJoinCleanUp();
            OnJoinCancelled?.Invoke();
        }

        /// <summary>Leaves the current lobby and disconnects from all peers.</summary>
        public static void LeaveLobby() {
            Instance._LeaveLobby();
        }

        /// <summary>Measures the Round-Trip Time (RTT) to a specific peer in milliseconds using Coroutine.</summary>
        public static IEnumerator GetRTT(string peerId, Action<double?> onResult) => Instance._GetRTT(peerId, onResult);

        /// <summary>Measures the Round-Trip Time (RTT) to a specific peer async/await.</summary>
        public static async Task<double?> GetRTTAsync(string peerId) {
            var task = new TaskCompletionSource<double?>();
            Instance.StartCoroutine(GetRTT(peerId, result => task.SetResult(result)));
            return await task.Task;
        }

        // ── Send ────────────────────────────────────────────────────

        /// <summary>Sends a byte array to a specified user.</summary>
        public static bool SendBytes(IUser user, byte[] data, SendOption option) => Instance._SendBytes(user, data, option, LocalUser);
        /// <summary>Sends a named signal to a specified user.</summary>
        public static bool SendSignal(IUser user, string signalName, SendOption option) => Instance._SendSignal(user, signalName, option, LocalUser);
        /// <summary>Sends a typed packet to a specified user.</summary>
        public static bool SendPacket<T>(IUser user, T packet, SendOption option) where T : IPacket
            => Instance._SendPacket(user, packet, option, LocalUser);

        // ── Broadcast (Host Only) ───────────────────────────────────

        /// <summary>Broadcasts a byte array to all connected peers. (Host only)</summary>
        public static bool BroadcastBytes(byte[] data, SendOption option) => Instance._BroadcastBytes(LocalUser, data, option);
        /// <summary>Broadcasts a named signal to all connected peers. (Host only)</summary>
        public static bool BroadcastSignal(string signalName, SendOption option) => Instance._BroadcastSignal(LocalUser, signalName, option);
        /// <summary>Broadcasts a typed packet to all connected peers. (Host only)</summary>
        public static bool BroadcastPacket<T>(T packet, SendOption option) where T : IPacket
            => Instance._BroadcastPacket(LocalUser, packet, option);

        /// <summary>Sends an <see cref="IAutoRelayPacket"/> to the host, which will relay it to all peers.</summary>
        public static bool SendAutoRelayPacket<T>(T packet, SendOption option) where T : IAutoRelayPacket
            => Instance._SendAutoRelayPacket(LocalUser, packet, option);

        // ── Spoofed (Host Only) ─────────────────────────────────────

        /// <summary>Sends a byte array to a specified user masquerading as the specified sender. (Host only)</summary>
        public static bool SendSpoofedBytes(IUser target, IUser fakeSender, byte[] data, SendOption option) => Instance._SendBytes(target, data, option, fakeSender);
        /// <summary>Sends a named signal to a specified user masquerading as the specified sender. (Host only)</summary>
        public static bool SendSpoofedSignal(IUser target, IUser fakeSender, string signalName, SendOption option) => Instance._SendSignal(target, signalName, option, fakeSender);
        /// <summary>Sends a typed packet to a specified user masquerading as the specified sender. (Host only)</summary>
        public static bool SendSpoofedPacket<T>(IUser target, IUser fakeSender, T packet, SendOption option) where T : IPacket
            => Instance._SendPacket(target, packet, option, fakeSender);
        /// <summary>Sends an <see cref="IAutoRelayPacket"/> to the host, which will relay it to all peers. (Host only)</summary>
        public static bool SendSpoofedAutoRelayPacket<T>(IUser fakeSender, T packet, SendOption option) where T : IAutoRelayPacket
            => Instance._SendAutoRelayPacket(fakeSender, packet, option);

        /// <summary>Broadcasts a byte array to all peers masquerading as the specified sender. (Host only)</summary>
        public static bool BroadcastSpoofedBytes(IUser fakeSender, byte[] data, SendOption option) => Instance._BroadcastBytes(fakeSender, data, option);
        /// <summary>Broadcasts a named signal to all peers masquerading as the specified sender. (Host only)</summary>
        public static bool BroadcastSpoofedSignal(IUser fakeSender, string signalName, SendOption option) => Instance._BroadcastSignal(fakeSender, signalName, option);
        /// <summary>Broadcasts a typed packet to all peers masquerading as the specified sender. (Host only)</summary>
        public static bool BroadcastSpoofedPacket<T>(IUser fakeSender, T packet, SendOption option) where T : IPacket
            => Instance._BroadcastPacket(fakeSender, packet, option);

        // ── Remote Exception ────────────────────────────────────────

        /// <summary>
        /// Throws an exception on a remote peer. The receiver will unconditionally throw the exception of the specified type.
        /// </summary>
        /// <typeparam name="TException">The type of Exception to throw remotely.</typeparam>
        /// <param name="user">The target user to send the exception to.</param>
        /// <param name="message">The exception message.</param>
        /// <param name="option">Send option (default: OrderedReliable).</param>
        public static bool ThrowOnRemote<TException>(IUser user, string message, SendOption option = SendOption.OrderedReliable)
            where TException : Exception => Instance._SendError(user, typeof(TException), message, option);

        // ── Handler Registration ────────────────────────────────────

        /// <summary>Registers a handler for receiving byte array data.</summary>
        public static void RegisterBytesHandler(BytesHandler handler) => Instance._handler.bytesHandler += handler;

        /// <summary>Unregisters a byte data handler.</summary>
        public static bool UnregisterBytesHandler(BytesHandler handler) {
            Instance._handler.bytesHandler -= handler;
            return true;
        }

        /// <summary>Registers a handler for receiving named signals.</summary>
        public static void RegisterSignalHandler(string signalName, SignalHandler handler) {
            // TryGetValue를 사용하여 KeyNotFoundException 방지 및 해시 조회 1회로 단축
            if (Instance._handler.signalHandlers.TryGetValue(signalName, out var existingHandler)) 
            {
                Instance._handler.signalHandlers[signalName] = existingHandler + handler;
            } 
            else 
            {
                Instance._handler.signalHandlers[signalName] = handler;
            }
        }

        /// <summary>Unregisters a signal handler.</summary>
        public static bool UnregisterSignalHandler(string signalName, SignalHandler handler) {
            if (Instance._handler.signalHandlers.TryGetValue(signalName, out var existingHandler)) 
            {
                // 델리게이트에서 핸들러 제거
                var newHandler = (SignalHandler)Delegate.Remove(existingHandler, handler);
                
                if (newHandler == null) 
                {
                    // 더 이상 연결된 핸들러가 없으면 딕셔너리에서 키를 완전히 제거 (메모리 누수 방지)
                    Instance._handler.signalHandlers.Remove(signalName);
                } 
                else 
                {
                    Instance._handler.signalHandlers[signalName] = newHandler;
                }
                return true;
            }
            
            // 애초에 등록된 적 없는 시그널이라면 false 반환
            return false;
        }

        /// <summary>Registers a handler for receiving strongly-typed packets.</summary>
        public static void RegisterPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket 
        {
            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) 
            {
                throw new ArgumentException($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.", nameof(handler));
            }

            // 로컬 함수 선언 (등록 시점에만 클로저 객체가 생성되므로 성능에 문제 없음)
            void wrapper(IUser sender, long timestamp, object packet, SendOption option) 
            {
                // 주의: PacketContext<T>는 가급적 struct여야 핫 패스에서 GC 할당을 방지할 수 있습니다.
                handler(new PacketContext<T>(sender, timestamp, (T)packet, option));
            }

            // 로컬 함수를 명시적으로 Delegate 인스턴스로 한 번만 변환하여 재사용
            Action<IUser, long, object, SendOption> typedWrapper = wrapper;

            Instance._handler.packetHandlerCache[(packetId, handler)] = typedWrapper;

            // TryGetValue를 활용한 단일 해시 조회
            if (Instance._handler.packetHandlerWrappers.TryGetValue(packetId, out var existingWrapper)) 
            {
                Instance._handler.packetHandlerWrappers[packetId] = existingWrapper + typedWrapper;    
            } 
            else 
            {
                Instance._handler.packetHandlerWrappers[packetId] = typedWrapper;
            }
        }

        /// <summary>Unregisters a typed packet handler.</summary>
        public static bool UnregisterPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket {
            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            if (Instance._handler.packetHandlerCache.TryGetValue((packetId, handler), out var wrapper)) {
                Instance._handler.packetHandlerWrappers[packetId] -= wrapper;
                Instance._handler.packetHandlerCache.Remove((packetId, handler));
                return true;
            }
            return false;
        }

        private static readonly MethodInfo _createPacketWrapperMethod = typeof(Conduit)
            .GetMethod(nameof(CreatePacketWrapper), BindingFlags.NonPublic | BindingFlags.Static);

        public static void RegisterPacketHandler(Type packetType, Delegate handler) 
        {
            string packetId = PacketRegistry.GetPacketId(packetType);
            if (packetId == null) 
            {
                throw new ArgumentException($"Type {packetType.FullName} is not a packet. Make sure it is decorated with [Packet] attribute.", nameof(packetType));
            }

            // 1. 매 호출마다 GetMethod를 찾는 비용을 제거하고 캐싱된 MethodInfo를 사용합니다.
            var factory = _createPacketWrapperMethod.MakeGenericMethod(packetType);
            var wrapper = (Action<IUser, long, object, SendOption>)factory.Invoke(null, new object[] { handler });

            Instance._handler.packetHandlerCache[(packetId, handler)] = wrapper;

            // 2. TryGetValue를 사용하여 딕셔너리 키 조회(해시 계산)를 한 번만 수행합니다.
            if (Instance._handler.packetHandlerWrappers.TryGetValue(packetId, out var existingWrapper)) 
            {
                Instance._handler.packetHandlerWrappers[packetId] = existingWrapper + wrapper;    
            } 
            else 
            {
                Instance._handler.packetHandlerWrappers[packetId] = wrapper;
            }
        }

        private static Action<IUser, long, object, SendOption> CreatePacketWrapper<T>(Delegate handler) {
            var typed = (PacketHandler<T>)handler;
            return (sender, timestamp, packet, option) =>
                typed(new PacketContext<T>(sender, timestamp, (T)packet, option));
        }

        public static bool UnregisterPacketHandler(Type packetType, Delegate handler) {
            string packetId = PacketRegistry.GetPacketId(packetType);
            if (packetId == null) throw new Exception($"Type {packetType.FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            if (Instance._handler.packetHandlerCache.TryGetValue((packetId, handler), out var wrapper)) {
                Instance._handler.packetHandlerWrappers[packetId] -= wrapper;
                Instance._handler.packetHandlerCache.Remove((packetId, handler));
                return true;
            }
            return false;
        }

        internal static void SendSignalingMessage(SignalingMsgType type, string to, object data) {
            Instance._SendSignalingMessage(type, to, data);
        }

        #endregion

        #region Singleton

        private static Conduit _instance;
        internal static Conduit Instance {
            get {
                if (_instance != null) return _instance;

                _instance = FindAnyObjectByType<Conduit>();
                if (_instance != null) return _instance;

                var obj = new GameObject("Conduit (Singleton)");
                _instance = obj.AddComponent<Conduit>();
                DontDestroyOnLoad(obj);
                return _instance;
            }
        }

        #endregion

        #region Private Fields

        private IUserService _userService;
        private ILobbyService _lobbyService;
        private ILobby _lobby;
        private IUser _user;
        private bool _isConnectedToServer = false;

        private string _socketUrl, _apiUrl;
        private List<RTCIceServer> _iceServers = new();
        private bool _debugLog = false;

        private WebSocket _signaling;
        internal Dictionary<string, RTCPeerConnection> _peerConnectionMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _myIceCandidatesMap = new();
        private Dictionary<string, List<RTCIceCandidate>> _remoteIceCandidatesMap = new();
        internal ConcurrentQueue<SignalingMessage> _incomingMessageQueue = new();
        internal ConcurrentQueue<DataChannelMessage> _dataChannelQueue = new();
        private readonly System.Diagnostics.Stopwatch _dataChannelStopwatch = new();
        private Dictionary<string, bool> _isDescriptionReadyMap = new();
        private Dictionary<string, List<RTCDataChannel>> _dataChannelListMap = new();
        private HandlerGroup _handler = new();

        private class HandlerGroup {
            public BytesHandler bytesHandler;
            public Dictionary<string, SignalHandler> signalHandlers = new();
            public Dictionary<string, Action<IUser, long, object, SendOption>> packetHandlerWrappers = new();
            public Dictionary<(string, Delegate), Action<IUser, long, object, SendOption>> packetHandlerCache = new();
        }

        internal struct DataChannelMessage {
            public byte[] rawdata;
            public SendOption option;
            public string sender;
            public int offset;
        }

        #endregion

        #region MonoBehaviour Lifecycle

        void Awake() {
            if (_instance != null && _instance != this) {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Update() {
#if !UNITY_WEBGL || UNITY_EDITOR
            _signaling?.DispatchMessageQueue();
#endif

            while (_incomingMessageQueue.TryDequeue(out var message)) {
                StartCoroutine(HandleSignalingMessage(message));
            }

            // Process data channel messages within the time budget
            _dataChannelStopwatch.Restart();
            while (_dataChannelQueue.TryDequeue(out var msg)) {
                ProcessDataChannelMessage(msg.rawdata, msg.option, msg.sender, msg.offset);

                if (_dataChannelStopwatch.Elapsed.TotalMilliseconds >= Config.MaxDataChannelProcessingTimeMs)
                    break;
            }
            _dataChannelStopwatch.Stop();
        }

        void OnApplicationQuit() {
            CleanUp();
        }

        void OnDestroy() {
            CleanUp();
        }

        private void CleanUp() {
            if (_signaling != null) LeaveLobby();
        }

        #endregion

        #region Private Implementation

        private void _Init<TLobbyState, TUserProfile, TAccountState>(string userId, TUserProfile userProfile, Action onUserSynced)
            where TLobbyState : class where TUserProfile : class where TAccountState : class {

            if (Config == null) throw new InvalidOperationException("Conduit.Config must be set before calling Init. Example: Conduit.Config = new ConduitConfig(serverUrl, stunServers);");
            if (userId.Length > 256) throw new Exception("User ID must be less than 256 characters");

            LocalUser = new User<TUserProfile, TAccountState> {
                Id = userId,
                Profile = userProfile
            };

            string serverUrl = Config.ServerUrl;
            if (string.IsNullOrEmpty(serverUrl))
                throw new InvalidDataException("Invalid server URL");

            int index = serverUrl.IndexOf("://", StringComparison.Ordinal);
            serverUrl = index >= 0 ? serverUrl[(index + 3)..] : serverUrl;

            if (serverUrl.EndsWith("/") && serverUrl.Length > 1)
                serverUrl = serverUrl[..^1];

            _socketUrl = (Config.UseWss ? "wss://" : "ws://") + serverUrl;
            _apiUrl = (Config.UseHttps ? "https://" : "http://") + serverUrl;

            IsConnectedToServer = false;
            _debugLog = Config.DebugLog;

            // Apply ICE servers from config
            _iceServers.Clear();
            if (Config.StunServers != null) {
                foreach (var stun in Config.StunServers)
                    _iceServers.Add(new RTCIceServer { urls = stun.urls });
            }
            if (Config.TurnServers != null) {
                foreach (var turn in Config.TurnServers)
                    _iceServers.Add(new RTCIceServer {
                        urls = turn.urls,
                        username = turn.username,
                        credential = turn.credential,
                        credentialType = RTCIceCredentialType.Password
                    });
            }

            UserService = new UserService<TUserProfile, TAccountState>();
            LobbyService = new LobbyService<TLobbyState, TUserProfile, TAccountState>();

            StartCoroutine(SyncUser<TUserProfile, TAccountState>(onUserSynced));
        }

        private IEnumerator SyncUser<TUserProfile, TAccountState>(Action onUserSynced = null) where TUserProfile : class where TAccountState : class {
            if (!LocalUser.TryCast<TUserProfile, TAccountState>(out var user)) yield break;

            yield return HttpRequest.Patch(string.Format(Config.SyncUserPath, _apiUrl, LocalUser.Id))
                .SetBody(user.Profile)
                .Send<TAccountState>(
                    onSuccess: account => {
                        (LocalUser as User<TUserProfile, TAccountState>).Account = account;
                        onUserSynced?.Invoke();
                    },
                    onError: (c, err) => Debug.LogError($"User Sync Failed: {err} ({c})")
                );
        }

        private IEnumerator _GetRTT(string peerId, Action<double?> onResult) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                var statsOp = connection.GetStats();
                yield return statsOp;
                foreach (var report in statsOp.Value.Stats.Values) {
                    if (report.Type == RTCStatsType.CandidatePair) {
                        RTCIceCandidatePairStats pairStats = (RTCIceCandidatePairStats)report;
                        onResult(pairStats.currentRoundTripTime * 1000.0);
                        yield break;
                    }
                }
            }

            onResult(null);
        }

        private bool _SendBytes(IUser user, byte[] data, SendOption option, IUser sender) {
            if (!sender.Equals(LocalUser) && !IsHost) throw new InvalidOperationException("Only host can spoof sender.");

            byte prefix = (byte)((byte)DataType.Byte << 6);

            List<byte> _data = new() { prefix };

            _data.AddRange(data);

            return SendData(user.Id, _data, option, sender?.Id);
        }

        private bool _SendSignal(IUser user, string signalName, SendOption option, IUser sender) {
            if (!sender.Equals(LocalUser) && !IsHost) throw new InvalidOperationException("Only host can spoof sender.");

            byte prefix = (byte)((byte)DataType.Signal << 6);

            byte[] rawdata = Encoding.UTF8.GetBytes(signalName);
            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            List<byte> _data = new() { prefix };
            _data.AddRange(timestamp);
            _data.AddRange(rawdata);

            return SendData(user.Id, _data, option, sender?.Id);
        }

        private bool _SendPacket<T>(IUser user, T packet, SendOption option, IUser sender) {
            if (!sender.Equals(LocalUser) && !IsHost) throw new InvalidOperationException("Only host can spoof sender.");

            byte prefix = (byte)((byte)DataType.Packet << 6);

            byte[] rawdata = Config.PacketSerializer.Serialize(packet, typeof(T));
            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            List<byte> _data = new() { prefix };
            _data.AddRange(timestamp);
            Utils.AppendData(ref _data, packetId);
            _data.AddRange(rawdata);

            return SendData(user.Id, _data, option, sender?.Id);
        }

        private bool _SendAutoRelayPacket<T>(IUser sender, T packet, SendOption option) {
            if (!sender.Equals(LocalUser) && !IsHost) throw new InvalidOperationException("Only host can spoof sender.");
            if (Host == null) return false;

            // If we are the host, broadcast directly — no need to route through self
            // Not spoofed
            if (IsHost && sender.Id == LocalUser.Id) {
                return _BroadcastPacket(sender, packet, option);
            }

            byte prefix = (byte)((byte)DataType.Packet << 6);
            byte[] rawdata = Config.PacketSerializer.Serialize(packet, typeof(T));
            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            List<byte> _data = new() { prefix };
            _data.AddRange(timestamp);
            Utils.AppendData(ref _data, packetId);
            _data.AddRange(rawdata);

            // host and spoofed
            if (IsHost && sender.Id != LocalUser.Id) {
                _dataChannelQueue.Enqueue(new DataChannelMessage {
                    rawdata = _data.ToArray(),
                    option = option,
                    sender = sender.Id,
                    offset = 0
                });

                return _BroadcastPacket(sender, packet, option);
            }

            return SendData(Host.Id, _data, option);
        }

        private bool _BroadcastBytes(IUser sender, byte[] data, SendOption option) {
            if (!IsHost) throw new InvalidOperationException("Only host can broadcast.");

            byte prefix = (byte)((byte)DataType.Byte << 6);
            List<byte> _data = new() { prefix };
            _data.AddRange(data);

            bool result = true;

            string senderId = sender?.Id;
            foreach (var peerId in Peers) {
                if (peerId == senderId) continue;
                result &= SendData(peerId, _data, option, senderId);
            }

            // Self-reception if spoofed
            if (senderId != LocalUser.Id) {
                _dataChannelQueue.Enqueue(new DataChannelMessage {
                    rawdata = _data.ToArray(),
                    option = option,
                    sender = senderId,
                    offset = 0
                });
            }

            return result;
        }

        private bool _BroadcastSignal(IUser sender, string signalName, SendOption option) {
            if (!IsHost) throw new InvalidOperationException("Only host can broadcast.");

            byte prefix = (byte)((byte)DataType.Signal << 6);
            byte[] rawdata = Encoding.UTF8.GetBytes(signalName);
            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            List<byte> _data = new() { prefix };
            _data.AddRange(timestamp);
            _data.AddRange(rawdata);

            bool result = true;

            string senderId = sender?.Id;
            foreach (var peerId in Peers) {
                if (peerId == senderId) continue;
                result &= SendData(peerId, _data, option, senderId);
            }

            // Self-reception if spoofed
            if (senderId != LocalUser.Id) {
                _dataChannelQueue.Enqueue(new DataChannelMessage {
                    rawdata = _data.ToArray(),
                    option = option,
                    sender = senderId,
                    offset = 0
                });
            }

            return result;
        }

        private bool _BroadcastPacket<T>(IUser sender, T packet, SendOption option) {
            if (!IsHost) throw new InvalidOperationException("Only host can broadcast.");

            byte prefix = (byte)((byte)DataType.Packet << 6);
            byte[] rawdata = Config.PacketSerializer.Serialize(packet, typeof(T));
            byte[] timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            string packetId = PacketRegistry.GetPacketId(typeof(T));
            if (packetId == null) throw new Exception($"Type {typeof(T).FullName} is not a packet. Make sure it is decorated with [Packet] attribute.");

            List<byte> _data = new() { prefix };
            _data.AddRange(timestamp);
            Utils.AppendData(ref _data, packetId);
            _data.AddRange(rawdata);

            bool result = true;

            string senderId = sender?.Id;
            foreach (var peerId in Peers) {
                if (peerId == senderId) continue;
                result &= SendData(peerId, _data, option, senderId);
            }

            // Self-reception if spoofed
            if (senderId != LocalUser.Id) {
                _dataChannelQueue.Enqueue(new DataChannelMessage {
                    rawdata = _data.ToArray(),
                    option = option,
                    sender = senderId,
                    offset = 0
                });
            }

            return result;
        }

        private bool _SendError(IUser user, Type exceptionType, string message, SendOption option) {
            byte prefix = (byte)((byte)DataType.Error << 6);

            List<byte> _data = new() { prefix };
            Utils.AppendData(ref _data, new TypeWrapper(exceptionType));
            _data.AddRange(Encoding.UTF8.GetBytes(message ?? ""));

            return SendData(user.Id, _data, option);
        }

        private void _SendSignalingMessage(SignalingMsgType type, string to, object data) {
            if (!IsConnectedToServer) return;
            try {
                string json = JsonConvert.SerializeObject(data);
                var message = new SignalingMessage {
                    type = type,
                    from = LocalUser.Id,
                    to = to,
                    body = json
                };

                _signaling.SendText(JsonConvert.SerializeObject(message));
            }
            catch (Exception ex) {
                throw new Exception("Failed to send signaling message", ex);
            }
        }

        #endregion

        #region Signaling

        private void SetupSignaling() {
            _signaling.OnOpen += () => {
                IsConnectedToServer = true;
                if (_debugLog) Debug.Log("Connected to signaling server");
            };

            _signaling.OnError += (e) => {
                if (_debugLog) Debug.LogError("Signaling error: " + e);
            };

            _signaling.OnClose += (e) => {
                IsConnectedToServer = false;
                if (_debugLog) Debug.Log("Disconnected from signaling server");
            };

            _signaling.OnMessage += (bytes) => {
                var message = Encoding.UTF8.GetString(bytes);
                var signalingMessage = JsonConvert.DeserializeObject<SignalingMessage>(message);
                _incomingMessageQueue.Enqueue(signalingMessage);
            };
        }

        private bool _isJoining = false;

        private IEnumerator _JoinLobby(ILobby lobby, Dictionary<string, string> headers) {
            if (headers == null) headers = new();

            _isJoining = true;

            headers.Add(Config.UserIdHeader, LocalUser.Id);
            headers.Add(Config.LobbyIdHeader, lobby.Id);
            _signaling = new WebSocket(_socketUrl, headers);

            _dataChannelListMap = new();
            _isDescriptionReadyMap = new();
            _peerConnectionMap = new();
            _myIceCandidatesMap = new();
            _remoteIceCandidatesMap = new();
            _incomingMessageQueue = new();
            _dataChannelQueue = new();

            SetupSignaling();

            _signaling.Connect();

            float elapsed = 0f;

            while (!IsConnectedToServer && elapsed < Config.JoinTimeout) {
                if (!_isJoining) yield break;
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (!_isJoining) yield break;

            if (!IsConnectedToServer) {
                _isJoining = false;
                Debug.LogError("Failed to connect to signaling server");
            }
        }

        private void _CancelJoinCleanUp() {
            DisconnectAll();
            Lobby = null;
            if (_signaling != null) {
                _signaling.Close();
                _signaling = null;
            }
            IsConnectedToServer = false;
        }

        private void _LeaveLobby() {
            DisconnectAll();
            Lobby = null;
            if (_signaling != null) {
                _signaling.Close();
                _signaling = null;
            }
        }

        private IEnumerator HandleSignalingMessage(SignalingMessage message) {
            switch (message.type) {
                case SignalingMsgType.Offer:
                    yield return HandleOffer(message);
                    break;
                case SignalingMsgType.Answer:
                    yield return HandleAnswer(message);
                    break;
                case SignalingMsgType.IceCandidate:
                    HandleIceCandidate(message);
                    break;
                case SignalingMsgType.LobbyUpdate:
                    HandleLobbyUpdate(message);
                    break;
                case SignalingMsgType.ConnectionFailed:
                    var dto = JsonConvert.DeserializeObject<ConnectionFailedDTO>(message.body);
                    OnConnectionFailed?.Invoke(dto.reason);

                    LeaveLobby();

                    break;
                case SignalingMsgType.ResponseData:
                    var res = JsonConvert.DeserializeObject<DataResponseDTO>(message.body);
                    if (!res.success) break;

                    if (res.type == DataRequestType.User) {
                        (UserService as IUserServiceInternal).HandleFetchResponse(res);
                    }
                    else if (res.type == DataRequestType.Lobby) {
                        (LobbyService as ILobbyServiceInternal).HandleFetchResponse(res);
                    }
                    break;
                case SignalingMsgType.DataUpdate:
                    HandleDataUpdate(message);
                    break;
                default:
                    if (_debugLog) Debug.LogWarning($"Unknown signaling message type: {message.type}");
                    break;
            }
        }

        private void HandleDataUpdate(SignalingMessage message) {
            var update = JsonConvert.DeserializeObject<DataUpdateDTO>(message.body);
            switch (update.type) {
                case DataChangeType.UserAccount: {
                        var user = (UserService as IUserServiceInternal).Deserialize(update.data);
                        LocalUser = user;
                        OnUserAccountStateUpdated?.Invoke();
                        break;
                    }
                case DataChangeType.LobbyMetadata: {
                        var lobby = (LobbyService as ILobbyServiceInternal).Deserialize(update.data);
                        Lobby = lobby;
                        OnLobbyMetadataUpdated?.Invoke();
                        break;
                    }
                case DataChangeType.LobbyState: {
                        var lobby = (LobbyService as ILobbyServiceInternal).Deserialize(update.data);
                        Lobby = lobby;
                        OnLobbyStateUpdated?.Invoke();
                        break;
                    }
                default:
                    break;
            }
        }

        private void HandleLobbyUpdate(SignalingMessage message) {
            var lobbyUpdateData = JsonConvert.DeserializeObject<LobbyUpdateDTO>(message.body);
            ILobby lobby = (LobbyService as ILobbyServiceInternal).Deserialize(lobbyUpdateData.lobby);

            switch (lobbyUpdateData.type) {
                case LobbyUpdateType.Join: {
                        Lobby = lobby;
                        var target = Lobby.Members.FirstOrDefault(x => x.Id == lobbyUpdateData.target);
                        OnUserJoined?.Invoke(target);
                        break;
                    }
                case LobbyUpdateType.Leave: {
                        bool wasHost = false;
                        if (Host.Id != lobby.HostId) { // when host changed
                            wasHost = true;

                            if (lobby.HostId != LocalUser.Id) {
                                // Reconnect
                                DisconnectPeer(Host.Id);
                                StartCoroutine(ConnectPeerAsync(lobby.HostId));
                            }


                            OnHostChanged?.Invoke(Host, lobby.Host);
                        }

                        var target = Participants.FirstOrDefault(x => x.Id == lobbyUpdateData.target);
                        Lobby = lobby;
                        OnUserLeft?.Invoke(target, wasHost);
                        break;
                    }
                case LobbyUpdateType.Init: {
                        if (!_isJoining) break;
                        Lobby = lobby;

                        if (Host.Id != LocalUser.Id) {
                            StartCoroutine(ConnectPeerAsync(Host.Id));
                        }
                        else {
                            _isJoining = false;
                        }

                        OnLobbyInitialized?.Invoke();

                        break;
                    }
            }
        }

        #endregion

        #region Peer Connection

        private IEnumerator ConnectPeerAsync(string peerId) {
            SetupPeerConnection(peerId, true);

            var connection = _peerConnectionMap[peerId];
            var offerOp = connection.CreateOffer();
            yield return offerOp;
            var offerDesc = offerOp.Desc;
            connection.SetLocalDescription(ref offerDesc);

            SendSignalingMessage(SignalingMsgType.Offer, peerId, new OfferAnswerData {
                sdp = offerDesc.sdp,
                type = offerDesc.type.ToString().ToLower()
            });

            if (_debugLog) Debug.Log($"Connecting to {peerId}...");
        }

        private void DisconnectPeer(string peerId) {
            if (_peerConnectionMap.TryGetValue(peerId, out var connection)) {
                if (connection.ConnectionState != RTCPeerConnectionState.Closed) {
                    connection.Close();
                }
                connection.Dispose();
                _peerConnectionMap.Remove(peerId);
            }

            _myIceCandidatesMap.Remove(peerId);
            _isDescriptionReadyMap.Remove(peerId);
            _dataChannelListMap.Remove(peerId);

        }

        private void DisconnectAll() {
            var keys = _peerConnectionMap.Keys.ToList();
            foreach (var peerId in keys) {
                DisconnectPeer(peerId);
            }
        }

        private void SetupPeerConnection(string peerId, bool isOfferer) {
            var config = new RTCConfiguration {

                iceTransportPolicy = RTCIceTransportPolicy.All,
                iceServers = _iceServers.ToArray()
            };

            var connection = new RTCPeerConnection(ref config);



            if (isOfferer) {
                List<RTCDataChannel> channels = new() {
                    connection.CreateDataChannel("OrderedReliable", new RTCDataChannelInit { ordered = true }),
                    connection.CreateDataChannel("OrderedUnreliable", new RTCDataChannelInit { ordered = true, maxRetransmits = 0 }),
                    connection.CreateDataChannel("UnorderedReliable", new RTCDataChannelInit { ordered = false }),
                    connection.CreateDataChannel("UnorderedUnreliable", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 }),
                };

                for (int i = 0; i < channels.Count; i++) {
                    var sendOption = (SendOption)i;
                    channels[i].OnMessage += (rawdata) => {
                        HandleDataChannel(rawdata, sendOption);
                    };
                }

                _dataChannelListMap[peerId] = channels;
            }

            if (_debugLog) Debug.Log($"Created peer connection for {peerId}");

            _myIceCandidatesMap[peerId] = new();

            connection.OnIceCandidate = candidate => {
                if (candidate != null) {
                    lock (_myIceCandidatesMap) {
                        _myIceCandidatesMap[peerId]?.Add(candidate);
                    }
                    if (_debugLog) Debug.Log($"New Local ICE candidate for {peerId}: {candidate.Candidate}");

                    SendSignalingMessage(SignalingMsgType.IceCandidate, peerId, new IceCandidateData {
                        candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid,
                        sdpMLineIndex = new NullableInt(candidate.SdpMLineIndex)
                    });
                }
            };

            bool isConnected = false;
            connection.OnIceConnectionChange = state => {
                if (_debugLog) Debug.Log($"ICE connection state for {peerId}: {state}");
                switch (state) {
                    case RTCIceConnectionState.Connected:
                    case RTCIceConnectionState.Completed:
                        if (!isConnected) {
                            _isJoining = false;
                            OnPeerConnected?.Invoke(peerId);
                            isConnected = true;
                        }
                        break;
                    case RTCIceConnectionState.Disconnected:
                        DisconnectPeer(peerId);
                        isConnected = false;
                        OnPeerDisconnected?.Invoke(peerId);
                        break;
                    case RTCIceConnectionState.Failed:
                        if (!IsHost) {
                            LeaveLobby();
                            OnConnectionFailed?.Invoke(ConnectionFailedReason.IceConnectionFailed);
                        }
                        break;
                }
            };

            connection.OnDataChannel = channel => {
                if (_debugLog) Debug.Log($"New data channel for {peerId}: {channel.Label}");

                if (!_dataChannelListMap.ContainsKey(peerId)) {
                    _dataChannelListMap[peerId] = new() { null, null, null, null };
                }

                var channelList = _dataChannelListMap[peerId];

                SendOption channelSendOption;
                switch (channel.Label) {
                    case "OrderedReliable":
                        channelSendOption = SendOption.OrderedReliable;
                        channelList[0] = channel;
                        break;
                    case "OrderedUnreliable":
                        channelSendOption = SendOption.OrderedUnreliable;
                        channelList[1] = channel;
                        break;
                    case "UnorderedReliable":
                        channelSendOption = SendOption.UnorderedReliable;
                        channelList[2] = channel;
                        break;
                    case "UnorderedUnreliable":
                        channelSendOption = SendOption.UnorderedUnreliable;
                        channelList[3] = channel;
                        break;
                    default:
                        return;
                }

                channel.OnMessage += (rawdata) => HandleDataChannel(rawdata, channelSendOption);
            };

            _peerConnectionMap[peerId] = connection;
        }

        private IEnumerator HandleOffer(SignalingMessage message) {
            SetupPeerConnection(message.from, false);

            var offerData = JsonConvert.DeserializeObject<OfferAnswerData>(message.body);
            var offer = new RTCSessionDescription {
                type = RTCSdpType.Offer,
                sdp = offerData.sdp
            };

            var connection = _peerConnectionMap[message.from];
            yield return connection.SetRemoteDescription(ref offer);

            var answerOp = connection.CreateAnswer();
            yield return answerOp;
            var answerDesc = answerOp.Desc;
            yield return connection.SetLocalDescription(ref answerDesc);

            ProcessQueuedRemoteIceCandidates(message.from);
            _isDescriptionReadyMap[message.from] = true;

            SendSignalingMessage(SignalingMsgType.Answer, message.from, new OfferAnswerData {
                sdp = answerDesc.sdp,
                type = answerDesc.type.ToString().ToLower()
            });
        }

        private void ProcessQueuedRemoteIceCandidates(string peerId) {
            if (_isDescriptionReadyMap.ContainsKey(peerId) && _isDescriptionReadyMap[peerId]) return;

            if (_remoteIceCandidatesMap.TryGetValue(peerId, out var _remoteIceCandidates)) {
                foreach (var c in _remoteIceCandidates) {
                    if (_debugLog) Debug.Log($"Processing Queued Remote ICE Candidate: {c.Candidate}");
                    _peerConnectionMap[peerId]?.AddIceCandidate(c);
                }
                _remoteIceCandidatesMap.Remove(peerId);
            }
        }

        private IEnumerator HandleAnswer(SignalingMessage message) {
            var answerData = JsonConvert.DeserializeObject<OfferAnswerData>(message.body);
            var answer = new RTCSessionDescription {
                type = RTCSdpType.Answer,
                sdp = answerData.sdp
            };

            var connection = _peerConnectionMap[message.from];
            yield return connection.SetRemoteDescription(ref answer);

            ProcessQueuedRemoteIceCandidates(message.from);
            _isDescriptionReadyMap[message.from] = true;
        }

        private void HandleIceCandidate(SignalingMessage message) {
            var candidateData = JsonConvert.DeserializeObject<IceCandidateData>(message.body);
            var candidate = new RTCIceCandidate(new RTCIceCandidateInit {
                candidate = candidateData.candidate,
                sdpMid = candidateData.sdpMid,
                sdpMLineIndex = candidateData.sdpMLineIndex.ToNullable()
            });

            if (_isDescriptionReadyMap.ContainsKey(message.from) && _isDescriptionReadyMap[message.from]) {
                _peerConnectionMap[message.from].AddIceCandidate(candidate);
                if (_debugLog) Debug.Log($"Added Remote ICE Candidate: {candidate.Candidate}");
            }
            else {
                if (_debugLog) Debug.Log($"Queued Remote ICE Candidate: {candidate.Candidate}");

                if (_remoteIceCandidatesMap.ContainsKey(message.from)) {
                    _remoteIceCandidatesMap[message.from].Add(candidate);
                }
                else {
                    _remoteIceCandidatesMap.Add(message.from, new() { candidate });
                }
            }
        }

        #endregion

        #region Data Channel

        private void HandleDataChannel(byte[] rawdata, SendOption option) {
            int offset = 0;
            var endpoint = Utils.ParseData<DataEndPoint>(ref offset, rawdata);

            if (endpoint.receiver != LocalUser.Id) {
                // Transfer data to destination if host (relay immediately)
                if (IsHost && _dataChannelListMap.TryGetValue(endpoint.receiver, out var channels)) {
                    var channel = channels[(byte)option];
                    if (channel == null) {
                        Debug.LogError($"Data channel for {option} is not ready for peer {endpoint.receiver}. Relay dropped.");
                        return;
                    }
                    channel.Send(rawdata);
                }
                return;
            }

            // Enqueue if the receiver is local user (processed in Update)
            _dataChannelQueue.Enqueue(new DataChannelMessage {
                rawdata = rawdata,
                option = option,
                sender = endpoint.sender,
                offset = offset
            });
        }

        private void ProcessDataChannelMessage(byte[] rawdata, SendOption option, string sender, int offset) {
            IUser senderUser = Participants.FirstOrDefault(p => p.Id == sender);
            if (senderUser == null) return;

            int contentStart = offset; // position right after the DataEndPoint header

            switch (rawdata[offset++] >> 6) {
                case (byte)DataType.Byte:
                    _handler.bytesHandler?.Invoke(new BytesContext(senderUser, new ReadOnlyMemory<byte>(rawdata, offset, rawdata.Length - offset), option));
                    break;
                case (byte)DataType.Signal: {
                        long timestamp = Utils.ReadInt64(rawdata, offset);
                        offset += sizeof(long);
                        if (_handler.signalHandlers.TryGetValue(Encoding.UTF8.GetString(rawdata, offset, rawdata.Length - offset), out var signalHandler))
                            signalHandler?.Invoke(new SignalContext(senderUser, timestamp, option));
                        break;
                    }
                case (byte)DataType.Packet: {
                        long timestamp = Utils.ReadInt64(rawdata, offset);
                        offset += sizeof(long);

                        string packetId = Utils.ParseData<string>(ref offset, rawdata);
                        Type packetType = PacketRegistry.GetPacketType(packetId);
                        if (packetType == null) {
                            throw new InvalidOperationException($"Received unknown packet ID: {packetId}");
                        }

                        var packetData = new ReadOnlyMemory<byte>(rawdata, offset, rawdata.Length - offset);

                        var data = Config.PacketSerializer.Deserialize(packetData, packetType);

                        // [AutoRelay]: host re-addresses and forwards the payload to all other peers.
                        // We use SendData (not raw channel.Send) so each peer gets a fresh DataEndPoint
                        // with the correct receiver. The payload starts at contentStart (type byte onwards),
                        // excluding the original DataEndPoint that was addressed to the host.
                        if (IsHost && PacketRegistry.IsAutoRelay(packetType)) {
                            var segment = new ArraySegment<byte>(rawdata, contentStart, rawdata.Length - contentStart);
                            foreach (var peerId in Peers) {
                                if (peerId == senderUser.Id) continue;
                                SendData(peerId, new List<byte>(segment), option, senderUser.Id);
                            }
                        }

                        if (_handler.packetHandlerWrappers.TryGetValue(packetId, out var packetHandlerWrapper)) {
                            packetHandlerWrapper?.Invoke(senderUser, timestamp, data, option);
                        }
                    }
                    break;
                case (byte)DataType.Error: {
                        var typeWrapper = Utils.ParseData<TypeWrapper>(ref offset, rawdata);
                        string message = Encoding.UTF8.GetString(rawdata, offset, rawdata.Length - offset);

                        Type exType = typeWrapper.Type;
                        Exception exception;
                        if (exType != null && typeof(Exception).IsAssignableFrom(exType)) {
                            try {
                                exception = (Exception)Activator.CreateInstance(exType, message);
                            }
                            catch {
                                exception = new RemoteException(senderUser, message, typeWrapper.TypeName);
                            }
                        }
                        else {
                            exception = new RemoteException(senderUser, message, typeWrapper.TypeName);
                        }

                        throw exception;
                    }
            }
        }

        private bool SendData(string userId, List<byte> data, SendOption option, string senderId = null) {
            if (userId == LocalUser.Id) return false;

            string destination = IsHost ? userId : Host.Id;
            if (!_dataChannelListMap.TryGetValue(destination, out var channels)) return false;

            var channel = channels[(byte)option];
            if (channel == null) {
                Debug.LogError($"Channel for {option} is not ready");
                return false;
            }

            var endpoint = new DataEndPoint() {
                sender = senderId ?? LocalUser.Id,
                receiver = userId
            };

            Utils.InsertData(data, endpoint);
            channel.Send(data.ToArray());
            return true;
        }

        #endregion
    }

    /// <summary>
    /// Typed accessor for Conduit. Use with a using alias for convenience:
    /// <code>using Net = ConduitNet.Conduit&lt;MyLobbyState, MyProfile, MyAccount&gt;;</code>
    /// Then access typed properties directly: <c>Net.LocalUser.Profile</c>, <c>Net.Lobby.State</c>
    /// </summary>
    public static class Conduit<TLobbyState, TUserProfile, TAccountState>
        where TLobbyState : class where TUserProfile : class where TAccountState : class {

        public static User<TUserProfile, TAccountState> LocalUser
            => Conduit.LocalUser as User<TUserProfile, TAccountState>;

        public static Lobby<TLobbyState> Lobby
            => Conduit.Lobby as Lobby<TLobbyState>;

        public static User<TUserProfile, TAccountState> Host
            => Conduit.Host as User<TUserProfile, TAccountState>;

        public static IEnumerable<User<TUserProfile, TAccountState>> Members
            => Conduit.Members?.Cast<User<TUserProfile, TAccountState>>();

        public static IEnumerable<User<TUserProfile, TAccountState>> Participants
            => Conduit.Participants?.Cast<User<TUserProfile, TAccountState>>();

        public static UserService<TUserProfile, TAccountState> UserService
            => Conduit.UserService as UserService<TUserProfile, TAccountState>;

        public static LobbyService<TLobbyState, TUserProfile, TAccountState> LobbyService
            => Conduit.LobbyService as LobbyService<TLobbyState, TUserProfile, TAccountState>;
    }
}