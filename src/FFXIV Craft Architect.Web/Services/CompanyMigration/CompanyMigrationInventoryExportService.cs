using System.Text.Json;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.CompanyMigration;

public sealed class CompanyMigrationInventoryExportService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IJSRuntime _jsRuntime;

    public CompanyMigrationInventoryExportService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<CompanyMigrationExportBundle> CreateInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        var source = await _jsRuntime.InvokeAsync<BrowserCompanyMigrationSource>(
            "IndexedDB.getCompanyMigrationSourceInventory",
            cancellationToken);
        return CompanyMigrationInventoryBuilder.Build(source, DateTime.UtcNow);
    }

    public async Task<string> ExportBundleJsonAsync(
        CancellationToken cancellationToken = default)
    {
        var bundle = await CreateInventoryAsync(cancellationToken);
        return JsonSerializer.Serialize(bundle, ExportJsonOptions);
    }
}
