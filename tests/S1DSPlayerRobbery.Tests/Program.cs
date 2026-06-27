using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1DSMod.PlayerRobbery;

var tests = new (string Name, Action Run)[]
{
    ("message command ids are unique", MessageCommandIdsAreUnique),
    ("metadata is release ready", MetadataIsReleaseReady),
    ("toggle hands up request serializes expected contract", ToggleHandsUpRequestSerializesExpectedContract),
    ("robbery session message round trips loot list", RobberySessionMessageRoundTripsLootList),
    ("robbery close message preserves reason", RobberyCloseMessagePreservesReason),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.Exit(1);
}

static void MessageCommandIdsAreUnique()
{
    string[] values =
    {
        Cmds.SetHandsUp,
        Cmds.HandsUpState,
        Cmds.OpenSession,
        Cmds.SessionState,
        Cmds.CloseSession,
        Cmds.TakeSlot,
        Cmds.ApplyAdd,
        Cmds.ApplyRemove,
        Cmds.Error,
    };

    Assert(values.All(value => value.StartsWith("robbery_", StringComparison.Ordinal)), "all command ids should use robbery_ prefix");
    Assert(values.Distinct(StringComparer.Ordinal).Count() == values.Length, "command ids must be unique");
}

static void MetadataIsReleaseReady()
{
    Assert(AddonMetadata.ModName == "S1DS-PlayerRobbery", "unexpected mod name");
    Assert(AddonMetadata.DisplayName == "S1DS Player Robbery", "unexpected display name");
    Assert(AddonMetadata.ModId == "bars.s1ds-player-robbery", "unexpected mod id");
    Assert(Version.TryParse(AddonMetadata.Version, out Version? version), "version must parse as semantic version");
    Assert(version >= new Version(1, 0, 0), "version must be at least 1.0.0 for public release");
    Assert(!string.IsNullOrWhiteSpace(AddonMetadata.Author), "author is required");
}

static void ToggleHandsUpRequestSerializesExpectedContract()
{
    var request = new ToggleHandsUpRequest { IsHandsUp = true };
    JObject json = JObject.Parse(JsonConvert.SerializeObject(request));
    Assert(json.Value<bool>("isHandsUp"), "isHandsUp should serialize true");
    Assert(json.Properties().Count() == 1, "toggle request should only expose isHandsUp");
}

static void RobberySessionMessageRoundTripsLootList()
{
    var message = new RobberySessionMessage
    {
        SessionId = "session-1",
        TargetId = "target-1",
        TargetName = "Target",
        Items = new List<LootableItemMessage>
        {
            new LootableItemMessage
            {
                SlotIndex = 2,
                ItemJson = "{\"id\":\"weed\"}",
                DisplayName = "OG Kush",
                Quantity = 4,
            },
        },
    };

    RobberySessionMessage? roundTrip = JsonConvert.DeserializeObject<RobberySessionMessage>(JsonConvert.SerializeObject(message));
    roundTrip = AssertNotNull(roundTrip, "session message should deserialize");
    Assert(roundTrip.SessionId == message.SessionId, "session id should round trip");
    Assert(roundTrip.TargetId == message.TargetId, "target id should round trip");
    Assert(roundTrip.TargetName == message.TargetName, "target name should round trip");
    Assert(roundTrip.Items.Count == 1, "loot item count should round trip");
    Assert(roundTrip.Items[0].SlotIndex == 2, "slot index should round trip");
    Assert(roundTrip.Items[0].Quantity == 4, "quantity should round trip");
}

static void RobberyCloseMessagePreservesReason()
{
    var message = new RobberyCloseMessage
    {
        SessionId = "session-1",
        TargetId = "target-1",
        Reason = "Target lowered their hands.",
    };

    RobberyCloseMessage? roundTrip = JsonConvert.DeserializeObject<RobberyCloseMessage>(JsonConvert.SerializeObject(message));
    roundTrip = AssertNotNull(roundTrip, "close message should deserialize");
    Assert(roundTrip.SessionId == message.SessionId, "session id should round trip");
    Assert(roundTrip.TargetId == message.TargetId, "target id should round trip");
    Assert(roundTrip.Reason == message.Reason, "reason should round trip");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static T AssertNotNull<T>(T? value, string message)
    where T : class
{
    if (value == null)
    {
        throw new InvalidOperationException(message);
    }

    return value;
}
