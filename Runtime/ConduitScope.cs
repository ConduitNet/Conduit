using System;
using System.Collections.Generic;
using System.Reflection;
using ConduitNet.Packets;
using ConduitNet.Utility;

namespace ConduitNet {
    /// <summary>
    /// IDisposable-based handler scope for plain C# classes.
    /// Inherit from this class to use attribute-based handler registration,
    /// and call Dispose to unregister all handlers.
    /// <code>
    /// class MyHandler : ConduitScope {
    ///     [PacketHandler(typeof(MyPacket))]
    ///     void OnPacket(PacketContext&lt;MyPacket&gt; ctx) { }
    /// }
    /// 
    /// // usage:
    /// using var handler = new MyHandler();
    /// </code>
    /// </summary>
    public class ConduitScope : IDisposable {
        private readonly List<Action> _unregisterActions = new();
        private bool _disposed;

        /// <summary>
        /// Creates a new scope. Automatically registers attribute-decorated handlers on this instance.
        /// </summary>
        public ConduitScope() {
            RegisterAttributeHandlers(this);
            SubscribeEvents();
        }

        private void RegisterAttributeHandlers(object target) {
            var methods = target.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods) {
                var peerFilter = method.GetCustomAttribute<RequireRoleAttribute>();
                var senderRole   = peerFilter?.Sender   ?? Role.Any;
                var receiverRole = peerFilter?.Receiver ?? Role.Any;
                bool hasFilter = senderRole != Role.Any || receiverRole != Role.Any;

                // [BytesHandler]
                if (method.GetCustomAttribute<BytesHandlerAttribute>() != null) {
                    if (!TryCreateDelegate(target, method, typeof(BytesHandler), out var handler,
                        "void (BytesContext context)")) continue;
                    var h = (BytesHandler)handler;
                    BindBytesHandler(!hasFilter ? h : ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) h(ctx); });
                    continue;
                }

