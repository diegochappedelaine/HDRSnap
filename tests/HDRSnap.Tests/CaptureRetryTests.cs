using System.Runtime.InteropServices;
using Xunit;

namespace HDRSnap.Tests;

public class CaptureRetryTests
{
    [Fact] public void Timeout_is_not_a_device_error()      => Assert.False(Capturer.IsDeviceError(new TimeoutException("x")));
    [Fact] public void Io_error_is_not_a_device_error()     => Assert.False(Capturer.IsDeviceError(new IOException("x")));
    [Fact] public void Unauthorized_is_not_a_device_error() => Assert.False(Capturer.IsDeviceError(new UnauthorizedAccessException("x")));
    [Fact] public void ComException_is_a_device_error()     => Assert.True(Capturer.IsDeviceError(new COMException("x")));
}
