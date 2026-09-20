using System.Runtime.InteropServices;
using Xunit;

namespace HDRSnap.Tests;

/// The sizes must be those of wingdi.h / winuser.h: QueryDisplayConfig writes native
/// records into an array marshalled at the stride of the managed struct.
public class InteropLayoutTests
{
    [Fact] public void Luid_is_8()                    => Assert.Equal(8,   Marshal.SizeOf<Native.LUID>());
    [Fact] public void PathSourceInfo_is_20()         => Assert.Equal(20,  Marshal.SizeOf<Native.DISPLAYCONFIG_PATH_SOURCE_INFO>());
    [Fact] public void PathTargetInfo_is_48()         => Assert.Equal(48,  Marshal.SizeOf<Native.DISPLAYCONFIG_PATH_TARGET_INFO>());
    [Fact] public void PathInfo_is_72()               => Assert.Equal(72,  Marshal.SizeOf<Native.DISPLAYCONFIG_PATH_INFO>());
    [Fact] public void ModeInfo_is_64()               => Assert.Equal(64,  Marshal.SizeOf<Native.DISPLAYCONFIG_MODE_INFO>());
    [Fact] public void DeviceInfoHeader_is_20()       => Assert.Equal(20,  Marshal.SizeOf<Native.DISPLAYCONFIG_DEVICE_INFO_HEADER>());
    [Fact] public void SdrWhiteLevel_is_24()          => Assert.Equal(24,  Marshal.SizeOf<Native.DISPLAYCONFIG_SDR_WHITE_LEVEL>());
    [Fact] public void SourceDeviceName_is_84()       => Assert.Equal(84,  Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
    [Fact] public void MonitorInfoEx_is_104()         => Assert.Equal(104, Marshal.SizeOf<Native.MONITORINFOEX>());
}
