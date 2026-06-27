using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace S1DSMod.PlayerRobbery
{
    internal static class Cmds
    {
        public const string SetHandsUp = "robbery_set_handsup";
        public const string HandsUpState = "robbery_handsup_state";
        public const string OpenSession = "robbery_open_session";
        public const string SessionState = "robbery_session_state";
        public const string CloseSession = "robbery_close_session";
        public const string TakeSlot = "robbery_take_slot";
        public const string ApplyAdd = "robbery_apply_add";
        public const string ApplyRemove = "robbery_apply_remove";
        public const string Error = "robbery_error";
    }

    [Serializable]
    internal sealed class ToggleHandsUpRequest
    {
        [JsonProperty("isHandsUp")]
        public bool IsHandsUp { get; set; }
    }

    [Serializable]
    internal sealed class OpenRobberyRequest
    {
        [JsonProperty("targetId")]
        public string TargetId { get; set; } = string.Empty;
    }

    [Serializable]
    internal sealed class HandsUpStateMessage
    {
        [JsonProperty("targetId")]
        public string TargetId { get; set; } = string.Empty;

        [JsonProperty("isHandsUp")]
        public bool IsHandsUp { get; set; }
    }

    [Serializable]
    internal sealed class LootableItemMessage
    {
        [JsonProperty("slotIndex")]
        public int SlotIndex { get; set; }

        [JsonProperty("itemJson")]
        public string ItemJson { get; set; } = string.Empty;

        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }

    [Serializable]
    internal sealed class RobberySessionMessage
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("targetId")]
        public string TargetId { get; set; } = string.Empty;

        [JsonProperty("targetName")]
        public string TargetName { get; set; } = string.Empty;

        [JsonProperty("items")]
        public List<LootableItemMessage> Items { get; set; } = new List<LootableItemMessage>();
    }

    [Serializable]
    internal sealed class RobberyTakeRequest
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("slotIndex")]
        public int SlotIndex { get; set; }
    }

    [Serializable]
    internal sealed class RobberyApplyAddMessage
    {
        [JsonProperty("itemJson")]
        public string ItemJson { get; set; } = string.Empty;
    }

    [Serializable]
    internal sealed class RobberyApplyRemoveMessage
    {
        [JsonProperty("slotIndex")]
        public int SlotIndex { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }
    }

    [Serializable]
    internal sealed class RobberyCloseMessage
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonProperty("targetId")]
        public string TargetId { get; set; } = string.Empty;

        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    [Serializable]
    internal sealed class RobberyErrorMessage
    {
        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }
}