                // [SignalHandler("name")]
                var signalAttr = method.GetCustomAttribute<SignalHandlerAttribute>();
                if (signalAttr != null) {
                    if (!TryCreateDelegate(target, method, typeof(SignalHandler), out var handler,
                        "void (SignalContext context)")) continue;
                    var h = (SignalHandler)handler;
                    BindSignalHandler(signalAttr.SignalName, !hasFilter ? h : ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) h(ctx); });
                    continue;
                }

                // [PacketHandler(typeof(T))]
                var packetAttr = method.GetCustomAttribute<PacketHandlerAttribute>();
                if (packetAttr != null) {
                    Type packetType = packetAttr.PacketType;
                    Type delegateType = typeof(PacketHandler<>).MakeGenericType(packetType);
                    if (!TryCreateDelegate(target, method, delegateType, out var handler,
                        $"void (PacketContext<{packetType.Name}> context)")) continue;

                    if (hasFilter) {
                        handler = WrapPacketHandlerWithFilter(packetType, handler, senderRole, receiverRole);
                    }

                    Conduit.RegisterPacketHandler(packetType, handler);
                    _unregisterActions.Add(() => Conduit.UnregisterPacketHandler(packetType, handler));
                }
            }
        }

        private static bool TryCreateDelegate(object target, MethodInfo method, Type delegateType, out Delegate result, string expectedSignature) {
            result = Delegate.CreateDelegate(delegateType, target, method, false);
            if (result == null) {
                UnityEngine.Debug.LogError(
                    $"[ConduitObject] Method '{target.GetType().Name}.{method.Name}' has an invalid signature.\n" +
                    $"  Expected: {expectedSignature}\n" +
                    $"  Actual:   {Utils.GetMethodSignature(method)}");
                return false;
            }
            return true;
        }

        #region Peer Filter

        private static bool MatchesRole(IUser user, Role role) => role switch {
            Role.Host   => Conduit.Host != null && user.Id == Conduit.Host.Id,
            Role.Member => Conduit.Host != null && user.Id != Conduit.Host.Id,
            _               => true
        };

        private static bool PassesPeerFilter(IUser sender, Role senderRole, Role receiverRole) =>
            MatchesRole(sender, senderRole) && (receiverRole == Role.Any || MatchesRole(Conduit.LocalUser, receiverRole));

        private static Delegate WrapPacketHandlerWithFilter(Type packetType, Delegate handler, Role senderRole, Role receiverRole) {
            var method = typeof(ConduitScope)
                .GetMethod(nameof(CreateFilteredPacketHandler), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(packetType);
            return (Delegate)method.Invoke(null, new object[] { handler, senderRole, receiverRole });
        }

        private static PacketHandler<T> CreateFilteredPacketHandler<T>(Delegate handler, Role senderRole, Role receiverRole) {
            var typed = (PacketHandler<T>)handler;
            return ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) typed(ctx); };
        }

        #endregion

        #region Events

        private void SubscribeEvents() {
            Conduit.OnUserJoined              += OnUserJoined;
            Conduit.OnUserLeft                += OnUserLeft;
            Conduit.OnHostChanged             += OnHostChanged;
            Conduit.OnPeerConnected           += OnPeerConnected;
            Conduit.OnPeerDisconnected        += OnPeerDisconnected;
            Conduit.OnConnectionFailed        += OnConnectionFailed;
            Conduit.OnLobbyInitialized        += OnLobbyInitialized;
            Conduit.OnLobbyMetadataUpdated    += OnLobbyMetadataUpdated;
            Conduit.OnLobbyStateUpdated       += OnLobbyStateUpdated;
            Conduit.OnUserAccountStateUpdated += OnUserAccountStateUpdated;
            Conduit.OnJoinCancelled           += OnJoinCancelled;
            Conduit.OnTimeSynced              += OnTimeSynced;
        }

        private void UnsubscribeEvents() {
            Conduit.OnUserJoined              -= OnUserJoined;
            Conduit.OnUserLeft                -= OnUserLeft;
            Conduit.OnHostChanged             -= OnHostChanged;
            Conduit.OnPeerConnected           -= OnPeerConnected;
            Conduit.OnPeerDisconnected        -= OnPeerDisconnected;
            Conduit.OnConnectionFailed        -= OnConnectionFailed;
            Conduit.OnLobbyInitialized        -= OnLobbyInitialized;
            Conduit.OnLobbyMetadataUpdated    -= OnLobbyMetadataUpdated;
            Conduit.OnLobbyStateUpdated       -= OnLobbyStateUpdated;
            Conduit.OnUserAccountStateUpdated -= OnUserAccountStateUpdated;
            Conduit.OnJoinCancelled           -= OnJoinCancelled;
            Conduit.OnTimeSynced              -= OnTimeSynced;
        }

        /// <summary>Called when a user joins the lobby.</summary>
        protected virtual void OnUserJoined(IUser user) {}
        /// <summary>Called when a user leaves the lobby.</summary>
        protected virtual void OnUserLeft(IUser user, bool hostChanged) {}
        /// <summary>Called when the lobby host changes.</summary>
        protected virtual void OnHostChanged(IUser previous, IUser next) {}
        /// <summary>Called when a P2P connection to a peer is established.</summary>
        protected virtual void OnPeerConnected(string peerId) {}
        /// <summary>Called when a P2P connection to a peer is lost.</summary>
        protected virtual void OnPeerDisconnected(string peerId) {}
        /// <summary>Called when a connection attempt fails.</summary>
        protected virtual void OnConnectionFailed(ConnectionFailedReason reason) {}
        /// <summary>Called when lobby initialization completes successfully.</summary>
        protected virtual void OnLobbyInitialized() {}
        /// <summary>Called when lobby metadata changes.</summary>
        protected virtual void OnLobbyMetadataUpdated() {}
        /// <summary>Called when the custom lobby state changes.</summary>
        protected virtual void OnLobbyStateUpdated() {}
        /// <summary>Called when the user account state changes.</summary>
        protected virtual void OnUserAccountStateUpdated() {}
        /// <summary>Called when JoinLobby is cancelled.</summary>
        protected virtual void OnJoinCancelled() {}
        /// <summary>Called when clock synchronization with the host completes.</summary>
        protected virtual void OnTimeSynced(long offsetMs) {}

        #endregion

        #region Manual Registration

        /// <summary>Binds a handler for receiving byte array data. Unbound on Dispose.</summary>
        public void BindBytesHandler(BytesHandler handler) {
            Conduit.RegisterBytesHandler(handler);
            _unregisterActions.Add(() => Conduit.UnregisterBytesHandler(handler));
        }

        /// <summary>Unbinds a byte data handler.</summary>
        public void UnbindBytesHandler(BytesHandler handler) {
            Conduit.UnregisterBytesHandler(handler);
        }

        /// <summary>Binds a handler for receiving named signals. Unbound on Dispose.</summary>
        public void BindSignalHandler(string signalName, SignalHandler handler) {
            Conduit.RegisterSignalHandler(signalName, handler);
            _unregisterActions.Add(() => Conduit.UnregisterSignalHandler(signalName, handler));
        }

        /// <summary>Unbinds a signal handler.</summary>
        public void UnbindSignalHandler(string signalName, SignalHandler handler) {
            Conduit.UnregisterSignalHandler(signalName, handler);
        }

        /// <summary>Binds a handler for receiving strongly-typed packets. Unbound on Dispose.</summary>
        public void BindPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket {
            Conduit.RegisterPacketHandler(handler);
            _unregisterActions.Add(() => Conduit.UnregisterPacketHandler(handler));
        }

        /// <summary>Unbinds a typed packet handler.</summary>
        public void UnbindPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket {
            Conduit.UnregisterPacketHandler(handler);
        }

        #endregion

        /// <summary>Unbinds all handlers bound through this scope.</summary>
        public void UnbindAll() {
            foreach (var unregister in _unregisterActions) {
                unregister();
            }
            _unregisterActions.Clear();
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            UnbindAll();
            UnsubscribeEvents();
            OnDispose();
        }
        
        protected virtual void OnDispose() {}
    }
}
