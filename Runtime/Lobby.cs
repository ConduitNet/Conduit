using System;
using System.Collections.Generic;
using System.Linq;
using ConduitNet;
using ConduitNet.DTO;
using Newtonsoft.Json;

namespace ConduitNet {
    /// <summary>Interface for lobby-related server requests.</summary>
    public interface ILobbyService {
        /// <summary>Applies a patch to lobby metadata on the server (host-only).</summary>
        public void ApplyMetadata(ILobby lobby, string name = null, bool? isPlaying = null, bool? isPrivate = null, int? maxPlayers = null);
        /// <summary>Applies a patch to lobby custom state on the server (host-only).</summary>
        public void ApplyState<TLobbyStatePatch>(ILobby lobby, TLobbyStatePatch state);
        /// <summary>Fetches up-to-date lobby data from the server.</summary>
        public void Fetch(ILobby lobby, Action callback = null);
    }

    internal interface ILobbyServiceInternal : ILobbyService {
        void HandleFetchResponse(DataResponseDTO res);
        ILobby Deserialize(string jsonLobby);
    }

    /// <summary>Implementation of the typed lobby service.</summary>
    public class LobbyService<TLobbyState, TUserProfile, TAccountState> : ILobbyService, ILobbyServiceInternal where TLobbyState : class where TUserProfile : class where TAccountState : class {
        private Dictionary<string, Action<ILobby>> _lobbyFetchCallbacks = new();

        public void ApplyMetadata(ILobby lobby, string name = null, bool? isPlaying = null, bool? isPrivate = null, int? maxPlayers = null) {
            if (name == null && isPlaying == null && isPrivate == null && maxPlayers == null) return;

            if (!Conduit.IsHost) UnityEngine.Debug.LogWarning("ApplyMetadata in LobbyService can only be called by the host");

            // Immediately patch local lobby and fire event (1st of 2 invocations).
            if (lobby.TryCast(out Lobby<TLobbyState> typedLobby)) {
                if (name != null)        typedLobby.Name       = name;
                if (isPlaying.HasValue)  typedLobby.IsPlaying  = isPlaying.Value;
                if (isPrivate.HasValue)  typedLobby.IsPrivate  = isPrivate.Value;
                if (maxPlayers.HasValue) typedLobby.MaxPlayers = maxPlayers.Value;
                Conduit.TriggerLobbyMetadataUpdated();
            }

            Conduit.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.LobbyMetadata,
                target = lobby.Id,
                data = JsonConvert.SerializeObject(new LobbyMetadataUpdateDTO {
                    name = name,
                    isPlaying = isPlaying,
                    isPrivate = isPrivate,
                    maxPlayers = maxPlayers
                })
            });
        }

        public void ApplyState<TLobbyStatePatch>(ILobby lobby, TLobbyStatePatch state) {
            if (state == null) return;

            if (!Conduit.IsHost) UnityEngine.Debug.LogWarning("ApplyState in LobbyService can only be called by the host");

            // Immediately patch local lobby and fire event (1st of 2 invocations).
            if (lobby.TryCast(out Lobby<TLobbyState> typedLobby)) {
                if (state is TLobbyState) {
                    typedLobby.State = state as TLobbyState;
                } else {
                    JsonConvert.PopulateObject(JsonConvert.SerializeObject(state), typedLobby.State);
                }
                Conduit.TriggerLobbyStateUpdated();
            }

            Conduit.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.LobbyState,
                target = lobby.Id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        public ILobby Deserialize(string jsonLobby) {
            return Lobby<TLobbyState>.FromJson<TUserProfile, TAccountState>(jsonLobby);
        }

        public void Fetch(ILobby lobby, Action callback = null) {
            Conduit.SendSignalingMessage(SignalingMsgType.RequestData, "server", new DataRequestDTO {
                type = DataRequestType.Lobby,
                target = lobby.Id
            });

            _lobbyFetchCallbacks[lobby.Id] = (source) => {
                lobby.Apply(source);
                callback?.Invoke();
            };
        }

        void ILobbyServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_lobbyFetchCallbacks.TryGetValue(res.target, out Action<ILobby> callback)) {
                var source = Deserialize(res.data);
                callback.Invoke(source);
                _lobbyFetchCallbacks.Remove(res.target);
            }
        }
    }
}