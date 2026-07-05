using System.Runtime.CompilerServices;
using VerifyTests;

namespace Ttr.Tests;

/// <summary>
/// Verify configuration (brief M5, Windows CI lane). File references and stack frames rendered into
/// snapshot frames carry OS-native path separators; on Windows those are '\' where the Linux-captured
/// verified files have '/'. This normalises '\' → '/' as a global scrubber that runs AFTER Verify's
/// built-in solution/project-directory tokenisation, so the <c>{SolutionDirectory}</c>/<c>{ProjectDirectory}</c>
/// tokens (matched against native paths) stay intact and only the residual separators in the path tails
/// are normalised. It is a no-op on Linux/macOS (no backslashes in the frames), so every existing
/// verified snapshot stays byte-identical (AC6).
/// </summary>
internal static class VerifyModuleInit
{
    [ModuleInitializer]
    public static void Init() => VerifierSettings.AddScrubber(builder => builder.Replace('\\', '/'));
}
