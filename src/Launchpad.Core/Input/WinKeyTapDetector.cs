namespace Launchpad.Core.Input;

/// <summary>The rule for "lone Windows key tap": Win goes down and up with no other key going down in between.</summary>
public sealed class WinKeyTapDetector
{
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private bool _winDown, _otherKeyPressed;

    public void Reset() => _winDown = _otherKeyPressed = false;

    /// <summary>Feeds one physical key event; returns true when it completes a lone Win tap.</summary>
    public bool Feed(int vk, bool down, bool up)
    {
        if (vk is VK_LWIN or VK_RWIN)
        {
            if (down)
            {
                if (!_winDown) { _winDown = true; _otherKeyPressed = false; }   // ignore auto-repeat
            }
            else if (up && _winDown)
            {
                _winDown = false;
                return !_otherKeyPressed;
            }
        }
        else if (down && _winDown)
        {
            _otherKeyPressed = true;   // Win+something: leave it entirely to Windows
        }
        return false;
    }
}
