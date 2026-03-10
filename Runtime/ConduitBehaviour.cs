using System;
using System.Collections.Generic;
using System.Reflection;
using ConduitNet.Packets;
using ConduitNet.Utility;
using UnityEngine;

namespace ConduitNet {
    /// <summary>
    /// Base class that inherits MonoBehaviour to register Network handlers
    /// and automatically unregister them upon OnDestroy.
    /// Supports both attribute-based automatic registration and manual method registration.
    /// </summary>
    public class ConduitBehaviour : MonoBehaviour {
        private readonly List<Action> _unregisterActions = new();

        #region Attribute-based Registration

        protected virtual void Awake() {
            RegisterAttributeHandlers();
            SubscribeEvents();
        }

        private void RegisterAttributeHandlers() {
            var methods = GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods) {
                var senderFilter = method.GetCustomAttribute<SenderFilterAttribute>()?.Filter ?? SenderFilter.Any;

                // [BytesHandler]
                if (method.GetCustomAttribute<BytesHandlerAttribute>() != null) {
                    if (!TryCreateDelegate(method, typeof(BytesHandler), out var handler,
                        "void (BytesContext context)")) continue;
                    var h = (BytesHandler)handler;
                    BindBytesHandler(senderFilter == SenderFilter.Any ? h : ctx => { if (PassesSenderFilter(ctx.Sender, senderFilter)) h(ctx); });
                    continue;
                }

                // [SignalHandler("name")]
                var signalAttr = method.GetCustomAttribute<SignalHandlerAttribute>();
                if (signalAttr != null) {
                    if (!TryCreateDelegate(method, typeof(SignalHandler), out var handler,
                        "void (SignalContext context)")) continue;
                    var h = (SignalHandler)handler;
                    BindSignalHandler(signalAttr.SignalName, senderFilter == SenderFilter.Any ? h : ctx => { if (PassesSenderFilter(ctx.Sender, senderFilter)) h(ctx); });
                    continue;
                }

                // [PacketHandler(typeof(T))]
                var packetAttr = method.GetCustomAttribute<PacketHandlerAttribute>();
                if (packetAttr != null) {
                    Type packetType = packetAttr.PacketType;
                    Type delegateType = typeof(PacketHandler<>).MakeGenericType(packetType);
                    if (!TryCreateDelegate(method, delegateType, out var handler,
                        $"void (PacketContext<{packetType.Name}> context)")) continue;

                    if (senderFilter != SenderFilter.Any) {
                        handler = WrapPacketHandlerWithFilter(packetType, handler, senderFilter);
                    }

                    Conduit.RegisterPacketHandler(packetType, handler);
                    _unregisterActions.Add(() => Conduit.UnregisterPacketHandler(packetType, handler));
                }
            }
        }

        private bool TryCreateDelegate(MethodInfo method, Type delegateType, out Delegate result, string expectedSignature) {
            result = Delegate.CreateDelegate(delegateType, this, method, false);
            if (result == null) {
                Debug.LogError(
                    $"[NetworkBehaviour] Method '{GetType().Name}.{method.Name}' has an invalid signature.\n" +
                    $"  Expected: {expectedSignature}\n" +
                    $"  Actual:   {Utils.GetMethodSignature(method)}");
                return false;
            }
            return true;
        }

        #endregion

        #region Events

        private void SubscribeEvents() {
            Conduit.OnUserJoined += OnUserJoined;
            Conduit.OnUserLeft += OnUserLeft;
            Conduit.OnHostChanged += OnHostChanged;
            Conduit.OnPeerConnected += OnPeerConnected;
            Conduit.OnPeerDisconnected += OnPeerDisconnected;
            Conduit.OnConnectionFailed += OnConnectionFailed;
            Conduit.OnLobbyInitialized += OnLobbyInitialized;
            Conduit.OnLobbyMetadataUpdated += OnLobbyMetadataUpdated;
            Conduit.OnLobbyStateUpdated += OnLobbyStateUpdated;
            Conduit.OnUserAccountStateUpdated += OnUserAccountStateUpdated;
            Conduit.OnJoinCancelled += OnJoinCancelled;
        }

        private void UnsubscribeEvents() {
            Conduit.OnUserJoined -= OnUserJoined;
            Conduit.OnUserLeft -= OnUserLeft;
            Conduit.OnHostChanged -= OnHostChanged;
            Conduit.OnPeerConnected -= OnPeerConnected;
            Conduit.OnPeerDisconnected -= OnPeerDisconnected;
            Conduit.OnConnectionFailed -= OnConnectionFailed;
            Conduit.OnLobbyInitialized -= OnLobbyInitialized;
            Conduit.OnLobbyMetadataUpdated -= OnLobbyMetadataUpdated;
            Conduit.OnLobbyStateUpdated -= OnLobbyStateUpdated;
            Conduit.OnUserAccountStateUpdated -= OnUserAccountStateUpdated;
            Conduit.OnJoinCancelled -= OnJoinCancelled;
        }

