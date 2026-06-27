#if SERVER
using System;
using System.Collections.Generic;
using System.Linq;
using DedicatedServerMod.API;
using DedicatedServerMod.API.Metadata;
using DedicatedServerMod.Server.Player;
using DedicatedServerMod.Shared.Networking;
using FishNet.Connection;
using MelonLoader;
using Newtonsoft.Json;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using UnityEngine;

[assembly: MelonInfo(typeof(S1DSMod.PlayerRobbery.S1DSPlayerRobberyServerMod), S1DSMod.PlayerRobbery.AddonMetadata.ModName, S1DSMod.PlayerRobbery.AddonMetadata.Version, S1DSMod.PlayerRobbery.AddonMetadata.Author)]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: S1DSClientCompanion(S1DSMod.PlayerRobbery.AddonMetadata.ModId, S1DSMod.PlayerRobbery.AddonMetadata.DisplayName, Required = true, MinVersion = S1DSMod.PlayerRobbery.AddonMetadata.Version)]

namespace S1DSMod.PlayerRobbery
{
    public sealed class S1DSPlayerRobberyServerMod : ServerMelonModBase
    {
        private const float RobberyRange = 3.25f;
        private const int LootableSlotCount = 8;

        private readonly Dictionary<string, bool> surrenderedByPlayerId = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, RobberySession> sessionsById = new Dictionary<string, RobberySession>(StringComparer.Ordinal);

        public override void OnServerInitialize()
        {
            LoggerInstance.Msg("S1DS player robbery server addon initialized.");
        }

        public override void OnPlayerConnected(ConnectedPlayerInfo player)
        {
            if (player == null)
            {
                return;
            }

            foreach (KeyValuePair<string, bool> state in surrenderedByPlayerId)
            {
                if (state.Value)
                {
                    SendHandsUpState(player.Connection, state.Key, true);
                }
            }
        }

        public override void OnPlayerDisconnected(ConnectedPlayerInfo player)
        {
            string playerId = GetPlayerId(player);
            if (string.IsNullOrEmpty(playerId))
            {
                return;
            }

            bool wasSurrendered = surrenderedByPlayerId.Remove(playerId);
            if (wasSurrendered)
            {
                BroadcastHandsUpState(playerId, false);
            }

            CloseSessionsForPlayer(playerId, "Player unavailable.");
        }

        public override bool OnCustomMessage(string messageType, byte[] data, ConnectedPlayerInfo sender)
        {
            if (sender == null)
            {
                return false;
            }

            string json = data != null && data.Length > 0
                ? System.Text.Encoding.UTF8.GetString(data)
                : string.Empty;

            switch (messageType)
            {
                case Cmds.SetHandsUp:
                    HandleSetHandsUp(sender, json);
                    return true;
                case Cmds.OpenSession:
                    HandleOpenSession(sender, json);
                    return true;
                case Cmds.TakeSlot:
                    HandleTakeSlot(sender, json);
                    return true;
                case Cmds.CloseSession:
                    HandleCloseSession(sender, json);
                    return true;
                default:
                    return false;
            }
        }

        private void HandleSetHandsUp(ConnectedPlayerInfo sender, string json)
        {
            ToggleHandsUpRequest request = Deserialize<ToggleHandsUpRequest>(json) ?? new ToggleHandsUpRequest();
            string playerId = GetPlayerId(sender);
            Player player = sender.PlayerInstance;
            if (player == null || string.IsNullOrEmpty(playerId))
            {
                return;
            }

            bool nextState = request.IsHandsUp && CanUseHandsUp(player);
            bool currentState = surrenderedByPlayerId.TryGetValue(playerId, out bool active) && active;
            if (currentState == nextState)
            {
                return;
            }

            if (nextState)
            {
                surrenderedByPlayerId[playerId] = true;
            }
            else
            {
                surrenderedByPlayerId.Remove(playerId);
                CloseSessionsForPlayer(playerId, "Target is no longer surrendering.");
            }

            player.SetAnimationBool("HandsUp", nextState);
            BroadcastHandsUpState(playerId, nextState);
        }

