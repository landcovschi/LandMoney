using System.Text;
using LandMoney.Web.Import;

namespace LandMoney.Web.Tests.Import;

/// <summary>What the importer will read, and what it refuses to guess at.</summary>
public class CsvTextTests
{
    [Fact]
    public void Plain_ascii_is_read()
    {
        Assert.True(CsvText.TryDecode("occurred_at,amount"u8.ToArray(), out var text, out _));
        Assert.Equal("occurred_at,amount", text);
    }

    [Fact]
    public void Multi_byte_utf8_survives()
    {
        var bytes = Encoding.UTF8.GetBytes("магазин лянка");

        Assert.True(CsvText.TryDecode(bytes, out var text, out _));
        Assert.Equal("магазин лянка", text);
    }

    // The commonest input there is: this is what a spreadsheet writes when told to
    // save as CSV UTF-8. Without the strip, U+FEFF ends up glued to the front of
    // the first header cell, so "occurred_at" does not equal "occurred_at" and the
    // file is refused for a missing column it plainly has.
    [Fact]
    public void A_utf8_byte_order_mark_is_stripped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "occurred_at"u8];

        Assert.True(CsvText.TryDecode(bytes, out var text, out _));
        Assert.Equal("occurred_at", text);
        Assert.DoesNotContain('﻿', text);
    }

    // #62's encoding trap. These are the cp1251 bytes for "магазин": 0xEC is a
    // three-byte UTF-8 lead and 0xE0 is not a continuation byte, so the sequence
    // cannot be UTF-8. Encoding.UTF8 would answer a string of replacement
    // characters and say nothing -- a description that imports fine, reads as
    // nonsense, and is never categorised.
    [Fact]
    public void A_cp1251_file_is_refused_rather_than_read_approximately()
    {
        var bytes = new byte[] { 0xEC, 0xE0, 0xE3, 0xE0, 0xE7, 0xE8, 0xED };

        Assert.False(CsvText.TryDecode(bytes, out var text, out var problem));
        Assert.Equal(string.Empty, text);
        Assert.Contains("UTF-8", problem);
    }

    [Fact]
    public void A_lone_invalid_byte_is_enough_to_refuse_the_file()
    {
        byte[] bytes = [.. "linella"u8, 0xFF, .. ",412.50"u8];

        Assert.False(CsvText.TryDecode(bytes, out _, out _));
    }

    // Named separately from "not valid UTF-8", which would be true and would point
    // at the wrong fix. UTF-16 is what Excel's "Unicode Text" export produces, and
    // the file looks perfectly correct on screen.
    [Theory]
    [InlineData(0xFF, 0xFE)]
    [InlineData(0xFE, 0xFF)]
    public void A_utf16_byte_order_mark_is_refused_by_name(byte first, byte second)
    {
        var bytes = new byte[] { first, second, 0x6F, 0x00 };

        Assert.False(CsvText.TryDecode(bytes, out _, out var problem));
        Assert.Contains("UTF-16", problem);
    }

    [Fact]
    public void An_empty_body_decodes_to_empty_text()
    {
        Assert.True(CsvText.TryDecode([], out var text, out _));
        Assert.Equal(string.Empty, text);
    }
}
