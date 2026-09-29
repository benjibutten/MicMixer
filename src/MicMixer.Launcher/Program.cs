using System.Diagnostics;

// Starts MicMixer.exe beside this exe with the same arguments and without the
// environment variables that make the .NET runtime load code before MicMixer's
// own code runs, such as a profiler DLL. Any program the user runs can set those in
// HKCU\Environment, and Windows passes them to MicMixer when it starts as
// administrator. Everything that starts MicMixer elevated starts it through here.

string streamDecky = Path.Combine(AppContext.BaseDirectory, "MicMixer.exe");
var startInfo = new ProcessStartInfo(streamDecky)
{
    UseShellExecute = false,
    WorkingDirectory = AppContext.BaseDirectory
};

foreach (string argument in args)
    startInfo.ArgumentList.Add(argument);

foreach (string name in startInfo.Environment.Keys.Where(IsRuntimeVariable).ToList())
    startInfo.Environment.Remove(name);

Process.Start(startInfo)?.Dispose();

static bool IsRuntimeVariable(string name) =>
    name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
    || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
    || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase);
