namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketMafiosoIntegrationState
{
    public bool Enabled { get; private set; }

    public event Action? Changed;

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        Changed?.Invoke();
    }
}
