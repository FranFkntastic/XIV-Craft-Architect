using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class UniversalisMarketDataMapperTests
{
    [Fact]
    public void Deserialize_ResponseTimestamps_ReadsUniversalisUploadAndListingReviewTimes()
    {
        const string json = """
            {
              "itemID": 3920,
              "dcName": "Aether",
              "lastUploadTime": 1710000000123,
              "worldUploadTimes": {
                "40": 1710000000000
              },
              "listings": [
                {
                  "worldName": "Siren",
                  "pricePerUnit": 100,
                  "quantity": 12,
                  "retainerName": "Seller",
                  "hq": false,
                  "lastReviewTime": 1709999900
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<UniversalisResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(1710000000123, response.LastUploadTimeUnixMilliseconds);
        Assert.Equal(1710000000000, response.WorldUploadTimes[40]);
        Assert.Equal(1709999900, Assert.Single(response.Listings).LastReviewTimeUnix);
    }

    [Fact]
    public void Deserialize_BulkResponseTimestamps_PreservesNestedItemUploadTimes()
    {
        const string json = """
            {
              "itemIDs": [3920, 5100],
              "items": {
                "3920": {
                  "itemID": 3920,
                  "lastUploadTime": 1710000000123,
                  "worldUploadTimes": { "40": 1710000000000 },
                  "listings": []
                },
                "5100": {
                  "itemID": 5100,
                  "lastUploadTime": 1710000100123,
                  "worldUploadTimes": { "73": 1710000100000 },
                  "listings": []
                }
              }
            }
            """;

        var response = JsonSerializer.Deserialize<UniversalisBulkResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(1710000000123, response.Items[3920].LastUploadTimeUnixMilliseconds);
        Assert.Equal(1710000000000, response.Items[3920].WorldUploadTimes[40]);
        Assert.Equal(1710000100123, response.Items[5100].LastUploadTimeUnixMilliseconds);
        Assert.Equal(1710000100000, response.Items[5100].WorldUploadTimes[73]);
    }

    [Fact]
    public void ToCachedMarketData_PerWorldUploadTime_MapsWorldIdAndListingReviewTime()
    {
        var fetchedAtUtc = DateTimeOffset.FromUnixTimeSeconds(1710000600).UtcDateTime;
        var response = new UniversalisResponse
        {
            ItemId = 3920,
            LastUploadTimeUnixMilliseconds = 1710000000123,
            WorldUploadTimes = new Dictionary<int, long>
            {
                [40] = 1709999700000
            },
            Listings =
            [
                new MarketListing
                {
                    WorldName = "Siren",
                    PricePerUnit = 100,
                    Quantity = 12,
                    RetainerName = "Seller",
                    LastReviewTimeUnix = 1709999900
                }
            ]
        };
        var worldData = new WorldData
        {
            WorldIdToName = new Dictionary<int, string>
            {
                [40] = "Siren"
            }
        };

        var cached = UniversalisMarketDataMapper.ToCachedMarketData(3920, "Aether", response, worldData, fetchedAtUtc);

        Assert.Equal(1710000000123, cached.LastUploadTimeUnixMilliseconds);
        Assert.Equal(fetchedAtUtc, cached.FetchedAt);
        var world = Assert.Single(cached.Worlds);
        Assert.Equal(40, world.WorldId);
        Assert.Equal(1709999700000, world.LastUploadTimeUnixMilliseconds);
        Assert.Equal(1709999900, Assert.Single(world.Listings).LastReviewTimeUnix);
    }

    [Fact]
    public void ToCachedMarketData_UnmappedWorldUploadTime_PreservesResponseFallbackOnly()
    {
        var response = new UniversalisResponse
        {
            ItemId = 3920,
            LastUploadTimeUnixMilliseconds = 1710000000123,
            WorldUploadTimes = new Dictionary<int, long>
            {
                [999] = 1709999700000
            },
            Listings =
            [
                new MarketListing
                {
                    WorldName = "Siren",
                    PricePerUnit = 100,
                    Quantity = 12,
                    RetainerName = "Seller"
                }
            ]
        };

        var cached = UniversalisMarketDataMapper.ToCachedMarketData(3920, "Aether", response, worldData: null, DateTime.UnixEpoch);

        Assert.Equal(1710000000123, cached.LastUploadTimeUnixMilliseconds);
        var world = Assert.Single(cached.Worlds);
        Assert.Null(world.WorldId);
        Assert.Null(world.LastUploadTimeUnixMilliseconds);
    }

    [Fact]
    public void ToCachedMarketData_WorldUploadTimeWithoutListings_CreatesZeroStockWorldRow()
    {
        var response = new UniversalisResponse
        {
            ItemId = 3920,
            WorldUploadTimes = new Dictionary<int, long>
            {
                [40] = 1709999700000,
                [73] = 1709999800000
            },
            Listings =
            [
                new MarketListing
                {
                    WorldName = "Siren",
                    PricePerUnit = 100,
                    Quantity = 12,
                    RetainerName = "Seller"
                }
            ]
        };
        var worldData = new WorldData
        {
            WorldIdToName = new Dictionary<int, string>
            {
                [40] = "Siren",
                [73] = "Midgardsormr"
            }
        };

        var cached = UniversalisMarketDataMapper.ToCachedMarketData(3920, "Aether", response, worldData, DateTime.UnixEpoch);

        var emptyWorld = cached.Worlds.Single(world => world.WorldName == "Midgardsormr");
        Assert.Equal(73, emptyWorld.WorldId);
        Assert.Equal(1709999800000, emptyWorld.LastUploadTimeUnixMilliseconds);
        Assert.Empty(emptyWorld.Listings);
    }

    [Fact]
    public void ToCachedMarketData_InvalidTimestamps_AreTreatedAsMissing()
    {
        var response = new UniversalisResponse
        {
            ItemId = 3920,
            LastUploadTimeUnixMilliseconds = -1,
            WorldUploadTimes = new Dictionary<int, long>
            {
                [40] = 0
            },
            Listings =
            [
                new MarketListing
                {
                    WorldName = "Siren",
                    PricePerUnit = 100,
                    Quantity = 12,
                    RetainerName = "Seller",
                    LastReviewTimeUnix = -1
                }
            ]
        };
        var worldData = new WorldData
        {
            WorldIdToName = new Dictionary<int, string>
            {
                [40] = "Siren"
            }
        };

        var cached = UniversalisMarketDataMapper.ToCachedMarketData(3920, "Aether", response, worldData, DateTime.UnixEpoch);

        Assert.Null(cached.LastUploadTimeUnixMilliseconds);
        Assert.Null(Assert.Single(cached.Worlds).LastUploadTimeUnixMilliseconds);
        Assert.Null(Assert.Single(Assert.Single(cached.Worlds).Listings).LastReviewTimeUnix);
    }

    [Fact]
    public void Deserialize_OldCachedWorldAndListingJson_LeavesNewTimestampFieldsNull()
    {
        const string json = """
            {
              "worldName": "Siren",
              "listings": [
                {
                  "quantity": 12,
                  "pricePerUnit": 100,
                  "retainerName": "Seller",
                  "isHq": false
                }
              ]
            }
            """;

        var world = JsonSerializer.Deserialize<CachedWorldData>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(world);
        Assert.Null(world.WorldId);
        Assert.Null(world.LastUploadTimeUnixMilliseconds);
        Assert.Null(Assert.Single(world.Listings).LastReviewTimeUnix);
    }

    [Fact]
    public void Deserialize_OldCachedMarketDataJson_LeavesResponseUploadTimeNull()
    {
        const string json = """
            {
              "itemId": 3920,
              "dataCenter": "Aether",
              "fetchedAtUnix": 1710000000,
              "dcAveragePrice": 100,
              "worlds": []
            }
            """;

        var cached = JsonSerializer.Deserialize<CachedMarketData>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(cached);
        Assert.Null(cached.LastUploadTimeUnixMilliseconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000).UtcDateTime, cached.FetchedAt);
    }
}
