namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Maps Linux evdev keycodes onto the Windows virtual-key values that
/// <c>HoldHotkey</c> stores. Settings written by the Windows app therefore mean
/// the same keys here, and hotkey names render identically on both platforms.
/// </summary>
internal static class EvdevKeys
{
    public static int ToVirtualKey(ushort evdevCode) =>
        Map.TryGetValue(evdevCode, out var vk) ? vk : 0;

    private static readonly Dictionary<ushort, int> Map = new()
    {
        [1] = 0x1B,   // KEY_ESC
        [2] = '1', [3] = '2', [4] = '3', [5] = '4', [6] = '5',
        [7] = '6', [8] = '7', [9] = '8', [10] = '9', [11] = '0',
        [12] = 0xBD,  // minus
        [13] = 0xBB,  // equal
        [14] = 0x08,  // backspace
        [15] = 0x09,  // tab
        [16] = 'Q', [17] = 'W', [18] = 'E', [19] = 'R', [20] = 'T',
        [21] = 'Y', [22] = 'U', [23] = 'I', [24] = 'O', [25] = 'P',
        [26] = 0xDB,  // left brace
        [27] = 0xDD,  // right brace
        [28] = 0x0D,  // enter
        [29] = 0xA2,  // left ctrl
        [30] = 'A', [31] = 'S', [32] = 'D', [33] = 'F', [34] = 'G',
        [35] = 'H', [36] = 'J', [37] = 'K', [38] = 'L',
        [39] = 0xBA,  // semicolon
        [40] = 0xDE,  // apostrophe
        [41] = 0xC0,  // grave
        [42] = 0xA0,  // left shift
        [43] = 0xDC,  // backslash
        [44] = 'Z', [45] = 'X', [46] = 'C', [47] = 'V', [48] = 'B',
        [49] = 'N', [50] = 'M',
        [51] = 0xBC,  // comma
        [52] = 0xBE,  // dot
        [53] = 0xBF,  // slash
        [54] = 0xA1,  // right shift
        [55] = 0x6A,  // keypad *
        [56] = 0xA4,  // left alt
        [57] = 0x20,  // space
        [58] = 0x14,  // caps lock
        [59] = 0x70, [60] = 0x71, [61] = 0x72, [62] = 0x73, [63] = 0x74,   // F1-F5
        [64] = 0x75, [65] = 0x76, [66] = 0x77, [67] = 0x78, [68] = 0x79,   // F6-F10
        [69] = 0x90,  // num lock
        [70] = 0x91,  // scroll lock
        [71] = 0x67, [72] = 0x68, [73] = 0x69,   // keypad 7 8 9
        [74] = 0x6D,  // keypad -
        [75] = 0x64, [76] = 0x65, [77] = 0x66,   // keypad 4 5 6
        [78] = 0x6B,  // keypad +
        [79] = 0x61, [80] = 0x62, [81] = 0x63,   // keypad 1 2 3
        [82] = 0x60,  // keypad 0
        [83] = 0x6E,  // keypad .
        [87] = 0x7A,  // F11
        [88] = 0x7B,  // F12
        [96] = 0x0D,  // keypad enter
        [97] = 0xA3,  // right ctrl
        [98] = 0x6F,  // keypad /
        [99] = 0x2C,  // print screen (sysrq)
        [100] = 0xA5, // right alt
        [102] = 0x24, // home
        [103] = 0x26, // up
        [104] = 0x21, // page up
        [105] = 0x25, // left
        [106] = 0x27, // right
        [107] = 0x23, // end
        [108] = 0x28, // down
        [109] = 0x22, // page down
        [110] = 0x2D, // insert
        [111] = 0x2E, // delete
        [119] = 0x13, // pause
        [125] = 0x5B, // left meta
        [126] = 0x5C, // right meta
        [127] = 0x5D, // menu (compose)
        [183] = 0x7C, [184] = 0x7D, [185] = 0x7E, [186] = 0x7F,           // F13-F16
        [187] = 0x80, [188] = 0x81, [189] = 0x82, [190] = 0x83,           // F17-F20
        [191] = 0x84, [192] = 0x85, [193] = 0x86, [194] = 0x87,           // F21-F24
    };
}
