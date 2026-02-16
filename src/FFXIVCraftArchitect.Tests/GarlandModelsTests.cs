using System.Text.Json;
using FFXIVCraftArchitect.Core.Helpers;
using FFXIVCraftArchitect.Core.Models;
using Xunit;

namespace FFXIVCraftArchitect.Tests;

/// <summary>
/// Unit tests for GarlandModels functionality.
/// </summary>
public class GarlandModelsTests
{
    [Theory]
    [InlineData(42, 42)]
    [InlineData(42L, 42)]
    [InlineData(42.5, 42)]
    [InlineData("42", 42)]
    [InlineData("not a number", 0)]
    [InlineData(null, 0)]
    public void ConvertToInt_HandlesVariousTypes(object? input, int expected)
    {
        // Test ConvertToInt with various input types
        // Note: JsonElement values are tested separately since they require JSON parsing
        
        if (input is double d)
        {
            // Test double directly (not via JsonElement)
            var result = InvokeConvertToInt(d);
            Assert.Equal(expected, result);
        }
        else if (input is string s)
        {
            var element = JsonDocument.Parse($"\"{s}\"").RootElement;
            var result = InvokeConvertToInt(element);
            Assert.Equal(expected, result);
        }
        else if (input is long l)
        {
            var element = JsonDocument.Parse(l.ToString()).RootElement;
            var result = InvokeConvertToInt(element);
            Assert.Equal(expected, result);
        }
        else if (input is int i)
        {
            var element = JsonDocument.Parse(i.ToString()).RootElement;
            var result = InvokeConvertToInt(element);
            Assert.Equal(expected, result);
        }
        else
        {
            var result = InvokeConvertToInt(input);
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void ConvertToInt_HandlesJsonElementNumber()
    {
        var element = JsonDocument.Parse("123").RootElement;
        var result = InvokeConvertToInt(element);
        Assert.Equal(123, result);
    }

    [Fact]
    public void ConvertToInt_HandlesJsonElementString()
    {
        var element = JsonDocument.Parse("\"456\"").RootElement;
        var result = InvokeConvertToInt(element);
        Assert.Equal(456, result);
    }

    [Fact]
    public void ConvertToInt_HandlesJsonElementInvalidString()
    {
        var element = JsonDocument.Parse("\"invalid\"").RootElement;
        var result = InvokeConvertToInt(element);
        Assert.Equal(0, result);
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

    // Helper method to invoke private ConvertToInt via reflection
    private static int InvokeConvertToInt(object? value)
    {
        var method = typeof(GarlandPartial).GetMethod("ConvertToInt", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (int)method.Invoke(null, new[] { value })!;
    }
}
