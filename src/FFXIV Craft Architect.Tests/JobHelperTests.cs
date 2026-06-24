using FFXIV_Craft_Architect.Core.Helpers;

namespace FFXIV_Craft_Architect.Tests;

public class JobHelperTests
{
    public static TheoryData<int, string> GarlandCrafterJobIds => new()
    {
        { 8, "Carpenter" },
        { 9, "Blacksmith" },
        { 10, "Armorer" },
        { 11, "Goldsmith" },
        { 12, "Leatherworker" },
        { 13, "Weaver" },
        { 14, "Alchemist" },
        { 15, "Culinarian" }
    };

    [Theory]
    [MemberData(nameof(GarlandCrafterJobIds))]
    public void GetJobName_MapsGarlandCrafterClassJobIds(int jobId, string expected)
    {
        Assert.Equal(expected, JobHelper.GetJobName(jobId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    public void GetJobName_ReturnsUnknownForNonGarlandCrafterJobIds(int jobId)
    {
        Assert.Equal("Unknown", JobHelper.GetJobName(jobId));
    }
}
