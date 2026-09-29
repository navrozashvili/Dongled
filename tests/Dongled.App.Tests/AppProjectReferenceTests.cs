using Xunit;

namespace Dongled.App.Tests;

/// <summary>
/// Proves a test project can reference the WinUI App project, which every test here depends on.
/// Deliberately reflection-only: instantiating a WinUI type needs a XAML runtime the test host
/// does not have.
/// </summary>
public class AppProjectReferenceTests
{
    [Fact]
    public void The_app_assembly_is_loadable_from_a_test_host()
    {
        var appType = typeof(Dongled.App.App);

        // "Dongled", not "Dongled.App": the project is Dongled.App but its AssemblyName is
        // overridden so the shipped binary is Dongled.exe rather than Dongled.App.exe. The
        // namespace and the assembly name diverge on purpose, and this is the one place that
        // divergence is asserted, so a future rename that forgets the override fails here.
        Assert.Equal("Dongled", appType.Assembly.GetName().Name);
    }
}
