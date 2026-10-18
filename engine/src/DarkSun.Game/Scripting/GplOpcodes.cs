namespace DarkSun.Game.Scripting;

/// <summary>
/// A GPL opcode: libgff's mnemonic and the operand kinds the DSUN.EXE handler
/// reads (E expression, A address, S string; null for the opcodes with their
/// own operand grammar). Port of <c>_OPCODE_TABLE</c> in <c>src/gpl_parser.py</c>;
/// see <c>dsun-exe-reference.md</c> §2.
/// </summary>
public sealed record GplOpcode(string Mnemonic, string? Operands);

public static class GplOpcodes
{
    public const byte StringEnd = 0x00;
    public const byte Jump = 0x12;
    public const byte CallLocal = 0x13;
    public const byte CallGlobal = 0x14;
    public const byte LocalReturn = 0x15;
    public const byte LoadVar = 0x16;
    public const byte Compare = 0x17;
    public const byte LoadAccum = 0x18;
    public const byte EndFunction = 0x19;
    public const byte Search = 0x33;
    public const byte SetRecord = 0x40;
    public const byte Menu = 0x48;
    public const byte MenuEnd = 0x4A;
    public const byte SearchQualifier = 0x53;

    /// <summary>
    /// Every valid opcode. Others (26 4A 4C–4E 53 55–57 60 71–75, 81 and up)
    /// reach DSUN.EXE's error stub, which ends the script.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, GplOpcode> All = new Dictionary<byte, GplOpcode>
    {
        [0x00] = new("STRING_END", ""),
        [0x01] = new("LONG /=", "EE"),
        [0x02] = new("BYTE_DEC", "E"),
        [0x03] = new("WORD_DEC", "E"),
        [0x04] = new("LONG_DEC", "E"),
        [0x05] = new("BYTE_INC", "E"),
        [0x06] = new("WORD_INC", "E"),
        [0x07] = new("LONG_INC", "E"),
        [0x08] = new("HUNT", "E"),
        [0x09] = new("GETXY", "E"),
        [0x0A] = new("STRING_COPY", "EE"),
        [0x0B] = new("PHYS_DAMAGE", "EE"),
        [0x0C] = new("CHANGE_MONEY", "E"),
        [0x0D] = new("SET_VAR", ""),
        [0x0E] = new("TOGGLE_ACCUM", ""),
        [0x0F] = new("GET_STATUS", "E"),
        [0x10] = new("GET_LOS", "EEE"),
        [0x11] = new("LONG *=", "EE"),
        [0x12] = new("JUMP", "A"),
        [0x13] = new("CALL_LOCAL", "A"),
        [0x14] = new("CALL_GLOBAL", "AE"),
        [0x15] = new("LOCAL_RET", ""),
        [0x16] = new("LOAD_VAR", null),
        [0x17] = new("COMPARE", "E"),
        [0x18] = new("LOAD_ACCUM", null),
        [0x19] = new("END_FUNCTION", ""),
        [0x1A] = new("NEXTTO", "EE"),
        [0x1B] = new("CREATE_INLOS_TRIGGER", "EEEE"),
        [0x1C] = new("CREATE_NOTINLOS_TRIGGER", "EEEE"),
        [0x1D] = new("CLEAR_LOS", "E"),
        [0x1E] = new("NAMETONUM", "E"),
        [0x1F] = new("NUMTONAME", "E"),
        [0x20] = new("BITSNOOP", "EE"),
        [0x21] = new("AWARD", "EE"),
        [0x22] = new("REQUEST", "EEEE"),
        [0x23] = new("SOURCE_TRACE", "S"),
        [0x24] = new("SHOP", "E"),
        [0x25] = new("CLONE", "EEEEEE"),
        [0x27] = new("IFCOMPARE", "EA"),
        [0x28] = new("OP_28", "EES"),
        [0x29] = new("ORELSE", "A"),
        [0x2A] = new("CLEAR_PIC", ""),
        [0x2B] = new("CONTINUE", ""),
        [0x2C] = new("LOG", "S"),
        [0x2D] = new("DAMAGE", "EE"),
        [0x2E] = new("SOURCE_LINE", "E"),
        [0x2F] = new("DROP", "EEE"),
        [0x30] = new("PASS_TIME", "E"),
        [0x31] = new("EXIT_GPL", ""),
        [0x32] = new("FETCH", "EE"),
        [0x33] = new("SEARCH", null),
        [0x34] = new("GETPARTY", "E"),
        [0x35] = new("FIGHT", ""),
        [0x36] = new("FLEE", "E"),
        [0x37] = new("FOLLOW", "EE"),
        [0x38] = new("ASK_YES_NO", ""),
        [0x39] = new("GIVE", "EEEE"),
        [0x3A] = new("GO", "EE"),
        [0x3B] = new("INPUT_BIGNUM", "E"),
        [0x3C] = new("GOXY", "EEE"),
        [0x3D] = new("READORDERS", "E"),
        [0x3E] = new("IF", "A"),
        [0x3F] = new("ELSE", "A"),
        [0x40] = new("SETRECORD", null),
        [0x41] = new("SET_OTHER", "E"),
        [0x42] = new("INPUT_STRING", "E"),
        [0x43] = new("INPUT_NUMBER", "E"),
        [0x44] = new("INPUT_MONEY", "E"),
        [0x45] = new("JOINPARTY", "E"),
        [0x46] = new("LEAVEPARTY", "E"),
        [0x47] = new("LOCKDOOR", ""),
        [0x48] = new("MENU", null),
        [0x49] = new("SETTHING", "EE"),
        [0x4B] = new("LOCAL_SUB_TRACE", "EEE"),
        [0x4F] = new("PRINT_STRING", "EE"),
        [0x50] = new("PRINT_NUMBER", "EE"),
        [0x51] = new("PRINT_NEWLINE", ""),
        [0x52] = new("RAND", "E"),
        [0x54] = new("SHOW_PIC", "E"),
        [0x58] = new("SKILLROLL", "EEE"),
        [0x59] = new("STAT_ROLL", "EEE"),
        [0x5A] = new("STRING_COMPARE", "EE"),
        [0x5B] = new("MATCH_STRING", "EE"),
        [0x5C] = new("TAKE", "EEEE"),
        [0x5D] = new("SOUND", "E"),
        [0x5E] = new("TELEPORT", "EEEEE"),
        [0x5F] = new("MUSIC", "E"),
        [0x61] = new("CMPEND", ""),
        [0x62] = new("WAIT", "E"),
        [0x63] = new("WHILE", "A"),
        [0x64] = new("WEND", "A"),
        [0x65] = new("CREATE_ATTACK_TRIGGER", "EEE"),
        [0x66] = new("CREATE_LOOK_TRIGGER", "EEE"),
        [0x67] = new("ENDIF", ""),
        [0x68] = new("CREATE_MOVE_TILE_TRIGGER", "EEEEE"),
        [0x69] = new("CREATE_DOOR_TILE_TRIGGER", "EEEEE"),
        [0x6A] = new("CREATE_MOVE_BOX_TRIGGER", "EEEEEEE"),
        [0x6B] = new("CREATE_DOOR_BOX_TRIGGER", "EEEEEEE"),
        [0x6C] = new("CREATE_PICKUP_TRIGGER", "EEE"),
        [0x6D] = new("CREATE_USE_TRIGGER", "EEE"),
        [0x6E] = new("CREATE_TALKTO_TRIGGER", "EEE"),
        [0x6F] = new("CREATE_NOORDERS_TRIGGER", "EEE"),
        [0x70] = new("CREATE_USEWITH_TRIGGER", "EEEE"),
        [0x76] = new("BYTE +=", "EE"),
        [0x77] = new("BYTE -=", "EE"),
        [0x78] = new("BYTE *=", "EE"),
        [0x79] = new("BYTE /=", "EE"),
        [0x7A] = new("WORD +=", "EE"),
        [0x7B] = new("WORD -=", "EE"),
        [0x7C] = new("WORD *=", "EE"),
        [0x7D] = new("WORD /=", "EE"),
        [0x7E] = new("LONG +=", "EE"),
        [0x7F] = new("LONG -=", "EE"),
        [0x80] = new("RANGE", "EE"),
    };
}
