using System.Globalization;
using LandMoney.Web.Api;

namespace LandMoney.Web.Tests.Api;

/// <summary>The token a page ends with, and everything it must survive. #95.</summary>
// A cursor is written by one request and read by the next, so every test here is a
// round trip: the failure that matters is not "it threw" but "it came back meaning
// something else", which on a paged list is a row shown twice or a row nobody ever
// sees. Neither is visible in a table small enough to check by eye, which is why
// this file is longer than the type it tests.
//
// Nothing here opens a connection or starts a server. The comparison half -- the
// SQL this becomes -- is in TransactionPagingTests, beside the ordering it has to
// agree with.
public class TransactionCursorTests
{
    private static readonly DateOnly Day = new(2026, 8, 19);
    private static readonly Guid Id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    // --- the round trip -------------------------------------------------------

    [Fact]
    public void A_token_reads_back_as_the_position_it_was_written_from()
    {
        var written = new TransactionCursor(Day, Instant(), Id);

        Assert.True(TransactionCursor.TryParse(written.Encode(), out var read));
        Assert.Equal(written, read);
    }

    // The tick is the whole point of the format, and it is the field a "readable"
    // encoding loses first. Postgres keeps microseconds, so a token written from a
    // row that was read back out of the database has six meaningful digits -- and a
    // cursor that dropped them would land in the middle of every group of rows an
    // import wrote inside one microsecond, which is about fourteen of them per three
    // hundred.
    [Fact]
    public void The_instant_survives_to_the_tick()
    {
        var precise = new DateTimeOffset(2026, 8, 19, 21, 4, 5, TimeSpan.Zero).AddTicks(1_234_567);

        Assert.True(TransactionCursor.TryParse(new TransactionCursor(Day, precise, Id).Encode(), out var read));
        Assert.Equal(precise, read!.CreatedAt);
    }

    // A cursor is built from a row that has just been read, so it carries UTC. This
    // is the assertion that the offset written is the offset that comes back rather
    // than being converted into whatever zone the machine is in -- without
    // RoundtripKind a cursor quietly moves by the local offset, and every row inside
    // that window is skipped or repeated.
    [Fact]
    public void The_offset_is_not_converted_to_local_time()
    {
        var utc = new DateTimeOffset(2026, 8, 19, 21, 4, 5, TimeSpan.Zero);

        Assert.True(TransactionCursor.TryParse(new TransactionCursor(Day, utc, Id).Encode(), out var read));
        Assert.Equal(TimeSpan.Zero, read!.CreatedAt.Offset);
        Assert.Equal(utc, read.CreatedAt);
    }

    // The token has to be safe in a query string with no escaping, which is what
    // base64url buys over base64: "+" in a query string is a space, and "/" and "="
    // are punctuation a proxy or a router may take an interest in. A cursor that
    // survives one hop and not another fails as a 400 in production and passes every
    // test written on this machine.
    [Fact]
    public void A_token_carries_nothing_a_query_string_would_change()
    {
        var token = new TransactionCursor(Day, Instant(), Id).Encode();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.Equal(Uri.EscapeDataString(token), token);
    }

    // --- the culture rule, #31 ------------------------------------------------

