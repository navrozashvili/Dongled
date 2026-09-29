using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Dongled.ReleaseTool;

/// <summary>
/// The parts of a release that have to be exact: plugin manifests and the plugin version check,
/// the zips, and SHA256SUMS. build/release.ps1 runs the rest.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage:
          Dongled.ReleaseTool plugins --root <Plugins dir> --out <plugins.json> [--previous <plugins.json>]
              Hash every plugin folder with PluginManifest, check each against the previous
              release's plugins.json, and write this release's.
          Dongled.ReleaseTool zip --source <dir> --out <file.zip>
              Zip a directory's contents, with no top-level folder.
          Dongled.ReleaseTool sums --out <SHA256SUMS> <file>...
              Write "<lowercase hex>  <file name>" lines for the given files.
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            var options = Options.Parse(args.AsSpan(1));

            return args[0] switch
            {
                "plugins" => Plugins(options),
                "zip" => Zip(options),
                "sums" => Sums(options),
                _ => Fail($"Unknown command '{args[0]}'.{Environment.NewLine}{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Plugins(Options options)
    {
        var root = options.Required("--root");
        var output = options.Required("--out");
        var previousPath = options.Optional("--previous");

        var current = PluginRelease.Describe(root);
        foreach (var plugin in current)
        {
            Console.WriteLine($"{plugin.Name} {plugin.Version} {plugin.Sha256}");
        }

        IReadOnlyList<ReleasedPlugin> previous = [];
        if (previousPath is null)
        {
            Console.WriteLine("No previous release to compare with: every plugin is new.");
        }
        else
        {
            previous = PluginRelease.Read(File.ReadAllText(previousPath));
        }

        var check = PluginRelease.Compare(previous, current);
        foreach (var note in check.Notes)
        {
            Console.WriteLine(note);
        }

        if (check.Errors.Count > 0)
        {
            foreach (var error in check.Errors)
            {
                Console.Error.WriteLine($"error: {error}");
            }

            return 1;
        }

        File.WriteAllText(output, PluginRelease.Write(current), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Wrote {output}.");
        return 0;
    }

    private static int Zip(Options options)
    {
        var source = options.Required("--source");
        var output = options.Required("--out");

        if (File.Exists(output))
        {
            File.Delete(output);
        }

        // Entry names use '/' and there is no top-level folder, so extracting the zip anywhere puts
        // Dongled.exe directly in that folder.
        ZipFile.CreateFromDirectory(source, output, CompressionLevel.Optimal, includeBaseDirectory: false);
        Console.WriteLine($"Wrote {output}.");
        return 0;
    }

    private static int Sums(Options options)
    {
        var output = options.Required("--out");
        if (options.Positional.Count == 0)
        {
            return Fail("sums needs at least one file.");
        }

        var lines = new StringBuilder();
        foreach (var file in options.Positional)
        {
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));

            // Two spaces: the text-mode separator sha256sum writes and "sha256sum -c" reads.
            lines.Append(hash).Append("  ").Append(Path.GetFileName(file)).Append('\n');
        }

        File.WriteAllText(output, lines.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.Write(lines.ToString());
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    /// <summary><c>--name value</c> pairs, and whatever is left over.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{args[i]} needs a value.");
                    }

                    options._named[args[i]] = args[++i];
                }
                else
                {
                    options.Positional.Add(args[i]);
                }
            }

            return options;
        }

        public string Required(string name) =>
            _named.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"{name} is required.");

        public string? Optional(string name) =>
            _named.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
