namespace Jorrk.Flagon.Storage.Postgres.Tests;

/// <summary>Pure checks of the notification channel name and payload format; no database is opened.</summary>
public class PostgresChangeChannelTests
{
    [Fact]
    public void A_channel_name_over_the_63_byte_identifier_limit_is_rejected_by_the_constructor()
    {
        var schema = new string('s', 57); // "flagon_" + 57 characters = 64 bytes

        var error = Assert.Throws<ArgumentException>(() => new PostgresFlagStore("Host=localhost;Database=flags", schema));

        Assert.Contains("63", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_channel_name_at_exactly_the_63_byte_limit_is_accepted()
    {
        var schema = new string('s', 56); // "flagon_" + 56 characters = 63 bytes

        using var store = new PostgresFlagStore("Host=localhost;Database=flags", schema);

        Assert.Equal(schema, store.Schema);
    }

    [Theory]
    [InlineData("42:1767225600123", 42L, 1767225600123L)]
    [InlineData("1:0", 1L, 0L)]
    [InlineData("9223372036854775807:1767225600123", long.MaxValue, 1767225600123L)]
    public void The_payload_is_the_version_and_the_commit_time_in_epoch_milliseconds(string payload, long version, long epochMilliseconds)
    {
        Assert.True(PostgresChangePayload.TryParse(payload, out var parsedVersion, out var commitTime));

        Assert.Equal(version, parsedVersion);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds), commitTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData(":1767225600123")]
    [InlineData("abc:1767225600123")]
    [InlineData("42:")]
    [InlineData("42:x")]
    [InlineData("-1:1767225600123")]
    [InlineData("42:1767225600123:extra")]
    [InlineData("42 :1767225600123")]
    public void A_malformed_payload_is_rejected_without_throwing(string payload)
    {
        Assert.False(PostgresChangePayload.TryParse(payload, out _, out _));
    }
}
