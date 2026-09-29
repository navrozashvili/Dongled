using Dongled.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Pipeline;

/// <summary>
/// Routes one provider's log lines into the host's logger at that plugin's configured level.
/// </summary>
/// <remarks>
/// <para>
/// Thread safe, because <see cref="IProviderLogger"/> says so and a provider logging from a
/// vendor callback has no other option. Everything here is either immutable or a single read of
/// a delegate, and <see cref="ILogger"/> implementations are required to be thread safe.
/// </para>
/// <para>
/// The level is read through a delegate on every call rather than captured, so changing a
/// plugin's level in the UI takes effect without restarting the provider.
/// </para>
/// <para>
/// <see cref="ProviderLogLevel"/>'s members and values match <see cref="LogLevel"/> exactly,
/// which is what makes the cast below sound. The <see cref="Enum.IsDefined{TEnum}(TEnum)"/>
/// check is what keeps it sound for a value that arrived from outside the enum.
/// </para>
/// </remarks>
internal sealed class ProviderLoggerAdapter : IProviderLogger
{
    private readonly ILogger _logger;
    private readonly Func<LogLevel> _minimumLevel;

    /// <param name="logger">The host logger, already scoped to this provider.</param>
    /// <param name="minimumLevel">
    /// This plugin's configured minimum, read on every call. The default is
    /// <see cref="LogLevel.Warning"/> in every build configuration: a bug report from a release
    /// user has to contain the same lines a developer sees.
    /// </param>
    internal ProviderLoggerAdapter(ILogger logger, Func<LogLevel> minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(minimumLevel);

        _logger = logger;
        _minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public bool IsEnabled(ProviderLogLevel level)
    {
        // None means "record nothing" and is never a severity, so it is false even when the
        // configured minimum is itself None. This matches Microsoft.Extensions.Logging, and it is
        // the case that a level >= minimum comparison gets wrong.
        if (level == ProviderLogLevel.None || !Enum.IsDefined(level))
        {
            return false;
        }

        var minimum = _minimumLevel();
        if (minimum == LogLevel.None)
        {
            return false;
        }

        return (int)level >= (int)minimum && _logger.IsEnabled((LogLevel)(int)level);
    }

    /// <inheritdoc />
    public void Log(ProviderLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        // Hoisted rather than written inline. CA1873 objects to any argument expression at a
        // logging call that it cannot see is cheap, and it does not recognise the IsEnabled guard
        // above because that one is this type's own rather than the ILogger's.
        var text = message ?? string.Empty;

        _logger.Log((LogLevel)(int)level, exception, "{ProviderMessage}", text);
    }
}
