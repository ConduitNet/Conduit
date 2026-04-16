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

        #region Source Generator Bindings

        protected virtual void Awake() {
            if (ConduitRegistry.GeneratedBinders.TryGetValue(this.GetType(), out var binder)) {
                binder(this);
            }
            SubscribeEvents();
        }

        #endregion
        
        #region Source Generator Bindings

        // Called by Source Generator (Internal use only)
        internal void InternalBindGeneratedBytesHandler(BytesHandler handler, Role senderRole, Role receiverRole) {
            if (senderRole != Role.Any || receiverRole != Role.Any) {
                var original = handler;
                handler = ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) original(ctx); };
            }
            BindBytesHandler(handler);
        }

        // Called by Source Generator (Internal use only)
        internal void InternalBindGeneratedSignalHandler(string signalName, SignalHandler handler, Role senderRole, Role receiverRole) {
            if (senderRole != Role.Any || receiverRole != Role.Any) {
                var original = handler;
                handler = ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) original(ctx); };
            }
            BindSignalHandler(signalName, handler);
        }

        // Called by Source Generator (Internal use only)
        internal void InternalBindGeneratedPacketHandler<T>(PacketHandler<T> handler, Role senderRole, Role receiverRole) where T : INetworkPacket {
            if (senderRole != Role.Any || receiverRole != Role.Any) {
                var original = handler;
                handler = ctx => { if (PassesPeerFilter(ctx.Sender, senderRole, receiverRole)) original(ctx); };
            }
            BindPacketHandler(handler);
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
            Conduit.OnTimeSynced += OnTimeSynced;
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
            Conduit.OnTimeSynced -= OnTimeSynced;
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
        /// <summary>Called when clock synchronization with the host completes.</summary>
        protected virtual void OnTimeSynced(long offsetMs) { }

        #endregion

        #region Peer Filter

        private static bool MatchesRole(IUser user, Role role) => role switch {
            Role.Host   => Conduit.Host != null && user.Id == Conduit.Host.Id,
            Role.Member => Conduit.Host != null && user.Id != Conduit.Host.Id,
            _               => true
        };

        private static bool PassesPeerFilter(IUser sender, Role senderRole, Role receiverRole) =>
            MatchesRole(sender, senderRole) && (receiverRole == Role.Any || MatchesRole(Conduit.LocalUser, receiverRole));



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
