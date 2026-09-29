using Dongled.App.Startup;

namespace Dongled.App.Tests.Fakes;

/// <summary>A Run value held in memory, classified the same way the registry one is.</summary>
internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public const string ThisExecutable = @"C:\Apps\Dongled\Dongled.exe";

    public string? Recorded { get; set; }

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase) { ThisExecutable };

    public bool FailWrites { get; set; }

    public int Writes { get; private set; }

    public StartupEntry Read() => StartupEntry.Classify(Recorded, ThisExecutable, ExistingFiles.Contains);

    public void SetEnabled(bool enabled)
    {
        if (FailWrites)
        {
            throw new UnauthorizedAccessException("The Run key is read-only.");
        }

        Writes++;
        Recorded = enabled ? $"\"{ThisExecutable}\"" : null;
    }
}
