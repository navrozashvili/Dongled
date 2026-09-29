using System.Linq;
using System.Reflection;
using Dongled.Core.Interop;
using Xunit;

namespace Dongled.Core.Tests.Interop;

/// <summary>
/// The ten unused <c>IPolicyConfig</c> slots must not be declared as placeholder
/// <c>int Foo()</c> methods just to pad the vtable: the real methods take pointers, so calling a
/// placeholder would corrupt the stack. These tests pin both halves — the slot order, because that is what keeps
/// <c>SetDefaultEndpoint</c> at the right offset, and the signatures, because that is what
/// makes calling any other slot safe.
/// </summary>
public class PolicyConfigSignatureTests
{
    /// <summary>
    /// Declaration order, which is vtable order. Sorting by metadata token recovers it:
    /// <see cref="Type.GetMethods()"/> does not promise source order, but tokens are assigned
    /// in declaration order within a type.
    /// </summary>
    private static MethodInfo[] VtableOrder() =>
        typeof(IPolicyConfig)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(method => method.MetadataToken)
            .ToArray();

    [Fact]
    public void The_slot_order_places_set_default_endpoint_where_windows_expects_it()
    {
        Assert.Equal(
            [
                "GetMixFormat",
                "GetDeviceFormat",
                "ResetDeviceFormat",
                "SetDeviceFormat",
                "GetProcessingPeriod",
                "SetProcessingPeriod",
                "GetShareMode",
                "SetShareMode",
                "GetPropertyValue",
                "SetPropertyValue",
                "SetDefaultEndpoint",
                "SetEndpointVisibility",
            ],
            VtableOrder().Select(method => method.Name).ToArray());
    }

    [Fact]
    public void No_slot_is_a_bare_padding_declaration()
    {
        foreach (var method in VtableOrder())
        {
            var parameters = method.GetParameters();

            // Every real IPolicyConfig method takes the endpoint identifier first. A zero-argument
            // declaration is a placeholder padding the vtable.
            Assert.NotEmpty(parameters);
            Assert.Equal(typeof(string), parameters[0].ParameterType);
        }
    }

    [Fact]
    public void Every_slot_returns_the_raw_result_rather_than_throwing_on_failure()
    {
        foreach (var method in VtableOrder())
        {
            Assert.Equal(typeof(int), method.ReturnType);
            Assert.NotNull(method.GetCustomAttribute<System.Runtime.InteropServices.PreserveSigAttribute>());
        }
    }
}
