#if CLIENT
using System;
using System.Collections.Generic;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Levelling;
using ScheduleOne.Persistence;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.UI.Items;
using ScheduleOne.Vision;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace S1DSMod.PlayerRobbery
{
    public sealed partial class S1DSPlayerRobberyClientMod
    {
        private sealed class RobberyPickpocketScreenUi
        {
            private const string ActiveUiElementId = "S1DSPlayerRobbery";
            private const float FailCloseDelaySeconds = 0.9f;

            private readonly MelonLogger.Instance logger;
            private readonly Action<int> onTakeSlot;
            private readonly Action onClose;
            private readonly Func<bool> shouldKeepEquippingDisabled;
            private readonly List<ItemSlot> shadowSlots = new List<ItemSlot>();
            private readonly HashSet<int> unlockedSlotIndices = new HashSet<int>();
            private readonly HashSet<int> consumedGreenAreaSlotIndices = new HashSet<int>();

            private PickpocketScreen screen;
            private GraphicRaycaster raycaster;
            private bool isOpen;
            private bool isSliding;
            private bool isFail;
            private bool isAwaitingTakeResponse;
            private int slideDirection = 1;
            private float sliderPosition;
            private float slideTimeMultiplier = 1f;
            private float failCloseAtTime;

            public RobberyPickpocketScreenUi(MelonLogger.Instance loggerInstance, Action<int> takeSlot, Action close, Func<bool> keepEquippingDisabled)
            {
                logger = loggerInstance;
                onTakeSlot = takeSlot;
                onClose = close;
                shouldKeepEquippingDisabled = keepEquippingDisabled;
            }

            public bool IsOpen => isOpen;

            public string SessionId { get; private set; } = string.Empty;

            public string TargetId { get; private set; } = string.Empty;

            public void Open(RobberySessionMessage message)
            {
                if (message == null)
                {
                    return;
                }

                if (!EnsureScreenAvailable())
                {
                    logger.Warning("S1DS-PlayerRobbery: PickpocketScreen is unavailable in the current scene.");
                    return;
                }

                bool isNewSession =
                    !isOpen ||
                    !string.Equals(SessionId, message.SessionId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(TargetId, message.TargetId ?? string.Empty, StringComparison.Ordinal);

                SessionId = message.SessionId ?? string.Empty;
                TargetId = message.TargetId ?? string.Empty;

                if (isNewSession)
                {
                    unlockedSlotIndices.Clear();
                    consumedGreenAreaSlotIndices.Clear();
                    ResetSlidingState();
                }

                ApplyOpenControlState();
                RenderSlots(message.Items ?? new List<LootableItemMessage>());
                screen.Canvas.enabled = true;
                screen.Container.gameObject.SetActive(true);
                isOpen = true;
                isAwaitingTakeResponse = false;
                UpdatePrompt();
            }

            public void Close(string reason, bool restoreControlState)
            {
                if (!isOpen && string.IsNullOrWhiteSpace(SessionId) && string.IsNullOrWhiteSpace(TargetId))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    logger.Warning($"S1DS-PlayerRobbery: {reason}");
                }

                if (EnsureScreenAvailable())
                {
                    screen.Canvas.enabled = false;
                    screen.Container.gameObject.SetActive(false);
                    if (screen.ActionsContainer != null)
                    {
                        screen.ActionsContainer.gameObject.SetActive(false);
                    }

                    for (int i = 0; i < screen.Slots.Length; i++)
                    {
                        if (i < shadowSlots.Count && shadowSlots[i] != null)
                        {
                            shadowSlots[i].SetIsAddLocked(false);
                            shadowSlots[i].SetIsRemovalLocked(false);
                            if (shadowSlots[i].ItemInstance != null)
                            {
                                shadowSlots[i].ClearStoredInstance(true);
                            }
                        }

                        if (screen.GreenAreas != null && i < screen.GreenAreas.Length && screen.GreenAreas[i] != null)
                        {
                            screen.GreenAreas[i].gameObject.SetActive(false);
                        }
                    }
                }

                isOpen = false;
                isSliding = false;
                isFail = false;
                isAwaitingTakeResponse = false;
                SessionId = string.Empty;
                TargetId = string.Empty;
                unlockedSlotIndices.Clear();
                consumedGreenAreaSlotIndices.Clear();

                if (restoreControlState)
                {
                    RestoreClosedControlState();
                }
            }

            public void Tick()
            {
                if (!isOpen || !EnsureScreenAvailable())
                {
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    onClose();
                    return;
                }

                if (isFail)
                {
                    if (Time.unscaledTime >= failCloseAtTime)
                    {
                        onClose();
                    }

                    return;
                }

                if (TryTakeHoveredUnlockedSlot())
                {
                    return;
                }

                if (!isAwaitingTakeResponse && GameInput.GetButtonDown(GameInput.ButtonCode.Jump))
                {
                    if (isSliding)
                    {
                        StopArrow();
                    }
                    else
                    {
                        ResumeSliding();
                    }
                }

                if (isSliding)
                {
                    slideTimeMultiplier = Mathf.Clamp(slideTimeMultiplier + Time.deltaTime / 20f, 0f, screen.SlideTimeMaxMultiplier);
                    if (slideDirection == 1)
                    {
                        sliderPosition = Mathf.Clamp01(sliderPosition + Time.deltaTime / screen.SlideTime * slideTimeMultiplier);
                        if (sliderPosition >= 1f)
                        {
                            slideDirection = -1;
                        }
                    }
                    else
                    {
                        sliderPosition = Mathf.Clamp01(sliderPosition - Time.deltaTime / screen.SlideTime * slideTimeMultiplier);
                        if (sliderPosition <= 0f)
                        {
                            slideDirection = 1;
                        }
                    }
                }

                if (screen.Slider != null)
                {
                    screen.Slider.value = sliderPosition;
                }
            }

            public void SetStatus(string message)
            {
                isAwaitingTakeResponse = false;
                UpdatePrompt();
            }

            private bool EnsureScreenAvailable()
            {
                if (screen == null)
                {
                    screen = Singleton<PickpocketScreen>.Instance;
                }

                if (screen == null)
                {
                    raycaster = null;
                    return false;
                }

                if (raycaster == null && screen.Canvas != null)
                {
                    raycaster = screen.Canvas.GetComponent<GraphicRaycaster>();
                }

                EnsureShadowSlotsAttached();
                return true;
            }

            private void EnsureShadowSlotsAttached()
            {
                if (screen == null || screen.Slots == null)
                {
                    return;
                }

                while (shadowSlots.Count < screen.Slots.Length)
                {
                    shadowSlots.Add(new ItemSlot());
                }

                for (int i = 0; i < screen.Slots.Length; i++)
                {
                    if (screen.Slots[i] == null || screen.Slots[i].assignedSlot == shadowSlots[i])
                    {
                        continue;
                    }

                    screen.Slots[i].ClearSlot();
                    screen.Slots[i].AssignSlot(shadowSlots[i]);
                    screen.Slots[i].SetLockVisible(false);
                }
            }

            private void RenderSlots(List<LootableItemMessage> items)
            {
                Dictionary<int, LootableItemMessage> itemsBySlot = new Dictionary<int, LootableItemMessage>();
                for (int i = 0; i < items.Count; i++)
                {
                    LootableItemMessage item = items[i];
                    if (item != null && item.SlotIndex >= 0 && item.SlotIndex < shadowSlots.Count)
                    {
                        itemsBySlot[item.SlotIndex] = item;
                    }
                }

                for (int i = 0; i < shadowSlots.Count; i++)
                {
                    ItemSlot slot = shadowSlots[i];
                    slot.SetIsAddLocked(false);
                    slot.SetIsRemovalLocked(false);
                    if (slot.ItemInstance != null)
                    {
                        slot.ClearStoredInstance(true);
                    }

                    if (!itemsBySlot.TryGetValue(i, out LootableItemMessage itemMessage))
                    {
                        unlockedSlotIndices.Remove(i);
                        consumedGreenAreaSlotIndices.Remove(i);
                        screen.SetSlotLocked(i, false);
                        SetGreenAreaVisible(i, false);
                        continue;
                    }

                    ItemInstance item = TryDeserializeItem(itemMessage);
                    if (item == null)
                    {
                        unlockedSlotIndices.Remove(i);
                        consumedGreenAreaSlotIndices.Remove(i);
                        screen.SetSlotLocked(i, false);
                        SetGreenAreaVisible(i, false);
                        continue;
                    }

                    slot.SetStoredItem(item, true);
                    bool isUnlocked = unlockedSlotIndices.Contains(i);
                    screen.SetSlotLocked(i, !isUnlocked);
                    ConfigureGreenArea(i, item, !consumedGreenAreaSlotIndices.Contains(i));
                }

                if (screen.ActionsContainer != null)
                {
                    screen.ActionsContainer.gameObject.SetActive(GetLockedSlotCount() > 0);
                }
            }

            private ItemInstance TryDeserializeItem(LootableItemMessage message)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.ItemJson))
                {
                    return null;
                }

                try
                {
                    return ItemDeserializer.LoadItem(message.ItemJson);
                }
                catch (Exception ex)
                {
                    logger.Warning($"S1DS-PlayerRobbery: failed to deserialize robbery item in slot {message.SlotIndex}: {ex.Message}");
                    return null;
                }
            }

            private void ConfigureGreenArea(int slotIndex, ItemInstance item, bool visible)
            {
                if (screen.GreenAreas == null || slotIndex < 0 || slotIndex >= screen.GreenAreas.Length || screen.GreenAreas[slotIndex] == null)
                {
                    return;
                }

                RectTransform greenArea = screen.GreenAreas[slotIndex];
                if (item == null || !visible)
                {
                    greenArea.gameObject.SetActive(false);
                    return;
                }

                float difficultyMultiplier = 1f;
                if (item.Definition is StorableItemDefinition storableDefinition)
                {
                    difficultyMultiplier = Mathf.Max(0.01f, storableDefinition.PickpocketDifficultyMultiplier);
                }

                float value = item.GetMonetaryValue() * difficultyMultiplier;
                float width = Mathf.Lerp(
                    screen.GreenAreaMaxWidth,
                    screen.GreenAreaMinWidth,
                    Mathf.Pow(Mathf.Clamp01(value / screen.ValueDivisor), 0.3f));

                greenArea.sizeDelta = new Vector2(width, greenArea.sizeDelta.y);
                greenArea.anchoredPosition = new Vector2(37.5f + 90f * slotIndex, greenArea.anchoredPosition.y);
                greenArea.gameObject.SetActive(true);
            }

            private void SetGreenAreaVisible(int slotIndex, bool visible)
            {
                if (screen.GreenAreas == null || slotIndex < 0 || slotIndex >= screen.GreenAreas.Length || screen.GreenAreas[slotIndex] == null)
                {
                    return;
                }

                screen.GreenAreas[slotIndex].gameObject.SetActive(visible);
            }

            private void StopArrow()
            {
                screen.onStop?.Invoke();
                isSliding = false;

                int hoveredIndex = GetHoveredGreenAreaIndex();
                if (hoveredIndex >= 0 && hoveredIndex < shadowSlots.Count && shadowSlots[hoveredIndex].ItemInstance != null)
                {
                    unlockedSlotIndices.Add(hoveredIndex);
                    screen.SetSlotLocked(hoveredIndex, false);
                    if (NetworkSingleton<LevelManager>.InstanceExists)
                    {
                        NetworkSingleton<LevelManager>.Instance.AddXP(2);
                    }

                    screen.onHitGreen?.Invoke();
                    UpdatePrompt();
                    return;
                }

                Fail();
            }

            private void ResumeSliding()
            {
                int hoveredIndex = GetHoveredGreenAreaIndex();
                if (hoveredIndex >= 0 && hoveredIndex < shadowSlots.Count && shadowSlots[hoveredIndex].ItemInstance != null)
                {
                    consumedGreenAreaSlotIndices.Add(hoveredIndex);
                    SetGreenAreaVisible(hoveredIndex, false);
                }

                isSliding = true;
                UpdatePrompt();
            }

            private void Fail()
            {
                isFail = true;
                isSliding = false;
                isAwaitingTakeResponse = false;
                failCloseAtTime = Time.unscaledTime + FailCloseDelaySeconds;
                screen.onFail?.Invoke();
                UpdatePrompt();
            }

            private bool TryTakeHoveredUnlockedSlot()
            {
                if (isSliding || isAwaitingTakeResponse || !GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick))
                {
                    return false;
                }

                ItemSlotUI hoveredSlot = GetHoveredItemSlot();
                if (hoveredSlot == null)
                {
                    return false;
                }

                int slotIndex = Array.IndexOf(screen.Slots, hoveredSlot);
                if (slotIndex < 0 || slotIndex >= shadowSlots.Count)
                {
                    return false;
                }

                ItemSlot assignedSlot = shadowSlots[slotIndex];
                if (assignedSlot == null || assignedSlot.ItemInstance == null || assignedSlot.IsRemovalLocked)
                {
                    return false;
                }

                isAwaitingTakeResponse = true;
                UpdatePrompt();
                onTakeSlot(slotIndex);
                return true;
            }

            private ItemSlotUI GetHoveredItemSlot()
            {
                if (raycaster == null || EventSystem.current == null)
                {
                    return null;
                }

                PointerEventData pointerEventData = new PointerEventData(EventSystem.current)
                {
                    position = GameInput.MousePosition
                };

                List<RaycastResult> results = new List<RaycastResult>();
                raycaster.Raycast(pointerEventData, results);
                for (int i = 0; i < results.Count; i++)
                {
                    ItemSlotUI slot = results[i].gameObject.GetComponentInParent<ItemSlotUI>();
                    if (slot != null)
                    {
                        return slot;
                    }
                }

                return null;
            }

            private int GetHoveredGreenAreaIndex()
            {
                if (screen == null || screen.GreenAreas == null || screen.SliderContainer == null)
                {
                    return -1;
                }

                for (int i = 0; i < screen.GreenAreas.Length; i++)
                {
                    RectTransform greenArea = screen.GreenAreas[i];
                    if (greenArea == null || !greenArea.gameObject.activeSelf)
                    {
                        continue;
                    }

                    float min = GetGreenAreaNormalizedPosition(i) - GetGreenAreaNormalizedWidth(i) / 2f;
                    float max = GetGreenAreaNormalizedPosition(i) + GetGreenAreaNormalizedWidth(i) / 2f;
                    if (sliderPosition >= min - screen.Tolerance && sliderPosition <= max + screen.Tolerance)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private float GetGreenAreaNormalizedPosition(int index)
            {
                return screen.GreenAreas[index].anchoredPosition.x / screen.SliderContainer.sizeDelta.x;
            }

            private float GetGreenAreaNormalizedWidth(int index)
            {
                return screen.GreenAreas[index].sizeDelta.x / screen.SliderContainer.sizeDelta.x;
            }

            private int GetLockedSlotCount()
            {
                int count = 0;
                for (int i = 0; i < shadowSlots.Count; i++)
                {
                    if (shadowSlots[i] != null && shadowSlots[i].ItemInstance != null && shadowSlots[i].IsRemovalLocked)
                    {
                        count++;
                    }
                }

                return count;
            }

            private bool HasUnlockedSlot()
            {
                for (int i = 0; i < shadowSlots.Count; i++)
                {
                    if (shadowSlots[i] != null && shadowSlots[i].ItemInstance != null && !shadowSlots[i].IsRemovalLocked)
                    {
                        return true;
                    }
                }

                return false;
            }

            private void ResetSlidingState()
            {
                isSliding = true;
                isFail = false;
                isAwaitingTakeResponse = false;
                slideDirection = 1;
                sliderPosition = 0f;
                slideTimeMultiplier = 1f;
                failCloseAtTime = 0f;
                if (screen != null && screen.Slider != null)
                {
                    screen.Slider.value = sliderPosition;
                }
            }

            private void UpdatePrompt()
            {
                if (screen?.InputPrompt == null)
                {
                    return;
                }

                if (isFail)
                {
                    screen.InputPrompt.SetLabel("Failed");
                    return;
                }

                if (isAwaitingTakeResponse)
                {
                    screen.InputPrompt.SetLabel("Taking...");
                    return;
                }

                if (GetLockedSlotCount() == 0)
                {
                    screen.InputPrompt.SetLabel(HasUnlockedSlot() ? "Click Item" : "Close");
                    return;
                }

                if (isSliding)
                {
                    screen.InputPrompt.SetLabel("Stop Arrow");
                    return;
                }

                screen.InputPrompt.SetLabel(HasUnlockedSlot() ? "Continue / Click Item" : "Continue");
            }

            private void ApplyOpenControlState()
            {
                Singleton<GameInput>.Instance.ExitAll();

                if (PlayerSingleton<PlayerMovement>.InstanceExists)
                {
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
                }

                if (PlayerSingleton<PlayerCamera>.InstanceExists)
                {
                    PlayerSingleton<PlayerCamera>.Instance.AddActiveUIElement(ActiveUiElementId);
                    PlayerSingleton<PlayerCamera>.Instance.FreeMouse();
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(false);
                    PlayerSingleton<PlayerCamera>.Instance.SetDoFActive(true, 0f);
                }

                if (PlayerSingleton<PlayerInventory>.InstanceExists)
                {
                    PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(false);
                }

                if (Singleton<ItemUIManager>.InstanceExists)
                {
                    Singleton<ItemUIManager>.Instance.SetDraggingEnabled(false);
                }

                if (Singleton<HUD>.InstanceExists)
                {
                    Singleton<HUD>.Instance.SetCrosshairVisible(false);
                }

                if (Player.Local != null && Player.Local.VisualState != null)
                {
                    Player.Local.VisualState.ApplyState("pickpocketing", EVisualState.Pickpocketing);
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
                    PlayerSingleton<PlayerCamera>.Instance.RemoveActiveUIElement(ActiveUiElementId);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetDoFActive(false, 0f);
                }

                if (Player.Local != null && Player.Local.VisualState != null)
                {
                    Player.Local.VisualState.RemoveState("pickpocketing");
                }

                if (PlayerSingleton<PlayerInventory>.InstanceExists)
                {
                    bool keepDisabled = shouldKeepEquippingDisabled != null && shouldKeepEquippingDisabled();
                    PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(!keepDisabled);
                }

                if (Singleton<ItemUIManager>.InstanceExists)
                {
                    Singleton<ItemUIManager>.Instance.SetDraggingEnabled(false);
                }

                if (Singleton<HUD>.InstanceExists)
                {
                    Singleton<HUD>.Instance.SetCrosshairVisible(true);
                }
            }
        }
    }
}
#endif
