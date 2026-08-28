using System.Text;

namespace LandMoney.Web.Import;

/// <summary>Turns the bytes of an uploaded file into text, or says why it will not.</summary>
// #62's encoding trap: "exports carry BOMs and sometimes cp1251. A mis-decoded
// description is a category the rules will never match." So the rule here is that
// a file this cannot read is *refused*, loudly, rather than read approximately.
public static class CsvText
{
    // throwOnInvalidBytes is the whole of it. Encoding.UTF8 -- the static one --
    // replaces a byte it cannot read with U+FFFD and says nothing, which turns a
    // cp1251 description into a string of question marks that parses fine, imports
    // fine, and is never categorised. This instance throws instead.
    //
    // encoderShouldEmitUTF8Identifier is false only because it is the encoding
    // half of the same object and nothing here encodes; it has no effect on
    // GetString.
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The file as text, or false and a sentence naming the fix.</summary>
    // Deliberately not `new StreamReader(stream, Strict, detectEncodingFromByteOrderMarks: true)`,
    // which is the obvious way to do this and is the version that fails silently
    // in the commonest case. On seeing a UTF-8 BOM, StreamReader swaps in its own
    // Encoding.UTF8 -- the replacing one -- so the strict instance above is
    // discarded exactly when a spreadsheet wrote the file. Stripping the BOM by
    // hand keeps one decoder for every input, and the correctness of this function
    // then does not depend on an internal detail of StreamReader at all.
    public static bool TryDecode(byte[] bytes, out string text, out string problem)
    {
        var span = bytes.AsSpan();

        // Named separately rather than left to fall through to "not valid UTF-8",
        // which would be a true sentence pointing at the wrong fix. UTF-16 is what
        // Excel's "Unicode Text (*.txt)" export produces, and it is a realistic way
        // to arrive here holding a file that looks perfectly fine on screen.
        if (span.Length >= 2
            && ((span[0] == 0xFF && span[1] == 0xFE) || (span[0] == 0xFE && span[1] == 0xFF)))
        {
            text = string.Empty;
            problem = "The file begins with a UTF-16 byte order mark. Save it as CSV UTF-8 and send it again.";
            return false;
        }

        // EF BB BF. Skipped rather than decoded: it would otherwise become U+FEFF
        // at the start of the first header cell, so `occurred_at` would not match
        // `occurred_at` and the file would be refused for a missing column it has.
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        try
        {
            text = Strict.GetString(span);
            problem = string.Empty;
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            problem =
                "The file is not valid UTF-8. An export saved as cp1251 or another single-byte encoding "
                + "reads as this. Re-save it as CSV UTF-8 and send it again.";
            return false;
        }
    }
}
