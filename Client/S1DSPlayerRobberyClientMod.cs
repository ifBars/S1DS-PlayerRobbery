#if CLIENT
using System;
using System.Collections.Generic;
using System.Text;
using DedicatedServerMod.API;
using DedicatedServerMod.API.Metadata;
using DedicatedServerMod.Shared.Networking;
#if IL2CPP
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
#endif
using MelonLoader;
using Newtonsoft.Json;
#if IL2CPP
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.ItemFramework;
using ScheduleOne.Persistence;
using ScheduleOne.PlayerScripts;
#endif
using UnityEngine;

[assembly: MelonInfo(typeof(S1DSMod.PlayerRobbery.S1DSPlayerRobberyClientMod), S1DSMod.PlayerRobbery.AddonMetadata.ModName, S1DSMod.PlayerRobbery.AddonMetadata.Version, S1DSMod.PlayerRobbery.AddonMetadata.Author)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: S1DSClientModIdentity(S1DSMod.PlayerRobbery.AddonMetadata.ModId, S1DSMod.PlayerRobbery.AddonMetadata.Version)]

namespace S1DSMod.PlayerRobbery
{
    public sealed partial class S1DSPlayerRobberyClientMod : ClientMelonModBase
    {
        private readonly Dictionary<Player, RobberyTargetInteractable> interactablesByPlayer = new Dictionary<Player, RobberyTargetInteractable>();
        private readonly HashSet<string> surrenderedTargetIds = new HashSet<string>(StringComparer.Ordinal);

        private RobberyPickpocketScreenUi robberyMenu;
        private bool hooksInstalled;
        private bool clientReady;
        private bool localHandsUp;

        public override void OnInitializeMelon()
        {
            robberyMenu = new RobberyPickpocketScreenUi(LoggerInstance, SendTakeRequest, CloseActiveSession, () => localHandsUp);
            LoggerInstance.Msg("S1DS player robbery client addon initialized.");
        }

        public override void OnClientPlayerReady()
        {
            clientReady = true;
            InstallHooks();
            RebuildPlayerInteractables();
        }

        public override void OnDisconnectedFromServer()
        {
            clientReady = false;
            localHandsUp = false;
            surrenderedTargetIds.Clear();
            robberyMenu.Close(string.Empty, restoreControlState: true);
            RefreshInteractables();
        }

        public override void OnClientShutdown()
        {
            if (hooksInstalled)
            {
                Player.onPlayerSpawned -= new Action<Player>(HandlePlayerSpawned);
                Player.onPlayerDespawned -= new Action<Player>(HandlePlayerDespawned);
                hooksInstalled = false;
            }

            robberyMenu.Close(string.Empty, restoreControlState: true);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (clientReady)
            {
                RebuildPlayerInteractables();
            }
        }

        public override void OnUpdate()
        {
            if (!clientReady || !S1DS.Client.IsConnected)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.X) && CanToggleHandsUp())
            {
                SendHandsUpRequest(!localHandsUp);
            }

            if (robberyMenu.IsOpen)
            {
                robberyMenu.Tick();
            }

