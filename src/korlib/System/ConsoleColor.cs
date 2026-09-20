// NeutrinoOS korlib - System.ConsoleColor
//
// Phase 2: the 16 standard console colors. On NeutrinoOS the serial
// console has no intrinsic color, so these values are mapped to ANSI SGR
// sequences by System.Console (30-37 / 90-97 for foreground, 40-47 /
// 100-107 for background).

namespace System;

/// <summary>
/// Specifies constants that define foreground and background colors for
/// the console. NeutrinoOS implements these as ANSI SGR sequences on the
/// serial console; terminals that do not understand SGR will show the
/// escape codes as text unless they are filtered.
/// </summary>
public enum ConsoleColor
{
    /// <summary>The color black.</summary>
    Black = 0,
    /// <summary>The color dark blue.</summary>
    DarkBlue = 1,
    /// <summary>The color dark green.</summary>
    DarkGreen = 2,
    /// <summary>The color dark cyan (dark blue-green).</summary>
    DarkCyan = 3,
    /// <summary>The color dark red.</summary>
    DarkRed = 4,
    /// <summary>The color dark magenta (dark purple).</summary>
    DarkMagenta = 5,
    /// <summary>The color dark yellow (brown).</summary>
    DarkYellow = 6,
    /// <summary>The color gray.</summary>
    Gray = 7,
    /// <summary>The color dark gray.</summary>
    DarkGray = 8,
    /// <summary>The color blue.</summary>
    Blue = 9,
    /// <summary>The color green.</summary>
    Green = 10,
    /// <summary>The color cyan (blue-green).</summary>
    Cyan = 11,
    /// <summary>The color red.</summary>
    Red = 12,
    /// <summary>The color magenta (purple).</summary>
    Magenta = 13,
    /// <summary>The color yellow.</summary>
    Yellow = 14,
    /// <summary>The color white.</summary>
    White = 15
}
