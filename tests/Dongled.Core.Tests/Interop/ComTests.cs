using Dongled.Core.Interop;
using Xunit;

namespace Dongled.Core.Tests.Interop;

/// <summary>
/// Pins the release contract the audio layer depends on. These touch real COM, so every
/// assertion here has to hold on a machine with no audio endpoints at all: a CI runner has
/// none, and a test that quietly needs hardware is a test that only fails on someone else's
/// machine.
/// </summary>
public class ComTests
{
    [Fact]
    public void Release_makes_the_wrapper_unusable_so_the_release_is_provably_deterministic()
    {
        var enumerator = Com.CreateDeviceEnumerator();

        Com.Release(enumerator);

        // A released wrapper throws rather than calling through a pointer it no longer owns.
        // That is what makes "released deterministically" observable instead of a claim.
        Assert.Throws<ObjectDisposedException>(
            () => enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out _));
    }

    [Fact]
    public void Release_is_idempotent_so_a_finally_block_cannot_double_release()
    {
        var enumerator = Com.CreateDeviceEnumerator();

        Com.Release(enumerator);
        Com.Release(enumerator);
    }

    [Fact]
    public void Release_ignores_null_and_non_com_arguments()
    {
        Com.Release(null);
        Com.Release("not a runtime callable wrapper");
    }

    [Fact]
    public void Enumerating_endpoints_returns_a_collection_whose_devices_all_have_identifiers()
    {
        var enumerator = Com.CreateDeviceEnumerator();
        try
        {
            var hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.All, out var collection);
            Assert.Equal(0, hr);
            try
            {
                Assert.Equal(0, collection.GetCount(out var count));

                for (uint i = 0; i < count; i++)
                {
                    Assert.Equal(0, collection.Item(i, out var device));
                    try
                    {
                        Assert.Equal(0, device.GetId(out var idPtr));
                        Assert.False(string.IsNullOrEmpty(Com.ReadCoTaskMemString(idPtr)));
                    }
                    finally
                    {
                        Com.Release(device);
                    }
                }
            }
            finally
            {
                Com.Release(collection);
            }
        }
        finally
        {
            Com.Release(enumerator);
        }
    }

    [Fact]
    public void An_unknown_endpoint_identifier_is_reported_as_a_failed_result_rather_than_thrown()
    {
        var enumerator = Com.CreateDeviceEnumerator();
        try
        {
            var hr = enumerator.GetDevice("not-an-endpoint-identifier", out var device);

            Assert.NotEqual(0, hr);
            Assert.Null(device);
        }
        finally
        {
            Com.Release(enumerator);
        }
    }

    [Fact]
    public void The_policy_configuration_object_can_be_created()
    {
        IPolicyConfig policy;
        try
        {
            policy = Com.CreatePolicyConfig();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Assert.Skip($"CPolicyConfigClient is not registered on this machine: 0x{ex.HResult:X8}");
            return;
        }

        Com.Release(policy);
    }
}
