using System.IO;

namespace Dongled.Core.Tests.Plugins;

/// <summary>
/// A plugins root of its own for one test, so a test that corrupts a plugin cannot affect another.
/// </summary>
internal sealed class TemporaryPluginRoot : IDisposable
{
    public TemporaryPluginRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "asw-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Where the sample plugin's build output was staged by the <c>StageSamplePlugin</c> target in
    /// this project's csproj. A subdirectory, because the default load context probes the base
    /// directory and would resolve the sample from it.
    /// </summary>
    public static string SampleSourceDirectory { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "SamplePlugin");

    /// <summary>The plugins root to hand the loader.</summary>
    public string Path { get; }

    /// <summary>The single candidate assembly inside a plugin directory.</summary>
    public static string MainAssembly(string pluginDirectory) =>
        Directory.GetFiles(pluginDirectory, "Dongled.Plugin.*.dll").Single();

    /// <summary>
    /// Where a ported plugin's build output was staged by the <c>StagePortedPlugins</c> target, by the
    /// same reasoning as <see cref="SampleSourceDirectory"/>.
    /// </summary>
    /// <remarks>
    /// A function of the directory name rather than one property per plugin, so a test can be a theory
    /// over the ported plugins without carrying an absolute path in its theory data - which would put
    /// the repository's path into the test's displayed name and so into any captured output.
    /// </remarks>
    public static string PortedPluginSource(string directoryName) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "PortedPlugins", directoryName);

    /// <summary>Copy the built sample plugin in, and return its directory.</summary>
    public string AddSample(string directoryName = "Sample") =>
        AddBuiltPlugin(SampleSourceDirectory, directoryName);

    /// <summary>
    /// Copy a plugin's whole staged build output in, and return its directory. Everything is copied,
    /// including the <c>.deps.json</c> and any NuGet dependency, because the manifest hashes every file
    /// and <c>AssemblyDependencyResolver</c> resolves siblings by directory.
    /// </summary>
    public string AddBuiltPlugin(string sourceDirectory, string directoryName)
    {
        var destination = AddDirectory(directoryName);

        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(sourceDirectory, source);
            var target = System.IO.Path.Combine(destination, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return destination;
    }

    /// <summary>Create an empty directory under the root, and return it.</summary>
    public string AddDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A loaded plugin assembly was read from a stream and holds no lock, but a test that
            // deliberately locks a file may still hold it. A leftover temp directory is not worth
            // failing a run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
