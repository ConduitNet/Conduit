using System;
using System.Collections.Generic;
using ConduitNet;
using ConduitNet.DTO;
using Newtonsoft.Json;

namespace ConduitNet {
    /// <summary>Interface for user-related server requests.</summary>
    public interface IUserService {
        /// <summary>Fetches up-to-date user data from the server.</summary>
        public void Fetch(IUser user, Action callback = null);
        /// <summary>Applies a patch to a user's account state on the server (host-only).</summary>
        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state);
    }

    internal interface IUserServiceInternal : IUserService {
        void HandleFetchResponse(DataResponseDTO res);
        IUser Deserialize(string jsonUser);
    }

    /// <summary>Implementation of the typed user service.</summary>
    public class UserService<TUserProfile, TAccountState> : IUserService, IUserServiceInternal where TUserProfile : class where TAccountState : class {
        private readonly Dictionary<string, Action<IUser>> _userFetchCallbacks = new();

        public void Fetch(IUser user, Action callback = null) {
            Conduit.SendSignalingMessage(SignalingMsgType.RequestData, "server", new DataRequestDTO {
                type = DataRequestType.User,
                target = user.Id
            });

            _userFetchCallbacks[user.Id] = (source) => {
                user.Apply(source);
                callback?.Invoke();
            };
        }

        public void ApplyAccountState<TAccountStatePatch>(IUser user, TAccountStatePatch state) {
            if (state == null) return;

            if (!Conduit.IsHost) UnityEngine.Debug.LogWarning("ApplyAccountState in UserService can only be called by the host");

            // Immediately patch local user and fire event (1st of 2 invocations).
            if (user.TryCast<TUserProfile, TAccountState>(out var typedUser)) {
                if (state is TAccountState) {
                    typedUser.Account = state as TAccountState;
                } else {
                    JsonConvert.PopulateObject(JsonConvert.SerializeObject(state), typedUser.Account);
                }
                Conduit.TriggerUserAccountStateUpdated();
            }

            Conduit.SendSignalingMessage(SignalingMsgType.ApplyData, "server", new DataApplyDTO {
                type = DataChangeType.UserAccount,
                target = user.Id,
                data = JsonConvert.SerializeObject(state)
            });
        }

        void IUserServiceInternal.HandleFetchResponse(DataResponseDTO res) {
            if (_userFetchCallbacks.TryGetValue(res.target, out Action<IUser> callback)) {
                var source = User<TUserProfile, TAccountState>.FromJson(res.data);
                callback.Invoke(source);
                _userFetchCallbacks.Remove(res.target);
            }
        }

        public IUser Deserialize(string jsonUser) {
            return User<TUserProfile, TAccountState>.FromJson(jsonUser);
        }
    }
}