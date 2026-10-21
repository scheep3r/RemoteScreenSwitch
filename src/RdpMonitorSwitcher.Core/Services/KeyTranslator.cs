namespace RdpMonitorSwitcher.Core.Services;

/// <summary>One key event to send to the server.</summary>
public readonly record struct RdpKeyEvent(bool Down, uint Scancode);

/// <summary>
/// Translates Windows low-level keyboard events (KBDLLHOOKSTRUCT) into RDP scancodes and
/// tracks which keys are held, so they can be released when the view loses focus or changes.
/// Quirks handled as in FreeRDP's Windows client: NumLock is reported as extended,
/// Pause arrives as plain 0x45 and must be sent as Ctrl+NumLock, right Shift is not extended.
/// </summary>
public sealed class KeyTranslator
{
    public const uint Extended = 0x100;
    public const uint ScanLeftControl = 0x1D;
    public const uint ScanNumLock = 0x45;
    public const uint ScanRightShift = 0x36;

    private readonly HashSet<uint> _pressed = [];

    public IReadOnlyCollection<uint> PressedKeys => _pressed;

    /// <param name="scanCode">KBDLLHOOKSTRUCT.scanCode.</param>
    /// <param name="extended">LLKHF_EXTENDED was set.</param>
    /// <param name="down">Key down (false = key up).</param>
    public IReadOnlyList<RdpKeyEvent> Translate(uint scanCode, bool extended, bool down)
    {
        var code = (scanCode & 0xFF) | (extended ? Extended : 0);
        if (code == 0)
            return [];

        if (code == (ScanNumLock | Extended))
        {
            code = ScanNumLock; // real NumLock
        }
        else if (code == ScanNumLock)
        {
            // Pause/Break: a one-shot Ctrl+NumLock on key down, nothing on key up.
            return down
                ?
                [
                    new(true, ScanLeftControl), new(true, ScanNumLock),
                    new(false, ScanLeftControl), new(false, ScanNumLock),
                ]
                : [];
        }
        else if (code == (ScanRightShift | Extended))
        {
            code = ScanRightShift;
        }

        if (down)
            _pressed.Add(code);
        else
            _pressed.Remove(code);
        return [new RdpKeyEvent(down, code)];
    }

    /// <summary>Key-up events for every key that is still held; clears the state.</summary>
    public IReadOnlyList<RdpKeyEvent> ReleaseAll()
    {
        var events = _pressed.Select(code => new RdpKeyEvent(false, code)).ToList();
        _pressed.Clear();
        return events;
    }
}
