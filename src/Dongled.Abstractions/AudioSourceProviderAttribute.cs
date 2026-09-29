namespace Dongled.Abstractions;

/// <summary>
/// Declares the single provider a plugin assembly offers.
/// </summary>
/// <remarks>
/// <para>Apply once per plugin assembly:</para>
/// <code>[assembly: AudioSourceProvider(typeof(MyProvider))]</code>
/// <para>
/// Discovery is explicit so the host never scans types and never runs a constructor while
/// deciding what a plugin is. An assembly without this attribute is rejected with a clear
/// message instead of a type-load error.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class AudioSourceProviderAttribute : Attribute
{
    /// <summary>Declares <paramref name="providerType"/> as this assembly's provider.</summary>
    /// <param name="providerType">
    /// A concrete type implementing <see cref="IAudioSourceProvider"/> with a public
    /// parameterless constructor.
    /// </param>
    public AudioSourceProviderAttribute(Type providerType) => ProviderType = providerType;

    /// <summary>The declared provider type.</summary>
    public Type ProviderType { get; }
}