            RefreshInteractables();
        }

        public override bool OnCustomMessage(string messageType, byte[] data)
        {
            string json = data != null && data.Length > 0
                ? Encoding.UTF8.GetString(data)
                : string.Empty;

            switch (messageType)
            {
                case Cmds.HandsUpState:
                    ApplyHandsUpState(Parse<HandsUpStateMessage>(json));
                    return true;
                case Cmds.SessionState:
                    ApplySessionState(Parse<RobberySessionMessage>(json));
                    return true;
                case Cmds.CloseSession:
                    ApplySessionClose(Parse<RobberyCloseMessage>(json));
                    return true;
                case Cmds.ApplyAdd:
                    ApplyInventoryAdd(Parse<RobberyApplyAddMessage>(json));
                    return true;
                case Cmds.ApplyRemove:
                    ApplyInventoryRemove(Parse<RobberyApplyRemoveMessage>(json));
                    return true;
                case Cmds.Error:
                    ApplyError(Parse<RobberyErrorMessage>(json));
                    return true;
                default:
                    return false;
            }
        }

        internal void RequestOpenRobbery(Player target)
        {
            if (target == null || !CanInteractWithTarget(target))
            {
                return;
            }

            CustomMessaging.SendToServer(Cmds.OpenSession, JsonConvert.SerializeObject(new OpenRobberyRequest
            {
                TargetId = GetPlayerId(target)
            }));
        }

        private void InstallHooks()
        {
            if (hooksInstalled)
            {
                return;
            }

            Player.onPlayerSpawned += new Action<Player>(HandlePlayerSpawned);
            Player.onPlayerDespawned += new Action<Player>(HandlePlayerDespawned);
            hooksInstalled = true;
        }

        private void HandlePlayerSpawned(Player player)
        {
            AttachInteractable(player);
            RefreshInteractables();
        }

        private void HandlePlayerDespawned(Player player)
        {
            if (player == null)
            {
                return;
            }

            if (interactablesByPlayer.TryGetValue(player, out RobberyTargetInteractable interactable) && interactable != null)
            {
                UnityEngine.Object.Destroy(interactable);
            }

            interactablesByPlayer.Remove(player);
            if (robberyMenu.IsOpen && string.Equals(robberyMenu.TargetId, GetPlayerId(player), StringComparison.Ordinal))
            {
                robberyMenu.Close("Target unavailable.", restoreControlState: true);
            }
        }

        private void RebuildPlayerInteractables()
        {
            for (int i = 0; i < Player.PlayerList.Count; i++)
            {
                AttachInteractable(Player.PlayerList[i]);
            }

            RefreshInteractables();
        }

        private void AttachInteractable(Player player)
        {
            if (player == null || player == Player.Local || interactablesByPlayer.ContainsKey(player))
            {
                return;
            }

            RobberyTargetInteractable interactable = RobberyTargetInteractable.GetOrCreate(this, player);
            interactablesByPlayer[player] = interactable;
        }

        private void RefreshInteractables()
        {
            List<Player> stalePlayers = null;
            foreach (KeyValuePair<Player, RobberyTargetInteractable> pair in interactablesByPlayer)
            {
                Player player = pair.Key;
                RobberyTargetInteractable interactable = pair.Value;
                if (player == null || interactable == null)
                {
                    if (stalePlayers == null)
                    {
                        stalePlayers = new List<Player>();
                    }

                    stalePlayers.Add(player);
                    continue;
                }

                interactable.SetAvailable(CanInteractWithTarget(player));
            }

            if (stalePlayers == null)
            {
                return;
            }

            for (int i = 0; i < stalePlayers.Count; i++)
            {
                interactablesByPlayer.Remove(stalePlayers[i]);
            }
        }

        private bool CanInteractWithTarget(Player target)
        {
            if (target == null || target == Player.Local || robberyMenu.IsOpen)
            {
                return false;
            }

            if (!target.Health.IsAlive || target.IsArrested || target.IsSleeping || target.IsUnconscious || target.IsInVehicle)
            {
                return false;
            }

            string targetId = GetPlayerId(target);
            return !string.IsNullOrWhiteSpace(targetId) && surrenderedTargetIds.Contains(targetId);
        }

        private bool CanToggleHandsUp()
        {
            if (Player.Local == null || PlayerSingleton<PlayerCamera>.Instance == null)
            {
                return false;
            }

            if (robberyMenu.IsOpen || GameInput.IsTyping)
            {
                return false;
            }

            if (Player.Local.IsArrested || Player.Local.IsTased || Player.Local.IsSleeping || Player.Local.IsUnconscious || Player.Local.IsRagdolled || Player.Local.IsInVehicle)
            {
                return false;
            }

            if (!Player.Local.Health.IsAlive)
            {
                return false;
            }

            return PlayerSingleton<PlayerCamera>.Instance.activeUIElementCount == 0;
        }

        private void SendHandsUpRequest(bool state)
        {
            CustomMessaging.SendToServer(Cmds.SetHandsUp, JsonConvert.SerializeObject(new ToggleHandsUpRequest
            {
                IsHandsUp = state
            }));
        }

        private void SendTakeRequest(int slotIndex)
        {
            if (!robberyMenu.IsOpen || string.IsNullOrWhiteSpace(robberyMenu.SessionId))
            {
                return;
            }

            CustomMessaging.SendToServer(Cmds.TakeSlot, JsonConvert.SerializeObject(new RobberyTakeRequest
            {
                SessionId = robberyMenu.SessionId,
                SlotIndex = slotIndex
            }));
        }

        private void CloseActiveSession()
        {
            if (!robberyMenu.IsOpen)
            {
                return;
            }

            string sessionId = robberyMenu.SessionId;
            string targetId = robberyMenu.TargetId;
            robberyMenu.Close(string.Empty, restoreControlState: true);

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                CustomMessaging.SendToServer(Cmds.CloseSession, JsonConvert.SerializeObject(new RobberyCloseMessage
                {
                    SessionId = sessionId,
                    TargetId = targetId,
                    Reason = string.Empty
                }));
            }
        }

        private void ApplyHandsUpState(HandsUpStateMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.TargetId))
            {
                return;
            }

            if (message.IsHandsUp)
            {
                surrenderedTargetIds.Add(message.TargetId);
            }
            else
            {
                surrenderedTargetIds.Remove(message.TargetId);
            }

            if (Player.Local != null && string.Equals(GetPlayerId(Player.Local), message.TargetId, StringComparison.Ordinal))
            {
                localHandsUp = message.IsHandsUp;
                if (PlayerSingleton<PlayerInventory>.InstanceExists)
                {
                    PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(!localHandsUp);
                }
            }

            if (robberyMenu.IsOpen && string.Equals(robberyMenu.TargetId, message.TargetId, StringComparison.Ordinal) && !message.IsHandsUp)
            {
                robberyMenu.Close("Target lowered their hands.", restoreControlState: true);
            }

            RefreshInteractables();
        }

        private void ApplySessionState(RobberySessionMessage message)
        {
            if (message != null)
            {
                robberyMenu.Open(message);
            }
        }

        private void ApplySessionClose(RobberyCloseMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (robberyMenu.IsOpen && string.Equals(robberyMenu.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                robberyMenu.Close(message.Reason, restoreControlState: true);
            }
        }

        private void ApplyInventoryAdd(RobberyApplyAddMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.ItemJson) || !PlayerSingleton<PlayerInventory>.InstanceExists)
            {
                return;
            }

            ItemInstance item = ItemDeserializer.LoadItem(message.ItemJson);
            if (item == null)
            {
                LoggerInstance.Warning("S1DS-PlayerRobbery: failed to deserialize robbery loot item.");
                return;
            }

            PlayerSingleton<PlayerInventory>.Instance.AddItemToInventory(item);
        }

        private void ApplyInventoryRemove(RobberyApplyRemoveMessage message)
        {
            if (message == null || !PlayerSingleton<PlayerInventory>.InstanceExists)
            {
                return;
            }

            if (message.SlotIndex < 0 || message.SlotIndex >= PlayerSingleton<PlayerInventory>.Instance.hotbarSlots.Count || message.Quantity <= 0)
            {
                return;
            }

            PlayerSingleton<PlayerInventory>.Instance.hotbarSlots[message.SlotIndex].ChangeQuantity(-message.Quantity);
        }

        private void ApplyError(RobberyErrorMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Message))
            {
                return;
            }

            LoggerInstance.Warning($"S1DS-PlayerRobbery: {message.Message}");
            if (robberyMenu.IsOpen)
            {
                robberyMenu.SetStatus(message.Message);
            }
        }

        private T Parse<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"S1DS-PlayerRobbery: failed to parse {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static string GetPlayerId(Player player)
        {
            return player != null ? player.PlayerCode ?? string.Empty : string.Empty;
        }

