using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for GarlandModels functionality.
/// </summary>
public class GarlandModelsTests
{
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
    public void TryGetNpcObject_WithInvalidType_ReturnsFalseAndError()
    {
        var json = @"{
            ""type"": ""item"",
            ""id"": 123,
            ""obj"": {}
        }";

        var partial = JsonSerializer.Deserialize<GarlandPartial>(json);
        Assert.NotNull(partial);

        var success = partial.TryGetNpcObject(out var npc, out var error);
        
        Assert.False(success);
        Assert.Null(npc);
        Assert.NotNull(error);
        Assert.Contains("item", error);
    }

    [Fact]
    public void TryGetNpcObject_WithNoObject_ReturnsFalseAndError()
    {
        var json = @"{
            ""type"": ""npc"",
            ""id"": 123
        }";

        var partial = JsonSerializer.Deserialize<GarlandPartial>(json);
        Assert.NotNull(partial);

        var success = partial.TryGetNpcObject(out var npc, out var error);
        
        Assert.False(success);
        Assert.Null(npc);
        Assert.NotNull(error);
        Assert.Contains("No object data", error);
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
    public void GetNpcObject_WithValidNpc_ReturnsObject()
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

        var npc = partial.GetNpcObject();
        
        Assert.NotNull(npc);
        Assert.Equal(123, npc.Id);
        Assert.Equal("Test NPC", npc.Name);
    }

    [Fact]
    public void GetNpcObject_WithInvalidType_ReturnsNull()
    {
        var json = @"{
            ""type"": ""item"",
            ""id"": 123
        }";

        var partial = JsonSerializer.Deserialize<GarlandPartial>(json);
        Assert.NotNull(partial);

        var npc = partial.GetNpcObject();
        
        Assert.Null(npc);
    }

    [Fact]
    public void LocationIdToName_ReturnsCorrectNameForKnownZone()
    {
        // Test some known zones
        Assert.Equal("Mist", ZoneMappingHelper.LocationIdToName(425));
        Assert.Equal("Limsa Lominsa", ZoneMappingHelper.LocationIdToName(128));
        Assert.Equal("Old Sharlayan", ZoneMappingHelper.LocationIdToName(135));
        Assert.Equal("Central Shroud", ZoneMappingHelper.LocationIdToName(148));
    }

    [Fact]
    public void LocationIdToName_ReturnsZoneIdForUnknownZone()
    {
        Assert.Equal("Zone 99999", ZoneMappingHelper.LocationIdToName(99999));
        Assert.Equal("Zone -1", ZoneMappingHelper.LocationIdToName(-1));
        Assert.Equal("Zone 0", ZoneMappingHelper.LocationIdToName(0));
    }

    [Fact]
    public void LocationIdToName_HandlesDawntrailZones()
    {
        Assert.Equal("Tuliyollal", ZoneMappingHelper.LocationIdToName(136));
        Assert.Equal("Solution Nine", ZoneMappingHelper.LocationIdToName(2500));
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
