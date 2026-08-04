using System.Text;

namespace CopyTool.Core;

/// <summary>
/// The line protocol between the host and its elevated worker.
///
/// One command per line, fields separated by tabs — but every field is escaped
/// on the way out and unescaped on the way in, because a file name may legally
/// contain a tab. NTFS permits control characters in names, and an archive
/// extractor, a WSL process or any caller using a <c>\\?\</c> path can create
/// one.
///
/// Splitting an unescaped line on tabs let such a name supply the *next* field
/// as well: a file called <c>payload.dll&lt;TAB&gt;C:\Windows\System32\x.dll</c>
/// parsed as its own destination, which is an arbitrary write in the one process
/// on the machine that holds administrator rights. Escaping closes that, and it
/// keeps the worker's parse loop to two lines — which JSON would not.
/// </summary>
public static class ElevatedProtocol
{
    private static readonly char[] Special = ['\\', '\t', '\r', '\n'];

    public static string Encode(params string[] fields)
    {
        var line = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) line.Append('\t');
            Escape(fields[i], line);
        }
        return line.ToString();
    }

    /// <summary>
    /// Splits a line back into its fields, or returns an empty array when the
    /// line is not well formed — so a caller rejects a malformed command with the
    /// same length check it already needs.
    /// </summary>
    public static string[] Decode(string line)
    {
        string[] parts = line.Split('\t');

        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryUnescape(parts[i], out string value)) return [];
            parts[i] = value;
        }

        return parts;
    }

    private static void Escape(string value, StringBuilder into)
    {
        if (value.IndexOfAny(Special) < 0) { into.Append(value); return; }

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': into.Append(@"\\"); break;
                case '\t': into.Append(@"\t"); break;
                case '\r': into.Append(@"\r"); break;
                case '\n': into.Append(@"\n"); break;
                default:   into.Append(c);     break;
            }
        }
    }

    private static bool TryUnescape(string value, out string result)
    {
        if (value.IndexOf('\\') < 0) { result = value; return true; }

        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { text.Append(value[i]); continue; }

            // A trailing backslash is not an escape sequence at all, and an
            // unknown one means the sender and this decoder disagree. Either way
            // the safe answer is to reject the whole line rather than guess at a
            // path that is about to be written with administrator rights.
            if (++i == value.Length) { result = ""; return false; }

            switch (value[i])
            {
                case '\\': text.Append('\\'); break;
                case 't':  text.Append('\t'); break;
                case 'r':  text.Append('\r'); break;
                case 'n':  text.Append('\n'); break;
                default:   result = ""; return false;
            }
        }

        result = text.ToString();
        return true;
    }
}
