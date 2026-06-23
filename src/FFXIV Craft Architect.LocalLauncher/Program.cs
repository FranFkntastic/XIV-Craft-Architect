using System.Diagnostics;

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var scriptPath = Path.Combine(repoRoot, "run-trade-local.ps1");
if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"Could not find launcher script at {scriptPath}");
    return 1;
}

var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
var forwardedArgs = string.Join(" ", args.Select(QuoteArgument));
var scriptArguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)}";
if (!string.IsNullOrWhiteSpace(forwardedArgs))
{
    scriptArguments += $" {forwardedArgs}";
}

var startInfo = new ProcessStartInfo(shell, scriptArguments)
{
    UseShellExecute = false
};

using var process = Process.Start(startInfo);
if (process == null)
{
    Console.Error.WriteLine("Failed to start the Trade local launcher script.");
    return 1;
}

process.WaitForExit();
return process.ExitCode;

static string FindRepoRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")) &&
            File.Exists(Path.Combine(directory.FullName, "run-trade-local.ps1")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string QuoteArgument(string argument)
{
    return argument.Contains(' ') || argument.Contains('"')
        ? $"\"{argument.Replace("\"", "\\\"")}\""
        : argument;
}
