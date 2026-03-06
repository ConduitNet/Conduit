using System;
using System.Collections.Generic;
using System.Reflection;
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
        }

        private void RegisterAttributeHandlers() {
            var methods = GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods) {
                // [BytesHandler]
                if (method.GetCustomAttribute<BytesHandlerAttribute>() != null) {
                    if (!TryCreateDelegate(method, typeof(BytesHandler), out var handler,
                        "void (IUser sender, ReadOnlyMemory<byte> data, SendOption option)")) continue;
                    BindBytesHandler((BytesHandler)handler);
                    continue;
                }

                // [SignalHandler("name")]
                var signalAttr = method.GetCustomAttribute<SignalHandlerAttribute>();
                if (signalAttr != null) {
                    if (!TryCreateDelegate(method, typeof(SignalHandler), out var handler,
                        "void (IUser sender, long timestamp, SendOption option)")) continue;
                    BindSignalHandler(signalAttr.SignalName, (SignalHandler)handler);
                    continue;
                }

                // [PacketHandler(typeof(T))]
                var packetAttr = method.GetCustomAttribute<PacketHandlerAttribute>();
                if (packetAttr != null) {
                    Type packetType = packetAttr.PacketType;
                    Type delegateType = typeof(PacketHandler<>).MakeGenericType(packetType);
                    if (!TryCreateDelegate(method, delegateType, out var handler,
                        $"void (IUser sender, long timestamp, {packetType.Name} packet, SendOption option)")) continue;

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
        protected void BindPacketHandler<T>(PacketHandler<T> handler) {
            Conduit.RegisterPacketHandler(handler);
            _unregisterActions.Add(() => Conduit.UnregisterPacketHandler(handler));
        }

        /// <summary>Unbinds a typed packet handler.</summary>
        protected void UnbindPacketHandler<T>(PacketHandler<T> handler) {
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
        }
    }
}