        /// <summary>Called when a user joins the lobby.</summary>
        protected virtual void OnUserJoined(IUser user) { }
        /// <summary>Called when a user leaves the lobby.</summary>
        protected virtual void OnUserLeft(IUser user, bool hostChanged) { }
        /// <summary>Called when the lobby host changes.</summary>
        protected virtual void OnHostChanged(IUser previous, IUser next) { }
        /// <summary>Called when a P2P connection to a peer is established.</summary>
        protected virtual void OnPeerConnected(string peerId) { }
        /// <summary>Called when a P2P connection to a peer is lost.</summary>
        protected virtual void OnPeerDisconnected(string peerId) { }
        /// <summary>Called when a connection attempt fails.</summary>
        protected virtual void OnConnectionFailed(ConnectionFailedReason reason) { }
        /// <summary>Called when lobby initialization completes successfully.</summary>
        protected virtual void OnLobbyInitialized() { }
        /// <summary>Called when lobby metadata changes.</summary>
        protected virtual void OnLobbyMetadataUpdated() { }
        /// <summary>Called when the custom lobby state changes.</summary>
        protected virtual void OnLobbyStateUpdated() { }
        /// <summary>Called when the user account state changes.</summary>
        protected virtual void OnUserAccountStateUpdated() { }
        /// <summary>Called when JoinLobby is cancelled.</summary>
        protected virtual void OnJoinCancelled() { }

        #endregion

        #region Sender Filter

        private static bool PassesSenderFilter(IUser sender, SenderFilter filter) => filter switch {
            SenderFilter.Host => Conduit.Host != null && sender.Id == Conduit.Host.Id,
            SenderFilter.Member => Conduit.Host != null && sender.Id != Conduit.Host.Id,
            _ => true
        };

        private static Delegate WrapPacketHandlerWithFilter(Type packetType, Delegate handler, SenderFilter filter) {
            var method = typeof(ConduitBehaviour)
                .GetMethod(nameof(CreateFilteredPacketHandler), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(packetType);
            return (Delegate)method.Invoke(null, new object[] { handler, filter });
        }

        private static PacketHandler<T> CreateFilteredPacketHandler<T>(Delegate handler, SenderFilter filter) {
            var typed = (PacketHandler<T>)handler;
            return ctx => { if (PassesSenderFilter(ctx.Sender, filter)) typed(ctx); };
        }

        #endregion

        #region Manual Registration

        /// <summary>Binds a handler for receiving byte array data. Automatically unbound OnDestroy.</summary>
        protected void BindBytesHandler(BytesHandler handler) {
            Conduit.RegisterBytesHandler(handler);
            _unregisterActions.Add(() => Conduit.UnregisterBytesHandler(handler));
        }

        /// <summary>Unbinds a byte data handler.</summary>
        protected void UnbindBytesHandler(BytesHandler handler) {
            Conduit.UnregisterBytesHandler(handler);
        }

        /// <summary>Binds a handler for receiving named signals. Automatically unbound OnDestroy.</summary>
        protected void BindSignalHandler(string signalName, SignalHandler handler) {
            Conduit.RegisterSignalHandler(signalName, handler);
            _unregisterActions.Add(() => Conduit.UnregisterSignalHandler(signalName, handler));
        }

        /// <summary>Unbinds a signal handler.</summary>
        protected void UnbindSignalHandler(string signalName, SignalHandler handler) {
            Conduit.UnregisterSignalHandler(signalName, handler);
        }

        /// <summary>Binds a handler for receiving strongly-typed packets. Automatically unbound OnDestroy.</summary>
        protected void BindPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket {
            Conduit.RegisterPacketHandler(handler);
            _unregisterActions.Add(() => Conduit.UnregisterPacketHandler(handler));
        }

        /// <summary>Unbinds a typed packet handler.</summary>
        protected void UnbindPacketHandler<T>(PacketHandler<T> handler) where T : INetworkPacket {
            Conduit.UnregisterPacketHandler(handler);
        }

        #endregion

        /// <summary>Unbinds all handlers bound through this instance.</summary>
        protected void UnbindAll() {
            foreach (var unregister in _unregisterActions) {
                unregister();
            }
            _unregisterActions.Clear();
        }

        protected virtual void OnDestroy() {
            UnbindAll();
            UnsubscribeEvents();
        }
    }
}