        private void HandleOpenSession(ConnectedPlayerInfo robberInfo, string json)
        {
            OpenRobberyRequest request = Deserialize<OpenRobberyRequest>(json);
            if (request == null || string.IsNullOrWhiteSpace(request.TargetId))
            {
                SendError(robberInfo.Connection, "Missing robbery target.");
                return;
            }

            string robberId = GetPlayerId(robberInfo);
            ConnectedPlayerInfo targetInfo = ResolvePlayer(request.TargetId);
            if (!TryValidateRobbery(robberInfo, robberId, targetInfo, out string reason))
            {
                SendError(robberInfo.Connection, reason);
                return;
            }

            CloseSessionsForPlayer(robberId, "Session replaced.");

            RobberySession session = new RobberySession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                RobberId = robberId,
                TargetId = GetPlayerId(targetInfo)
            };

            sessionsById[session.SessionId] = session;
            SendSessionState(robberInfo.Connection, session, targetInfo.PlayerInstance);
        }

        private void HandleTakeSlot(ConnectedPlayerInfo robberInfo, string json)
        {
            RobberyTakeRequest request = Deserialize<RobberyTakeRequest>(json);
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                SendError(robberInfo.Connection, "Missing robbery session.");
                return;
            }

            if (!sessionsById.TryGetValue(request.SessionId, out RobberySession session))
            {
                SendClose(robberInfo.Connection, new RobberyCloseMessage
                {
                    SessionId = request.SessionId,
                    Reason = "Robbery session expired."
                });
                return;
            }

            string robberId = GetPlayerId(robberInfo);
            if (!string.Equals(session.RobberId, robberId, StringComparison.Ordinal))
            {
                SendError(robberInfo.Connection, "That robbery session belongs to another player.");
                return;
            }

            ConnectedPlayerInfo targetInfo = ResolvePlayer(session.TargetId);
            if (!TryValidateRobbery(robberInfo, robberId, targetInfo, out string reason))
            {
                CloseSession(session.SessionId, reason);
                return;
            }

            if (request.SlotIndex < 0 || request.SlotIndex >= LootableSlotCount)
            {
                SendError(robberInfo.Connection, "Invalid loot slot.");
                return;
            }

            Player robber = robberInfo.PlayerInstance;
            Player target = targetInfo.PlayerInstance;
            ItemSlot sourceSlot = target.Inventory[request.SlotIndex];
            ItemInstance sourceItem = sourceSlot != null ? sourceSlot.ItemInstance : null;
            if (sourceItem == null || string.Equals(sourceItem.ID, "cash", StringComparison.OrdinalIgnoreCase))
            {
                SendSessionState(robberInfo.Connection, session, target);
                return;
            }

            int transferableQuantity = GetTransferableQuantity(robber.Inventory, sourceItem);
            if (transferableQuantity <= 0)
            {
                SendError(robberInfo.Connection, "Inventory full.");
                return;
            }

            transferableQuantity = Math.Min(transferableQuantity, sourceItem.Quantity);
            ItemInstance transferItem = sourceItem.GetCopy(transferableQuantity);
            ApplyTransferOnServer(target.Inventory, request.SlotIndex, transferItem.Quantity, robber.Inventory, transferItem);

            CustomMessaging.SendToClient(targetInfo.Connection, Cmds.ApplyRemove, JsonConvert.SerializeObject(new RobberyApplyRemoveMessage
            {
                SlotIndex = request.SlotIndex,
                Quantity = transferItem.Quantity
            }));

            CustomMessaging.SendToClient(robberInfo.Connection, Cmds.ApplyAdd, JsonConvert.SerializeObject(new RobberyApplyAddMessage
            {
                ItemJson = transferItem.GetItemData().GetJson(false)
            }));

