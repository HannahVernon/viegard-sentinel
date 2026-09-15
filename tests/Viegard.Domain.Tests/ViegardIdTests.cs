namespace Viegard.Domain.Tests;

public sealed class ViegardIdTests
{
    [Fact]
    public void New_produces_version7_guids()
    {
        var id = ViegardId.New();

        Assert.Equal(7, id.Version);
        // Guid.Variant exposes the 4-bit nibble; RFC 4122/9562 variant is
        // the top bits 10xx, i.e. any value from 8 through 11.
        Assert.InRange(id.Variant, 8, 11);
    }

    [Fact]
    public void New_produces_unique_ids()
    {
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(ids.Add(ViegardId.New()));
        }
    }

    [Fact]
    public void New_embeds_a_current_timestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var id = ViegardId.New();
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        // The first 48 bits of a v7 uuid are big-endian unix milliseconds.
        var bytes = id.ToByteArray(bigEndian: true);
        long unixMs = 0;
        for (var i = 0; i < 6; i++)
        {
            unixMs = (unixMs << 8) | bytes[i];
        }

        var embedded = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        Assert.InRange(embedded, before, after);
    }

    [Fact]
    public void Ids_minted_later_sort_later_in_postgres_byte_order()
    {
        // PostgreSQL compares uuid values as the 16 RFC-order bytes, which
        // for v7 means time order.  Verify with explicit timestamps so the
        // random tail cannot influence the outcome.
        var earlier = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var later = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddHours(1));

        var earlierBytes = earlier.ToByteArray(bigEndian: true);
        var laterBytes = later.ToByteArray(bigEndian: true);

        Assert.True(earlierBytes.AsSpan().SequenceCompareTo(laterBytes) < 0);

        // .NET's Guid comparison agrees, so in-process sorts match the db.
        Assert.True(earlier.CompareTo(later) < 0);
    }
}
