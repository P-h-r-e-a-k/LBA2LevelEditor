namespace LBAAssembler.Lba1;

// Walks an LBA1 track (movement) script only far enough to find the scene track points
// it steers the actor through, so the map can draw the patrol route.
internal static class Lba1TrackScript
{
    // Operand byte counts per opcode; -1 = unknown, -2 = NUL-terminated string (play FLA).
    private static readonly sbyte[] OperandSize =
    {
        0, 0, 1, 1, 1, 0, 0, 2, 1, 1,      // END NOP BODY ANIM GOTO_POINT WAIT_ANIM LOOP ANGLE POS_POINT LABEL
        2, 0, 1, 2, 2, 1, 2, 1, 5, 0,      // GOTO STOP GOTO_SYM_POINT WAIT_NUM_ANIM SAMPLE GOTO_POINT_3D SPEED BACKGROUND WAIT_NUM_SECOND NO_BODY
        2, 2, 2, 2, 2, 0, 0, 2, 2, 2,      // BETA OPEN_LEFT/RIGHT/UP/DOWN CLOSE WAIT_DOOR SAMPLE_RND SAMPLE_ALWAYS SAMPLE_STOP
        -2, 1, 2, 2, 4,                    // PLAY_FLA REPEAT_SAMPLE SIMPLE_SAMPLE FACE_TWINSEN ANGLE_RND
    };

    private static readonly string[] Names =
    {
        "END", "NOP", "BODY", "ANIM", "GOTO_POINT", "WAIT_ANIM", "LOOP", "ANGLE", "POS_POINT", "LABEL",
        "GOTO", "STOP", "GOTO_SYM_POINT", "WAIT_NUM_ANIM", "SAMPLE", "GOTO_POINT_3D", "SPEED", "BACKGROUND", "WAIT_NUM_SECOND", "NO_BODY",
        "BETA", "OPEN_LEFT", "OPEN_RIGHT", "OPEN_UP", "OPEN_DOWN", "CLOSE", "WAIT_DOOR", "SAMPLE_RND", "SAMPLE_ALWAYS", "SAMPLE_STOP",
        "PLAY_FLA", "REPEAT_SAMPLE", "SIMPLE_SAMPLE", "FACE_TWINSEN", "ANGLE_RND",
    };

    private static readonly HashSet<int> PointOpcodes = new() { 4, 8, 12, 15 };

    // One instruction per line: NAME followed by its operand bytes (decimal).
    public static string Disassemble(byte[] script)
    {
        var text = new System.Text.StringBuilder();
        var p = 0;
        while (p < script.Length)
        {
            int op = script[p++];
            if (op >= OperandSize.Length || OperandSize[op] == -1)
            {
                text.AppendLine($"?? opcode {op} (rest not decoded)");
                break;
            }
            text.Append(Names[op]);
            if (OperandSize[op] == -2)
            {
                var start = p;
                while (p < script.Length && script[p] != 0) p++;
                text.Append(' ').Append(System.Text.Encoding.ASCII.GetString(script, start, p - start));
                p++;
            }
            else
            {
                for (var k = 0; k < OperandSize[op] && p < script.Length; k++) text.Append(' ').Append(script[p++]);
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    // The referenced track point numbers in script order, or what was found before the walk
    // hit an opcode it doesn't know.
    public static List<int> Points(byte[] script)
    {
        var points = new List<int>();
        var p = 0;
        while (p < script.Length)
        {
            int op = script[p++];
            if (op >= OperandSize.Length || OperandSize[op] == -1) break;
            if (OperandSize[op] == -2)
            {
                while (p < script.Length && script[p] != 0) p++;
                p++;
                continue;
            }
            if (PointOpcodes.Contains(op) && p < script.Length && (points.Count == 0 || points[^1] != script[p])) points.Add(script[p]);
            p += OperandSize[op];
        }
        return points;
    }

    // True when the whole script decodes with the table above (a sanity check on the table).
    public static bool DecodesFully(byte[] script)
    {
        var p = 0;
        while (p < script.Length)
        {
            int op = script[p++];
            if (op >= OperandSize.Length || OperandSize[op] == -1) return false;
            if (OperandSize[op] == -2) { while (p < script.Length && script[p] != 0) p++; p++; continue; }
            p += OperandSize[op];
        }
        return p == script.Length;
    }
}