            SendSessionState(robberInfo.Connection, session, target);
        }

        private void HandleCloseSession(ConnectedPlayerInfo robberInfo, string json)
        {
            RobberyCloseMessage request = Deserialize<RobberyCloseMessage>(json);
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                return;
            }

            if (sessionsById.TryGetValue(request.SessionId, out RobberySession session))
            {
                string robberId = GetPlayerId(robberInfo);
                if (string.Equals(session.RobberId, robberId, StringComparison.Ordinal))
                {
                    sessionsById.Remove(request.SessionId);
                }
            }
        }

        private bool TryValidateRobbery(ConnectedPlayerInfo robberInfo, string robberId, ConnectedPlayerInfo targetInfo, out string reason)
        {
            reason = string.Empty;
            if (robberInfo == null || targetInfo == null)
            {
                reason = "Target unavailable.";
                return false;
            }

            if (string.IsNullOrEmpty(robberId))
            {
                reason = "Invalid robber identity.";
                return false;
            }

            string targetId = GetPlayerId(targetInfo);
            if (string.IsNullOrEmpty(targetId) || string.Equals(targetId, robberId, StringComparison.Ordinal))
            {
                reason = "Invalid robbery target.";
                return false;
            }

            Player robber = robberInfo.PlayerInstance;
            Player target = targetInfo.PlayerInstance;
            if (robber == null || target == null)
            {
                reason = "Player entity unavailable.";
                return false;
            }

            if (!CanUseHandsUp(robber))
            {
                reason = "You cannot rob anyone right now.";
                return false;
            }

            if (!CanUseHandsUp(target))
            {
                reason = "Target cannot be robbed right now.";
                return false;
            }

            if (!surrenderedByPlayerId.TryGetValue(targetId, out bool surrendered) || !surrendered)
            {
                reason = "Target is not surrendering.";
                return false;
            }

            float distance = Vector3.Distance(robber.Avatar.CenterPoint, target.Avatar.CenterPoint);
            if (distance > RobberyRange)
            {
                reason = "Target is too far away.";
                return false;
            }

            return true;
        }

        private static bool CanUseHandsUp(Player player)
        {
            return player != null
                && player.Health != null
                && player.Health.IsAlive
                && !player.IsArrested
                && !player.IsTased
                && !player.IsSleeping
                && !player.IsUnconscious
                && !player.IsRagdolled
                && !player.IsInVehicle;
        }

        private void CloseSessionsForPlayer(string playerId, string reason)
        {
            List<string> sessionsToClose = sessionsById.Values
                .Where(session => string.Equals(session.RobberId, playerId, StringComparison.Ordinal) || string.Equals(session.TargetId, playerId, StringComparison.Ordinal))
                .Select(session => session.SessionId)
                .ToList();

            for (int i = 0; i < sessionsToClose.Count; i++)
            {
                CloseSession(sessionsToClose[i], reason);
            }
        }

        private void CloseSession(string sessionId, string reason)
        {
            if (!sessionsById.TryGetValue(sessionId, out RobberySession session))
            {
                return;
            }

            sessionsById.Remove(sessionId);

            ConnectedPlayerInfo robberInfo = ResolvePlayer(session.RobberId);
            if (robberInfo != null && robberInfo.Connection != null)
            {
                SendClose(robberInfo.Connection, new RobberyCloseMessage
                {
                    SessionId = sessionId,
                    TargetId = session.TargetId,
                    Reason = reason
                });
            }
        }

        private void BroadcastHandsUpState(string targetId, bool isHandsUp)
        {
            IReadOnlyList<ConnectedPlayerInfo> players = S1DS.Server.Players != null
                ? S1DS.Server.Players.GetConnectedPlayers()
                : Array.Empty<ConnectedPlayerInfo>();

            for (int i = 0; i < players.Count; i++)
            {
                SendHandsUpState(players[i].Connection, targetId, isHandsUp);
            }
        }

        private void SendHandsUpState(NetworkConnection connection, string targetId, bool isHandsUp)
        {
            if (connection == null || string.IsNullOrEmpty(targetId))
            {
                return;
            }

            CustomMessaging.SendToClient(connection, Cmds.HandsUpState, JsonConvert.SerializeObject(new HandsUpStateMessage
            {
                TargetId = targetId,
                IsHandsUp = isHandsUp
            }));
        }

        private void SendSessionState(NetworkConnection robberConnection, RobberySession session, Player target)
        {
            if (robberConnection == null || session == null || target == null)
            {
                return;
            }

            CustomMessaging.SendToClient(robberConnection, Cmds.SessionState, JsonConvert.SerializeObject(new RobberySessionMessage
            {
                SessionId = session.SessionId,
                TargetId = session.TargetId,
                TargetName = target.PlayerName ?? "Player",
                Items = BuildLootableItems(target)
            }));
        }

        private static List<LootableItemMessage> BuildLootableItems(Player target)
        {
            List<LootableItemMessage> items = new List<LootableItemMessage>();
            for (int i = 0; i < LootableSlotCount; i++)
            {
                ItemInstance item = target.Inventory[i] != null ? target.Inventory[i].ItemInstance : null;
                if (item == null || string.Equals(item.ID, "cash", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add(new LootableItemMessage
                {
                    SlotIndex = i,
                    ItemJson = item.GetItemData().GetJson(false),
                    DisplayName = item.Name,
                    Quantity = item.Quantity
                });
            }

            return items;
        }

        private static int GetTransferableQuantity(ItemSlot[] destinationSlots, ItemInstance item)
        {
            if (destinationSlots == null || item == null)
            {
                return 0;
            }

            int remaining = item.Quantity;
            for (int i = 0; i < LootableSlotCount; i++)
            {
                ItemSlot slot = destinationSlots[i];
                ItemInstance destinationItem = slot != null ? slot.ItemInstance : null;
                if (destinationItem != null && destinationItem.CanStackWith(item, false))
                {
                    remaining -= destinationItem.StackLimit - destinationItem.Quantity;
                    if (remaining <= 0)
                    {
                        return item.Quantity;
                    }
                }
            }

            for (int i = 0; i < LootableSlotCount; i++)
            {
                if (destinationSlots[i] != null && destinationSlots[i].ItemInstance == null)
                {
                    remaining -= item.StackLimit;
                    if (remaining <= 0)
                    {
                        return item.Quantity;
                    }
                }
            }

            return Math.Max(0, item.Quantity - remaining);
        }

        private static void ApplyTransferOnServer(ItemSlot[] sourceSlots, int sourceSlotIndex, int quantity, ItemSlot[] destinationSlots, ItemInstance transferItem)
        {
            if (sourceSlots == null || destinationSlots == null || transferItem == null)
            {
                return;
            }

            ItemSlot sourceSlot = sourceSlots[sourceSlotIndex];
            if (sourceSlot != null)
            {
                sourceSlot.ChangeQuantity(-quantity);
            }

            List<ItemSlot> destinationHotbar = new List<ItemSlot>(LootableSlotCount);
            for (int i = 0; i < LootableSlotCount; i++)
            {
                destinationHotbar.Add(destinationSlots[i]);
            }

            ItemSlot.TryInsertItemIntoSet(destinationHotbar, transferItem.GetCopy(transferItem.Quantity));
        }

        private ConnectedPlayerInfo ResolvePlayer(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId) || S1DS.Server.Players == null)
            {
                return null;
            }

            ConnectedPlayerInfo exact = S1DS.Server.Players.GetPlayerBySteamId(playerId);
            if (exact != null)
            {
                return exact;
            }

            IReadOnlyList<ConnectedPlayerInfo> connectedPlayers = S1DS.Server.Players.GetConnectedPlayers();
            for (int i = 0; i < connectedPlayers.Count; i++)
            {
                ConnectedPlayerInfo player = connectedPlayers[i];
                if (string.Equals(GetPlayerId(player), playerId, StringComparison.Ordinal)
                    || (player.PlayerInstance != null && string.Equals(player.PlayerInstance.PlayerCode, playerId, StringComparison.Ordinal))
                    || string.Equals(player.ClientId.ToString(), playerId, StringComparison.Ordinal))
                {
                    return player;
                }
            }

            return null;
        }

        private static string GetPlayerId(ConnectedPlayerInfo player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(player.AuthenticatedSteamId))
            {
                return player.AuthenticatedSteamId;
            }

            if (!string.IsNullOrWhiteSpace(player.SteamId))
            {
                return player.SteamId;
            }

            if (player.PlayerInstance != null && !string.IsNullOrWhiteSpace(player.PlayerInstance.PlayerCode))
            {
                return player.PlayerInstance.PlayerCode;
            }

            return player.ClientId.ToString();
        }

        private void SendClose(NetworkConnection connection, RobberyCloseMessage message)
        {
            if (connection == null || message == null)
            {
                return;
            }

            CustomMessaging.SendToClient(connection, Cmds.CloseSession, JsonConvert.SerializeObject(message));
        }

        private void SendError(NetworkConnection connection, string message)
        {
            if (connection == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            CustomMessaging.SendToClient(connection, Cmds.Error, JsonConvert.SerializeObject(new RobberyErrorMessage
            {
                Message = message
            }));
        }

        private T Deserialize<T>(string json) where T : class
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

        private sealed class RobberySession
        {
            public string SessionId { get; set; } = string.Empty;
            public string RobberId { get; set; } = string.Empty;
            public string TargetId { get; set; } = string.Empty;
        }
    }
}
#endif
