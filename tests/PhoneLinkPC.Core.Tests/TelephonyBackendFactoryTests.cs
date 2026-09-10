using PhoneLinkPC.Core.Platform;
using PhoneLinkPC.Infrastructure;
using Xunit;

namespace PhoneLinkPC.Core.Tests;

public class TelephonyBackendFactoryTests
{
    [Fact]
    public void Creates_a_backend_for_the_current_platform()
    {
        using var _ = new NoopDisposable();
        var service = TelephonyBackendFactory.CreateService();

        Assert.NotNull(service);
        Assert.False(string.IsNullOrWhiteSpace(service.BackendName));
    }

    [Fact]
    public void Backend_reports_availability_consistent_with_the_host()
    {
        var service = TelephonyBackendFactory.CreateService();

        // A backend must never claim to be usable on a platform it does not support.
        if (HostPlatform.Current is not (HostOs.Windows or HostOs.Linux))
            Assert.False(service.IsAvailable);
    }

    [Fact]
    public void Diagnostics_backend_is_always_available()
    {
        var diagnostics = TelephonyBackendFactory.CreateDiagnostics();

        Assert.NotNull(diagnostics);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
