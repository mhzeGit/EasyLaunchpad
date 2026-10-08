using Launchpad.Core.Input;

namespace Launchpad.Tests;

public class WinKeyTapDetectorTests
{
    private const int LWin = 0x5B, RWin = 0x5C, E = 0x45, Ctrl = 0xA2, D = 0x44;

    // each step is (vk, isDown); returns how many taps were detected
    private static int Taps(params (int vk, bool down)[] events)
    {
        var d = new WinKeyTapDetector();
        int taps = 0;
        foreach (var (vk, down) in events)
            if (d.Feed(vk, down, !down)) taps++;
        return taps;
    }

    [Fact] public void LoneTapOfEitherWinKeyCounts()
    {
        Assert.Equal(1, Taps((LWin, true), (LWin, false)));
        Assert.Equal(1, Taps((RWin, true), (RWin, false)));
    }

    [Fact] public void AutoRepeatWhileHeldStillOneTap() =>
        Assert.Equal(1, Taps((LWin, true), (LWin, true), (LWin, true), (LWin, false)));

    [Fact] public void WinPlusAnotherKeyIsAShortcutNotATap()
    {
        Assert.Equal(0, Taps((LWin, true), (E, true), (E, false), (LWin, false)));
        Assert.Equal(0, Taps((LWin, true), (Ctrl, true), (D, true), (D, false), (Ctrl, false), (LWin, false)));
    }

    [Fact] public void ShortcutDoesNotPoisonTheNextTap() =>
        Assert.Equal(1, Taps((LWin, true), (E, true), (E, false), (LWin, false), (LWin, true), (LWin, false)));

    [Fact] public void KeysPressedBeforeWinAreIgnored() =>
        Assert.Equal(1, Taps((E, true), (E, false), (LWin, true), (LWin, false)));

    [Fact] public void KeyHeldAcrossWinTapDoesNotMatterIfPressedBeforeWin() =>
        Assert.Equal(1, Taps((Ctrl, true), (LWin, true), (LWin, false), (Ctrl, false)));

    [Fact] public void StrayWinReleaseWithoutPressIsIgnored() =>
        Assert.Equal(0, Taps((LWin, false)));

    [Fact] public void ResetClearsState()
    {
        var d = new WinKeyTapDetector();
        d.Feed(LWin, true, false);
        d.Reset();
        Assert.False(d.Feed(LWin, false, true));
    }
}