#if IL2CPP
        [RegisterTypeInIl2Cpp]
#endif
        private sealed class RobberyTargetInteractable : InteractableObject
        {
            private const string InteractionObjectName = "Pickpocket Interaction";

            private S1DSPlayerRobberyClientMod owner;
            private Player target;
            private SphereCollider interactionCollider;

#if IL2CPP
            public RobberyTargetInteractable(IntPtr ptr)
                : base(ptr)
            {
            }
#endif

#if IL2CPP
            [HideFromIl2Cpp]
#endif
            public static RobberyTargetInteractable GetOrCreate(S1DSPlayerRobberyClientMod mod, Player player)
            {
                if (player == null)
                {
                    return null;
                }

                Transform parent = player.transform;
                Transform interactionTransform = null;
                Transform[] transforms = player.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i] != null && string.Equals(transforms[i].name, InteractionObjectName, StringComparison.Ordinal))
                    {
                        interactionTransform = transforms[i];
                        break;
                    }
                }

                GameObject interactionObject = interactionTransform != null ? interactionTransform.gameObject : null;
                if (interactionObject == null)
                {
                    interactionObject = new GameObject(InteractionObjectName);
                }

                interactionObject.transform.SetParent(parent, false);
                interactionObject.transform.localRotation = Quaternion.identity;
                interactionObject.transform.localScale = Vector3.one;

                RobberyTargetInteractable interactable = interactionObject.GetComponent<RobberyTargetInteractable>();
                if (interactable == null)
                {
                    interactable = interactionObject.AddComponent<RobberyTargetInteractable>();
                }

                interactable.Initialize(mod, player);
                return interactable;
            }

