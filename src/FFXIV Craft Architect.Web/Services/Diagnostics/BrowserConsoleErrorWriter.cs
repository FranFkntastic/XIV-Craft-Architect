using System.Text;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.Diagnostics;

/// <summary>
/// Sends <see cref="Console.Error"/> diagnostics to the browser's <c>console.error</c>.
/// </summary>
/// <remarks>
/// Blazor WebAssembly treats every write to the runtime's stderr as fatal and shows the
/// "An unhandled error has occurred" bar. The app's <c>Console.Error</c> lines are recoverable
/// diagnostics (for example, profile sync skipping a retired hosted setting), so they must keep
/// their console severity without claiming the app crashed. Genuine unhandled exceptions still
/// reach the bar through the renderer's critical logging path.
/// </remarks>
public sealed class BrowserConsoleErrorWriter(IJSInProcessRuntime jsRuntime) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public static void Install(IServiceProvider services)
    {
        if (services.GetService<IJSRuntime>() is IJSInProcessRuntime jsRuntime)
        {
            Console.SetError(new BrowserConsoleErrorWriter(jsRuntime));
        }
    }

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Emit();
        }
        else if (value != '\r')
        {
            _line.Append(value);
        }
    }

    // Keep a multi-line message, such as an exception with its stack trace, as one console entry.
    public override void WriteLine(string? value)
    {
        _line.Append(value);
        Emit();
    }

    public override void Flush() => Emit();

    private void Emit()
    {
        if (_line.Length == 0)
        {
            return;
        }

        var message = _line.ToString();
        _line.Clear();
        try
        {
            jsRuntime.InvokeVoid("console.error", message);
        }
        catch
        {
            // A diagnostic write must never fail the operation that reported it.
        }
    }
}
