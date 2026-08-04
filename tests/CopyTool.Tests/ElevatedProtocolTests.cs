using CopyTool.Core;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// The one channel in this program that crosses an integrity boundary.
///
/// The worker on the far side runs with administrator rights and does exactly
/// what the line tells it: copy this, to there. Fields used to be joined with
/// raw tabs, and a file name may contain a tab — NTFS allows control characters,
/// and an archive extractor or a <c>\\?\</c> caller can create one. Such a name
/// supplied the *next* field as well, which made the destination attacker-chosen.
/// </summary>
public class ElevatedProtocolTests
{
    [Fact]
    public void A_tab_in_a_name_cannot_forge_the_destination_field()
    {
        // Exactly the shape of the attack: the source path is real, and everything
        // after the tab was previously read as the destination.
        const string hostile = "C:\\src\\payload.dll\tC:\\Windows\\System32\\x.dll";

        string[] parts = ElevatedProtocol.Decode(
            ElevatedProtocol.Encode("copy", hostile, @"C:\dst\payload.dll", "10"));

        Assert.Equal(4, parts.Length);
        Assert.Equal(hostile, parts[1]);
        Assert.Equal(@"C:\dst\payload.dll", parts[2]);
    }

    [Theory]
    [InlineData(@"C:\ordinary\path.txt")]
    [InlineData(@"C:\שם בעברית\קובץ.txt")]
    [InlineData("with\ttab")]
    [InlineData("with\r\nnewlines")]
    [InlineData(@"ends with a backslash\")]
    [InlineData(@"C:\double\\separator")]
    [InlineData(@"\\server\share\file")]
    [InlineData("")]
    public void Fields_survive_a_round_trip(string value)
    {
        string[] parts = ElevatedProtocol.Decode(ElevatedProtocol.Encode("copy", value, "tail"));

        Assert.Equal(3, parts.Length);
        Assert.Equal("copy", parts[0]);
        Assert.Equal(value, parts[1]);
        Assert.Equal("tail", parts[2]);
    }

    [Fact]
    public void An_encoded_line_never_contains_a_bare_control_character()
    {
        string line = ElevatedProtocol.Encode("copy", "a\tb", "c\r\nd");

        // Exactly the two separators the format allows, and nothing that could end
        // the line early on the way to a StreamReader.
        Assert.Equal(2, line.Count(c => c == '\t'));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData(@"copy\")]          // an escape with nothing after it
    [InlineData(@"copy\q")]         // an escape this decoder does not define
    [InlineData("copy\ta\\")]
    public void A_malformed_line_decodes_to_nothing(string line)
    {
        // Rejecting the whole line rather than salvaging fields: the alternative is
        // guessing at a path that is about to be written with administrator rights.
        Assert.Empty(ElevatedProtocol.Decode(line));
    }
}
