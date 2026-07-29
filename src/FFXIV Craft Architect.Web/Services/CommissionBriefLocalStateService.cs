namespace FFXIV_Craft_Architect.Web.Services;

public sealed class CommissionBriefLocalStateService
{
    public const string SettingPrefix = "commissionBrief.editor.";
    private readonly IndexedDbService _indexedDb;

    public CommissionBriefLocalStateService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public Task<bool> SaveEditorTokenAsync(Guid orderId, string token) =>
        _indexedDb.SaveSettingAsync($"{SettingPrefix}{orderId:D}", token);

    public Task<string?> LoadEditorTokenAsync(Guid orderId) =>
        _indexedDb.LoadSettingAsync<string>($"{SettingPrefix}{orderId:D}");

    public Task<bool> ForgetEditorTokenAsync(Guid orderId) =>
        _indexedDb.SaveSettingAsync($"{SettingPrefix}{orderId:D}", string.Empty);
}
