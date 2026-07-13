using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for GarlandModels functionality.
/// </summary>
public class GarlandModelsTests
{
    [Fact]
    public void GarlandItemResponse_IconPathString_DeserializesWithZeroIconId()
    {
        var json = @"{
            ""item"": {
                ""id"": 52288,
                ""name"": ""Shovel"",
                ""icon"": ""t/58056""
            }
        }";

        var response = JsonSerializer.Deserialize<GarlandItemResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(52288, response.Item.Id);
        Assert.Equal("Shovel", response.Item.Name);
        Assert.Equal(0, response.Item.IconId);
    }

    [Fact]
    public void GarlandItemResponse_CraftDraftUnlockId_DeserializesWithZeroUnlockItemId()
    {
        var json = @"{
            ""item"": {
                ""id"": 24361,
                ""name"": ""Modified Coelacanth-class Bridge"",
                ""craft"": [
                    {
                        ""id"": ""fc563"",
                        ""job"": 0,
                        ""rlvl"": 1,
                        ""lvl"": 1,
                        ""unlockId"": ""draft30"",
                        ""ingredients"": [
                            { ""id"": 26521, ""amount"": 1, ""phase"": 1 }
                        ]
                    }
                ]
            }
        }";

        var response = JsonSerializer.Deserialize<GarlandItemResponse>(json);

        Assert.NotNull(response);
        var craft = Assert.Single(response.Item.Crafts!);
        Assert.Equal("fc563", craft.Id);
        Assert.Equal(0, craft.UnlockItemId);
        Assert.Equal(26521, Assert.Single(craft.Ingredients).Id);
    }

    [Fact]
    public void GarlandPartial_Id_FromNumber_IsParsed()
    {
        var partial = JsonSerializer.Deserialize<GarlandPartial>("{\"type\":\"npc\",\"id\":123}");
        Assert.NotNull(partial);
        Assert.Equal(123, partial.Id);
    }

    [Fact]
    public void GarlandPartial_Id_FromStringNumber_IsParsed()
    {
        var partial = JsonSerializer.Deserialize<GarlandPartial>("{\"type\":\"npc\",\"id\":\"456\"}");
        Assert.NotNull(partial);
        Assert.Equal(456, partial.Id);
    }

    [Fact]
    public void GarlandPartial_Id_FromInvalidString_ReturnsZero()
    {
        var partial = JsonSerializer.Deserialize<GarlandPartial>("{\"type\":\"npc\",\"id\":\"invalid\"}");
        Assert.NotNull(partial);
        Assert.Equal(0, partial.Id);
    }

    [Fact]
    public void TryGetNpcObject_WithValidNpc_ReturnsTrueAndObject()
    {
        var json = @"{
            ""type"": ""npc"",
            ""id"": 123,
            ""obj"": {
                ""i"": 123,
                ""n"": ""Test NPC"",
                ""l"": 456,
                ""c"": [100, 200]
            }
        }";

        var partial = JsonSerializer.Deserialize<GarlandPartial>(json);
        Assert.NotNull(partial);

        var success = partial.TryGetNpcObject(out var npc, out var error);

        Assert.True(success);
        Assert.NotNull(npc);
        Assert.Null(error);
        Assert.Equal(123, npc.Id);
        Assert.Equal("Test NPC", npc.Name);
        Assert.Equal(456, npc.LocationId);
        Assert.NotNull(npc.Coordinates);
        Assert.Equal(100, npc.Coordinates[0]);
        Assert.Equal(200, npc.Coordinates[1]);
    }





    [Fact]
    public void TryGetNpcObject_WithInvalidJson_ReturnsFalseAndError()
    {
        var json = @"{
            ""type"": ""npc"",
            ""id"": 123,
            ""obj"": ""invalid json""
        }";

        var partial = JsonSerializer.Deserialize<GarlandPartial>(json);
        Assert.NotNull(partial);
        Assert.True(partial.ObjectRaw.HasValue);

        var success = partial.TryGetNpcObject(out var npc, out var error);

        Assert.False(success);
        Assert.Null(npc);
        Assert.NotNull(error);
        Assert.Contains("Failed to deserialize", error);
    }











    [Fact]
    public void ZoneNameMappings_NoDuplicateKeys()
    {
        // This test verifies that the zone dictionary was properly constructed
        // If there were duplicates, the static constructor would throw
        var zone54 = ZoneMappingHelper.LocationIdToName(54);
        var zone148 = ZoneMappingHelper.LocationIdToName(148);

        // 54 should be New Gridania
        Assert.Equal("New Gridania", zone54);
        // 148 should be Central Shroud (the fix we made)
        Assert.Equal("Central Shroud", zone148);

        // They should not be the same
        Assert.NotEqual(zone54, zone148);
    }

}
