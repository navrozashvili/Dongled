using Dongled.Abstractions;
using Dongled.Plugin.Logitech;
using Xunit;

namespace Dongled.Plugins.Tests.Logitech;

public sealed class LogitechGhubOptionsTests
{
    [Theory]
    [InlineData("ws://localhost:9010/")]
    [InlineData("ws://127.0.0.1:9010/")]
    [InlineData("ws://[::1]:9010/")]
    [InlineData("wss://localhost:9010/")]
    // The parameter is deliberately not called "url": CA1054 fires on a public method with a string
    // parameter whose name looks like a URI, and "dotnet format" applies its code fix, which generates
    // a throwing Uri overload rather than doing anything useful.
    public void A_loopback_websocket_url_is_accepted(string candidate)
    {
        Assert.True(LogitechGhubOptions.TryParseLoopback(candidate, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("ws://192.168.1.10:9010/")]  // another machine on the LAN
    [InlineData("ws://evil.example.com/")]   // a remote host
    [InlineData("ws://localhost.evil.com/")] // a host that merely starts with localhost
    [InlineData("http://localhost:9010/")]   // right host, wrong scheme
    [InlineData("file:///c:/windows/")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_loopback_websocket_is_refused(string? candidate)
    {
        Assert.False(LogitechGhubOptions.TryParseLoopback(candidate, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void A_missing_config_file_is_the_defaults_and_not_an_error()
    {
        var logger = new RecordingProviderLogger();

        var options = LogitechGhubOptions.Load(
            Path.Combine(Path.GetTempPath(), $"asw-absent-{Guid.NewGuid():N}.json"),
            logger);

        Assert.Equal(LogitechGhubOptions.Default, options);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= ProviderLogLevel.Warning);
    }

    [Fact]
    public void A_config_file_that_is_not_json_falls_back_to_the_defaults_and_says_so()
    {
        using var file = new TemporaryFile("{ this is not json");
        var logger = new RecordingProviderLogger();

        var options = LogitechGhubOptions.Load(file.Path, logger);

        Assert.Equal(LogitechGhubOptions.Default, options);
        Assert.Contains(logger.Entries, e => e.Level == ProviderLogLevel.Warning);
    }

    [Fact]
    public void A_non_loopback_url_in_the_config_falls_back_to_the_default_and_warns()
    {
        // Refusing to start would be worse for the user than ignoring a wrong config
        // loudly, and the default is the safe value.
        using var file = new TemporaryFile("""{ "url": "ws://192.168.1.10:9010/" }""");
        var logger = new RecordingProviderLogger();

        var options = LogitechGhubOptions.Load(file.Path, logger);

        Assert.Equal(LogitechGhubOptions.Default.Url, options.Url);
        Assert.Contains(
            logger.Entries,
            e => e.Level == ProviderLogLevel.Warning
                && e.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recognised_values_are_read_and_the_rest_stay_default()
    {
        using var file = new TemporaryFile("""
            {
              "url": "ws://127.0.0.1:9099/",
              "refreshSeconds": 30,
              "disconnectGraceSeconds": 0,
              "connectedStates": ["ACTIVE", "IDLE"],
              "somethingUnknown": 12
            }
            """);
        var logger = new RecordingProviderLogger();

        var options = LogitechGhubOptions.Load(file.Path, logger);

        Assert.Equal(new Uri("ws://127.0.0.1:9099/"), options.Url);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RefreshInterval);
        Assert.Equal(TimeSpan.Zero, options.DisconnectGrace);
        Assert.Equal(["ACTIVE", "IDLE"], options.ConnectedStates);
        Assert.Equal(LogitechGhubOptions.Default.ReconnectDelay, options.ReconnectDelay);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_rather_than_refused()
    {
        using var file = new TemporaryFile("""
            { "refreshSeconds": -5, "keepAliveSeconds": 100000, "disconnectGraceSeconds": -1, "connectedStates": [] }
            """);
        var logger = new RecordingProviderLogger();

        var options = LogitechGhubOptions.Load(file.Path, logger);

        Assert.Equal(TimeSpan.FromSeconds(2), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(300), options.KeepAliveInterval);
        Assert.Equal(TimeSpan.Zero, options.DisconnectGrace);

        // An empty list would mean no device is ever connected, which is never what anyone meant.
        Assert.Equal(["ACTIVE"], options.ConnectedStates);
    }

    [Fact]
    public void The_default_config_path_is_in_the_app_data_folder_and_not_beside_the_plugin()
    {
        // A writable config file beside a hash-pinned DLL is a tampering surface, and writing
        // one would change the plugin directory's manifest hash and re-block the plugin.
        var path = LogitechGhubOptions.DefaultConfigPath;

        Assert.Equal("logitech-ghub.json", Path.GetFileName(path));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dongled"),
            Path.GetDirectoryName(path));
        Assert.NotEqual(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void Loading_never_creates_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asw-absent-{Guid.NewGuid():N}.json");

        LogitechGhubOptions.Load(path, new RecordingProviderLogger());

        Assert.False(File.Exists(path), "the plugin only ever reads its config");
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"asw-ghub-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