#if IL2CPP
            [HideFromIl2Cpp]
#endif
            public void Initialize(S1DSPlayerRobberyClientMod mod, Player player)
            {
                owner = mod;
                target = player;
                gameObject.layer = ResolveInteractionLayer();
                MaxInteractionRange = 4.0f;
                RequiresUniqueClick = true;
                Priority = 250;
                SetInteractionType(EInteractionType.Key_Press);
                EnsureCollider();
                UpdatePlacement();
                displayLocationPoint = null;
                LimitInteractionAngle = true;
                AngleLimit = 90f;
            }

#if IL2CPP
            [HideFromIl2Cpp]
#endif
            public void SetAvailable(bool available)
            {
                UpdatePlacement();
                enabled = available;
                if (interactionCollider != null)
                {
                    interactionCollider.enabled = available;
                }

                SetInteractableState(available ? EInteractableState.Default : EInteractableState.Disabled);
                if (target != null)
                {
                    SetMessage($"Search {target.PlayerName}'s pockets");
                }
            }

            public override void StartInteract()
            {
                if (owner == null || target == null)
                {
                    return;
                }

                base.StartInteract();
                owner.RequestOpenRobbery(target);
            }

            private void EnsureCollider()
            {
                interactionCollider = GetComponent<SphereCollider>();
                if (interactionCollider == null)
                {
                    interactionCollider = gameObject.AddComponent<SphereCollider>();
                }

                interactionCollider.isTrigger = true;
                interactionCollider.radius = 0.24f;
                interactionCollider.center = Vector3.zero;
                interactionCollider.gameObject.layer = ResolveInteractionLayer();
            }

            private void UpdatePlacement()
            {
                if (target == null)
                {
                    return;
                }
                
                transform.localPosition = new Vector3(0f, 0.5f, 0f);

                if (target.CapCol != null)
                {
                    displayLocationCollider = target.CapCol;
                }
                else
                {
                    displayLocationCollider = interactionCollider;
                }
            }

            private static int ResolveInteractionLayer()
            {
                int npcLayer = LayerMask.NameToLayer("NPC");
                if (npcLayer >= 0)
                {
                    return npcLayer;
                }

                int defaultLayer = LayerMask.NameToLayer("Default");
                return defaultLayer >= 0 ? defaultLayer : 0;
            }
        }

    }
}
#endif
