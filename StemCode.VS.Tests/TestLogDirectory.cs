using System.Runtime.CompilerServices;

namespace StemCode.VS.Tests;

internal static class TestLogDirectory
{
    /// <summary>Keeps LogService (linked from the extension) out of the user's %LOCALAPPDATA%\StemCode\Logs.</summary>
    [ModuleInitializer]
    internal static void Initialize()
        => Environment.SetEnvironmentVariable("STEMCODE_VS_LOG_DIR", Path.Combine(Path.GetTempPath(), "StemCode.VS.Tests", "Logs"));
}
