#if CLIENT
using System;
using System.Collections.Generic;
using System.Text;
using DedicatedServerMod.API;
using DedicatedServerMod.API.Metadata;
using DedicatedServerMod.Shared.Networking;
using MelonLoader;
using Newtonsoft.Json;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Interaction;
using ScheduleOne.ItemFramework;
using ScheduleOne.Persistence;
using ScheduleOne.PlayerScripts;
using UnityEngine;
using UnityEngine.UI;

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
                Player.onPlayerSpawned -= HandlePlayerSpawned;
                Player.onPlayerDespawned -= HandlePlayerDespawned;
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

            Player.onPlayerSpawned += HandlePlayerSpawned;
            Player.onPlayerDespawned += HandlePlayerDespawned;
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
            List<Player> players = new List<Player>(Player.PlayerList);
            for (int i = 0; i < players.Count; i++)
            {
                AttachInteractable(players[i]);
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

        private sealed class RobberyTargetInteractable : InteractableObject
        {
            private const string InteractionObjectName = "Pickpocket Interaction";

            private S1DSPlayerRobberyClientMod owner;
            private Player target;
            private SphereCollider interactionCollider;

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

        private sealed class RobberyMenuUi
        {
            private readonly MelonLogger.Instance logger;
            private readonly Action<int> onTakeSlot;
            private readonly Action onClose;
            private readonly Func<bool> shouldKeepEquippingDisabled;
            private readonly List<RowRefs> rows = new List<RowRefs>();

            private GameObject canvasObject;
            private GameObject panelObject;
            private Text titleText;
            private Text statusText;
            private Font font;

            public bool IsOpen => panelObject != null && panelObject.activeSelf;
            public string SessionId { get; private set; } = string.Empty;
            public string TargetId { get; private set; } = string.Empty;

            public RobberyMenuUi(MelonLogger.Instance loggerInstance, Action<int> takeSlot, Action close, Func<bool> keepEquippingDisabled)
            {
                logger = loggerInstance;
                onTakeSlot = takeSlot;
                onClose = close;
                shouldKeepEquippingDisabled = keepEquippingDisabled;
            }

            public void Open(RobberySessionMessage message)
            {
                if (message == null)
                {
                    return;
                }

                EnsureBuilt();
                SessionId = message.SessionId ?? string.Empty;
                TargetId = message.TargetId ?? string.Empty;
                titleText.text = string.IsNullOrWhiteSpace(message.TargetName) ? "Search pockets" : $"Search {message.TargetName}'s pockets";
                statusText.text = message.Items != null && message.Items.Count > 0 ? "Take what fits." : "Nothing to steal.";
                RenderRows(message.Items ?? new List<LootableItemMessage>());
                panelObject.SetActive(true);
                ApplyOpenControlState();
            }

            public void Close(string reason, bool restoreControlState)
            {
                if (panelObject == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    logger.Warning($"S1DS-PlayerRobbery: {reason}");
                }

                panelObject.SetActive(false);
                SessionId = string.Empty;
                TargetId = string.Empty;
                statusText.text = string.Empty;
                if (restoreControlState)
                {
                    RestoreClosedControlState();
                }
            }

            public void Tick()
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    onClose();
                }
            }

            public void SetStatus(string message)
            {
                if (statusText != null)
                {
                    statusText.text = message ?? string.Empty;
                }
            }

            private void EnsureBuilt()
            {
                if (canvasObject != null)
                {
                    return;
                }

                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

                canvasObject = new GameObject("S1DSPlayerRobberyCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasObject);

                Canvas canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 6000;
                canvasObject.AddComponent<GraphicRaycaster>();

                CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);

                panelObject = CreateUiObject("Panel", canvasObject.transform, new Vector2(560f, 520f));
                panelObject.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 0.96f);

                RectTransform panelRect = panelObject.GetComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0.5f);
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.anchoredPosition = Vector2.zero;

                titleText = CreateText("Title", panelObject.transform, new Vector2(500f, 36f), 24, FontStyle.Bold);
                titleText.rectTransform.anchoredPosition = new Vector2(0f, -36f);

                statusText = CreateText("Status", panelObject.transform, new Vector2(500f, 28f), 16, FontStyle.Normal);
                statusText.color = new Color(0.82f, 0.85f, 0.9f, 1f);
                statusText.rectTransform.anchoredPosition = new Vector2(0f, -72f);

                CreateText("Hint", panelObject.transform, new Vector2(500f, 24f), 14, FontStyle.Normal, "Press Esc to close.")
                    .rectTransform.anchoredPosition = new Vector2(0f, -106f);

                for (int i = 0; i < 8; i++)
                {
                    float y = -150f - i * 42f;
                    GameObject rowObject = CreateUiObject($"Row{i}", panelObject.transform, new Vector2(500f, 36f));
                    rowObject.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, y);
                    rowObject.AddComponent<Image>().color = (i % 2 == 0)
                        ? new Color(0.14f, 0.15f, 0.18f, 0.9f)
                        : new Color(0.11f, 0.12f, 0.15f, 0.9f);

                    Text rowLabel = CreateText($"RowLabel{i}", rowObject.transform, new Vector2(360f, 30f), 16, FontStyle.Normal);
                    rowLabel.alignment = TextAnchor.MiddleLeft;
                    rowLabel.rectTransform.anchorMin = new Vector2(0f, 0.5f);
                    rowLabel.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                    rowLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
                    rowLabel.rectTransform.anchoredPosition = new Vector2(12f, 0f);

                    Button takeButton = CreateButton($"Take{i}", rowObject.transform, new Vector2(92f, 28f), "Take");
                    RectTransform buttonRect = takeButton.GetComponent<RectTransform>();
                    buttonRect.anchorMin = new Vector2(1f, 0.5f);
                    buttonRect.anchorMax = new Vector2(1f, 0.5f);
                    buttonRect.pivot = new Vector2(1f, 0.5f);
                    buttonRect.anchoredPosition = new Vector2(-10f, 0f);

                    int capturedIndex = i;
                    takeButton.onClick.AddListener(() => onTakeSlot(capturedIndex));

                    rows.Add(new RowRefs
                    {
                        Root = rowObject,
                        Label = rowLabel,
                        Button = takeButton
                    });
                }

                panelObject.SetActive(false);
            }

            private void RenderRows(List<LootableItemMessage> items)
            {
                Dictionary<int, LootableItemMessage> bySlot = new Dictionary<int, LootableItemMessage>();
                for (int i = 0; i < items.Count; i++)
                {
                    bySlot[items[i].SlotIndex] = items[i];
                }

                for (int i = 0; i < rows.Count; i++)
                {
                    if (bySlot.TryGetValue(i, out LootableItemMessage item))
                    {
                        rows[i].Root.SetActive(true);
                        rows[i].Label.text = $"{item.DisplayName} x{item.Quantity}";
                        rows[i].Button.interactable = true;
                    }
                    else
                    {
                        rows[i].Root.SetActive(true);
                        rows[i].Label.text = "Empty";
                        rows[i].Button.interactable = false;
                    }
                }
            }

            private void ApplyOpenControlState()
            {
                if (PlayerSingleton<PlayerMovement>.InstanceExists)
                {
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
                }

                if (PlayerSingleton<PlayerCamera>.InstanceExists)
                {
                    PlayerSingleton<PlayerCamera>.Instance.AddActiveUIElement("S1DSPlayerRobbery");
                    PlayerSingleton<PlayerCamera>.Instance.FreeMouse();
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(false);
                    PlayerSingleton<PlayerCamera>.Instance.SetDoFActive(true, 0.05f);
                }

                if (PlayerSingleton<PlayerInventory>.InstanceExists)
                {
                    PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(false);
                }
            }

            private void RestoreClosedControlState()
            {
                if (PlayerSingleton<PlayerMovement>.InstanceExists)
                {
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
                }

                if (PlayerSingleton<PlayerCamera>.InstanceExists)
                {
                    PlayerSingleton<PlayerCamera>.Instance.RemoveActiveUIElement("S1DSPlayerRobbery");
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetDoFActive(false, 0.05f);
                }

                if (PlayerSingleton<PlayerInventory>.InstanceExists)
                {
                    bool keepDisabled = shouldKeepEquippingDisabled != null && shouldKeepEquippingDisabled();
                    PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(!keepDisabled);
                }
            }

            private Text CreateText(string name, Transform parent, Vector2 size, int fontSize, FontStyle fontStyle, string text = "")
            {
                GameObject obj = CreateUiObject(name, parent, size);
                Text uiText = obj.AddComponent<Text>();
                uiText.font = font;
                uiText.fontSize = fontSize;
                uiText.fontStyle = fontStyle;
                uiText.color = Color.white;
                uiText.alignment = TextAnchor.MiddleCenter;
                uiText.text = text;
                return uiText;
            }

            private Button CreateButton(string name, Transform parent, Vector2 size, string label)
            {
                GameObject obj = CreateUiObject(name, parent, size);
                Image image = obj.AddComponent<Image>();
                image.color = new Color(0.19f, 0.47f, 0.25f, 1f);

                Button button = obj.AddComponent<Button>();
                ColorBlock colors = button.colors;
                colors.normalColor = image.color;
                colors.highlightedColor = new Color(0.25f, 0.58f, 0.31f, 1f);
                colors.pressedColor = new Color(0.12f, 0.33f, 0.17f, 1f);
                colors.disabledColor = new Color(0.22f, 0.22f, 0.22f, 0.8f);
                button.colors = colors;

                Text buttonText = CreateText($"{name}Label", obj.transform, size, 15, FontStyle.Bold, label);
                buttonText.rectTransform.anchoredPosition = Vector2.zero;
                return button;
            }

            private static GameObject CreateUiObject(string name, Transform parent, Vector2 size)
            {
                GameObject obj = new GameObject(name, typeof(RectTransform));
                obj.transform.SetParent(parent, false);
                RectTransform rect = obj.GetComponent<RectTransform>();
                rect.sizeDelta = size;
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                return obj;
            }

            private sealed class RowRefs
            {
                public GameObject Root;
                public Text Label;
                public Button Button;
            }
        }
    }
}
#endif