    // **The rule this repository has been bitten by twice, arriving at a value that
    // is written and then read back.** `PlausibleDateAttribute` wrote its bound
    // through an interpolated {date:yyyy-MM-dd} and under ar-SA produced a Hijri
    // year -- the same format string, a different calendar, silently. A cursor
    // formatted in one calendar and parsed in another is a 400 the server answers to
    // a token it wrote itself, so a Saudi reader's list would end after one page.
    //
    // Both cultures are here for two different halves of the rule, which is #89's
    // finding: ro-RO renders "yyyy-MM-dd" exactly as the invariant culture does --
    // the dashes are literals, not separator placeholders -- so it catches a change
    // of *separator* and cannot catch a change of *calendar*. ar-SA is the one whose
    // default calendar is Umm al-Qura.
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("ro-RO")]
    [InlineData("en-US")]
    public void A_token_written_under_one_culture_reads_back_under_another(string culture)
    {
        var written = InCulture(culture, () => new TransactionCursor(Day, Instant(), Id).Encode());

        Assert.True(TransactionCursor.TryParse(written, out var read));
        Assert.Equal(new TransactionCursor(Day, Instant(), Id), read);
    }

    // The other direction, and the one a round trip inside one culture cannot see:
    // if both halves used the ambient calendar they would agree with each other and
    // disagree with every token in flight when the reader's machine changed. So the
    // bytes are pinned rather than only their round trip.
    [Fact]
    public void A_token_is_the_same_bytes_whatever_culture_wrote_it()
    {
        var invariant = new TransactionCursor(Day, Instant(), Id).Encode();

        Assert.Equal(invariant, InCulture("ar-SA", () => new TransactionCursor(Day, Instant(), Id).Encode()));
    }

    // Which is only worth asserting if ar-SA would in fact have rendered something
    // else -- otherwise the two tests above pass over a rule that is not being
    // applied. #89 recorded exactly this, having written a culture test that could
    // not fail.
    [Fact]
    public void The_culture_really_would_have_changed_the_day()
    {
        var hijri = InCulture("ar-SA", () => $"{Day:yyyy-MM-dd}");

        Assert.NotEqual("2026-08-19", hijri);
    }

    // **The reading half, under the culture that changes the calendar**, and it is a
    // gap a mutation found rather than a symmetry somebody noticed. The three tests
    // above set CurrentCulture while *writing* and then parse under the default, so
    // switching the parse to CultureInfo.CurrentCulture killed nothing: every token
    // they produced was read back by a machine that was no longer Saudi.
    //
    // It is the sharper half of the two. A cursor is written by one request and read
    // by the next, both on the same machine, so a parse that follows the ambient
    // calendar answers 400 to a token this server wrote a second earlier -- and the
    // list ends after one page for that reader, with a message about a cursor this
    // API did not issue.
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("ro-RO")]
    public void A_token_is_read_the_same_way_under_any_culture(string culture)
    {
        var written = new TransactionCursor(Day, Instant(), Id).Encode();

        var read = InCulture(culture, () =>
        {
            Assert.True(TransactionCursor.TryParse(written, out var parsed));
            return parsed;
        });

        Assert.Equal(new TransactionCursor(Day, Instant(), Id), read);
    }

    // --- what is refused ------------------------------------------------------

    // Every one of these is answered with a 400 by the endpoint rather than with an
    // empty page, and the reason is that an empty page is indistinguishable from
    // having reached the end of the list. A cursor that does not parse names a place
    // that does not exist, and there is nothing to do but say so.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all !!")]
    [InlineData("bm90LWEtY3Vyc29y")]                        // "not-a-cursor"
    [InlineData("MjAyNi0wOC0xOXwzZjI1MDRlMA==")]            // two fields, not three
    [InlineData("MjAyNi0xMy0wMXx8")]                        // a month that does not exist
    public void Anything_this_application_did_not_write_is_refused(string? token)
    {
        Assert.False(TransactionCursor.TryParse(token, out var cursor));
        Assert.Null(cursor);
    }

    // **This passes with the length check deleted, and that is worth saying rather
    // than leaving to be rediscovered.** A mutation removing it killed nothing,
    // because 4,096 characters of "A" decode to garbage, split into one field and are
    // refused by the shape check anyway. So the guard is not what makes this false --
    // it is what stops a decode buffer being allocated for whatever was sent, which
    // is a cost and not a behaviour, and no test in this suite can see the
    // difference.
    //
    // The test stays because the *behaviour* is worth holding; the claim about which
    // line produces it does not belong in its name.
    [Fact]
    public void A_token_far_too_long_to_be_one_is_refused()
    {
        Assert.False(TransactionCursor.TryParse(new string('A', 4096), out _));
    }

    // Each of the three fields on its own, so a parse that stopped checking one of
    // them is caught by name rather than by a token that is wrong in several ways at
    // once.
    [Theory]
    [InlineData("19/08/2026", "2026-08-19T21:04:05.0000000+00:00", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("2026-08-19", "yesterday", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("2026-08-19", "2026-08-19T21:04:05.0000000+00:00", "not-a-guid")]
    public void One_bad_field_is_enough_to_refuse_the_whole_token(string day, string instant, string id)
    {
        var forged = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes($"{day}|{instant}|{id}"));

        Assert.False(TransactionCursor.TryParse(forged, out _));
    }

    // A token with a field too many, which the theory above cannot reach because
    // every row in it is still three fields long. It is the mutation
    // `parts.Length != 3` -> `parts.Length < 3` that asked for this: too few is
    // refused either way, and too many would have been read as the first three with
    // the rest ignored -- so a token with a trailing bar, or one from a format this
    // application has not written yet, would be accepted as a position rather than
    // refused as a stranger.
    [Fact]
    public void A_token_with_a_field_too_many_is_refused()
    {
        var forged = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                "2026-08-19|2026-08-19T21:04:05.0000000+00:00|3f2504e0-4f89-11d3-9a0c-0305e82c3301|extra"));

        Assert.False(TransactionCursor.TryParse(forged, out _));
    }

    // And the same three fields, correct, do parse -- so the theory above is
    // measuring the field it changed rather than the shape of the forgery.
    [Fact]
    public void The_forged_shape_is_the_right_shape_when_the_fields_are_right()
    {
        var forged = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes(
                "2026-08-19|2026-08-19T21:04:05.0000000+00:00|3f2504e0-4f89-11d3-9a0c-0305e82c3301"));

        Assert.True(TransactionCursor.TryParse(forged, out var cursor));
        Assert.Equal(Day, cursor!.OccurredAt);
        Assert.Equal(Id, cursor.Id);
    }

    // --- what it is built from ------------------------------------------------

    // The three fields the ordering sorts by, and nothing else. A cursor built from
    // any other field of the row would name a position the list does not have.
    [Fact]
    public void A_cursor_is_the_three_keys_of_the_row_it_came_from()
    {
        var row = new TransactionResponse(
            Id, Day, 42.50m, "EUR", "linella", "groceries", "rules", false, Instant());

        Assert.True(TransactionCursor.TryParse(TransactionCursor.Encode(row), out var cursor));
        Assert.Equal(new TransactionCursor(Day, Instant(), Id), cursor);
    }

    private static DateTimeOffset Instant() =>
        new(2026, 8, 19, 21, 4, 5, TimeSpan.Zero);

    // Setting CurrentCulture rather than passing one in, because that is how the bug
    // arrives: nothing in the code names a culture, and the machine supplies one.
    // Restored in a finally so a failure here does not leave the rest of the suite
    // running under ar-SA.
    private static T InCulture<T>(string name, Func<T> act)
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
