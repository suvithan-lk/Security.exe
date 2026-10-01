using System.Runtime.InteropServices;
using Security.Infrastructure.Services;
using Xunit;

namespace Security.Core.Tests;

/// <summary>
/// Regression guard for the WTS lock-state probe.
///
/// The native WTSINFOEX union is 8-byte aligned (WTSINFOEX_LEVEL1 ends with
/// LARGE_INTEGER members), so the level data starts at offset 8 — a
/// Sequential C# struct with a uint Level would place it at 4 and read
/// SessionState (WTSActive = 0) as SessionFlags, reporting EVERY unlocked
/// session as Locked. That bug silently disabled camera auto-start on every
/// launch, because the monitor treats Unknown/Locked as "do not capture".
///
/// These tests pin the marshalled layout to what the real Windows buffer
/// looks like (Level@0, SessionId@8, SessionState@12, SessionFlags@16,
/// WinStationName@20 — verified against a live
/// WTSQuerySessionInformation(WTSSessionInfoEx) call).
/// </summary>
public class WindowsSessionProbeLayoutTests
{
    private static Type WtsInfoExType()
    {
        var type = typeof(WindowsSessionService)
            .GetNestedType("WtsInfoEx", System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(type);
        return type!;
    }

    [Fact]
    public void WtsInfoEx_data_starts_at_offset_8_not_4()
    {
        var offset = (int)Marshal.OffsetOf(WtsInfoExType(), "Data");

        Assert.Equal(8, offset);
    }

    [Fact]
    public void WtsInfoEx_level_sits_at_offset_0()
    {
        var offset = (int)Marshal.OffsetOf(WtsInfoExType(), "Level");

        Assert.Equal(0, offset);
    }

    /// <summary>The innermost struct that actually declares SessionId/State/Flags.</summary>
    private static Type Level1Type()
    {
        var union = WtsInfoExType().GetField("Data")!.FieldType;      // WtsInfoExLevel (explicit union)
        var level1 = union.GetField("Level1")!.FieldType;             // WtsInfoExLevel1 (sequential prefix)
        return level1;
    }

    [Fact]
    public void SessionFlags_is_read_where_windows_writes_it()
    {
        // The struct is a prefix of WTSINFOEX_LEVEL1 laid out at Data:
        // SessionId (u32) + SessionState (u32) + SessionFlags (u32).
        var flags = (int)Marshal.OffsetOf(Level1Type(), "SessionFlags");

        // Data starts at 8 → SessionFlags must land at absolute offset 16.
        Assert.Equal(16, 8 + flags);
    }

    [Fact]
    public void Session_state_field_precedes_the_flags()
    {
        var level1 = Level1Type();

        var state = (int)Marshal.OffsetOf(level1, "SessionState");
        var flags = (int)Marshal.OffsetOf(level1, "SessionFlags");

        Assert.Equal(4, state);  // after SessionId
        Assert.Equal(8, flags);  // after SessionState — reading state as flags was the bug
    }
}
