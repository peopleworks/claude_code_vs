using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// Probe for the (undocumented) <c>at_mentioned</c> notification - the message behind the official
/// IDE plugins' "insert @File reference into the CLI composer" shortcut. Verified shape
/// (claudecode.nvim PROTOCOL.md + a decompiled CLI 2.1.161 handler):
/// <c>{"method":"at_mentioned","params":{"filePath":"...","lineStart":0-based?,"lineEnd":0-based?}}</c>
/// - a notification (no id), insert-not-submit, line keys omitted for whole-file mentions.
///
/// The open question this probe answers: is an at-mentioned IMAGE path a real image attachment at
/// submit (the model sees pixels), or just a text path it may or may not Read? Test files are
/// generated, not shipped: solid-colour PNGs whose content the model can only know by SEEING them
/// ("what colour is the attached image?" -> "red" proves pixel delivery), plus a .txt control with
/// a nonce only readable from disk. Files land in &lt;workspace&gt;\.claude\attachments\ to mirror the
/// planned panel-attachment staging (including its self-ignoring .gitignore).
/// </summary>
internal static class AtMentionProbe
{
    /// <summary>Create the probe files (idempotent) and return their absolute paths.</summary>
    public static string[] EnsureFiles(string workspace)
    {
        string dir = Path.Combine(workspace, ".claude", "attachments");
        Directory.CreateDirectory(dir);

        // Mirror the planned panel staging: a '*' .gitignore inside the folder keeps every
        // attachment (and the ignore file itself) out of the repo with no user-file edits.
        string gitignore = Path.Combine(dir, ".gitignore");
        if (!File.Exists(gitignore)) File.WriteAllText(gitignore, "*\n");

        string red = Path.Combine(dir, "spike-attach-red.png");
        if (!File.Exists(red)) File.WriteAllBytes(red, SolidPng(64, 64, 0xE5, 0x1C, 0x23));

        string blue = Path.Combine(dir, "spike-attach-blue.png");
        if (!File.Exists(blue)) File.WriteAllBytes(blue, SolidPng(64, 64, 0x1E, 0x63, 0xD0));

        string note = Path.Combine(dir, "spike-attach-note.txt");
        if (!File.Exists(note)) File.WriteAllText(note, "at_mention control file. The nonce is: kestrel-42.\n");

        return new[] { red, blue, note };
    }

    /// <summary>
    /// Send one at_mentioned probe. Variants: 'm' = red PNG as a workspace-RELATIVE forward-slash
    /// path (the form the VS Code plugin renders, e.g. "@app.ts#5-10"); 'M' = blue PNG as an
    /// ABSOLUTE native Windows path (what our panel would send for out-of-workspace files);
    /// 't' = the .txt control (the battle-tested at_mention case - if this one fails, the problem
    /// is delivery, not image handling).
    /// </summary>
    public static async Task SendAsync(IdeWebSocketServer server, string workspace, char variant, CancellationToken ct)
    {
        if (!server.HasClients)
        {
            Log.Warn("at_mentioned: no CLI connected - launch claude against this spike first");
            return;
        }

        string[] files = EnsureFiles(workspace);
        (string sentPath, string expect) = variant switch
        {
            'm' => (".claude/attachments/spike-attach-red.png",
                    "ask: 'what colour is the attached image?' -> RED proves pixels arrived"),
            'M' => (files[1], // absolute path, native separators
                    "ask: 'what colour is the attached image?' -> BLUE proves pixels arrived"),
            _   => (".claude/attachments/spike-attach-note.txt",
                    "ask: 'what is the nonce in the attached file?' -> 'kestrel-42' proves the mention resolved"),
        };

        // Whole-file mention: lineStart/lineEnd are omitted entirely (nvim sends nil -> keys absent).
        var @params = new JsonObject { ["filePath"] = sentPath };
        await server.BroadcastNotificationAsync("at_mentioned", @params, ct);

        Log.Info($"sent at_mentioned  filePath='{sentPath}'");
        Log.Info($"   check the claude composer: an @-mention chip should have appeared (insert, NOT submit).");
        Log.Info($"   then {expect}");
    }

    // ---------------------------------------------------------------------------------------
    // Minimal dependency-free PNG writer (solid-colour truecolour image). Spike-only: the real
    // extension gets bitmaps from WPF's clipboard/drag APIs and encodes with PngBitmapEncoder.
    // ---------------------------------------------------------------------------------------

    private static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        // Raw image data: each scanline is one filter byte (0 = None) + width * RGB.
        var raw = new byte[height * (1 + width * 3)];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            raw[i++] = 0;
            for (int x = 0; x < width; x++) { raw[i++] = r; raw[i++] = g; raw[i++] = b; }
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // signature

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour (RGB)
        // [10..12] compression/filter/interlace = 0
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", ZlibCompress(raw));
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    // PNG IDAT needs a zlib stream: 2-byte header + raw deflate + Adler-32 of the uncompressed data.
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        uint a = 1, s = 0;
        foreach (byte by in raw) { a = (a + by) % 65521; s = (s + a) % 65521; }
        var adler = new byte[4];
        WriteBigEndian(adler, 0, (s << 16) | a);
        ms.Write(adler, 0, 4);
        return ms.ToArray();
    }

    // Chunk = length(4 BE, data only) + type(4 ASCII) + data + CRC32(type + data).
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        var crcInput = new byte[4 + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, 4);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        s.Write(crc, 0, 4);
    }

    private static void WriteBigEndian(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
