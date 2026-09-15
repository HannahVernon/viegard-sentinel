using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Persistence.Postgres.Tests;

/// <summary>
/// Regression tests for the live-deployment failure of 2026-08-25: Npgsql
/// rejects DateTimeOffset values with non-zero offsets for timestamptz, and
/// nginx log timestamps arrive with local offsets (e.g., -05:00).  The
/// mapping layer must normalize every timestamp to UTC, preserving the
/// instant.
/// </summary>
public sealed class MappingTimestampTests
{
    private static readonly DateTimeOffset Local = new(2026, 8, 25, 17, 15, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Normalized_event_occurred_at_is_written_as_utc()
    {
        var normalizedEvent = new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = "syslog:udp-5514",
            SourceType = "syslog",
            OccurredAt = Local,
            Entities = [new EntityRef(EntityKind.IpAddress, "203.0.113.7")],
            Payload = new HttpRequestEvent { RemoteAddress = "203.0.113.7" },
            RawObservationId = Guid.NewGuid(),
        };

        var row = normalizedEvent.ToRow(sourceRefId: 1);

        Assert.Equal(TimeSpan.Zero, row.OccurredAt.Offset);
        Assert.Equal(Local, row.OccurredAt);
    }

    [Fact]
    public void Raw_observation_observed_at_is_written_as_utc()
    {
        var observation = new RawObservation
        {
            Id = Guid.NewGuid(),
            SourceId = "s",
            SourceType = "syslog",
            ObservedAt = Local,
            PayloadReference = "r",
        };

        var row = observation.ToRow("payload", sourceRefId: 1);

        Assert.Equal(TimeSpan.Zero, row.ObservedAt.Offset);
        Assert.Equal(Local, row.ObservedAt);
    }

    [Fact]
    public void Incident_window_bounds_are_written_as_utc()
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            CorrelationKey = "ip=203.0.113.7",
            WindowStart = Local,
            WindowEnd = Local.AddMinutes(10),
            EventIds = [],
            Evidence = [],
            State = IncidentState.Open,
        };

        var row = incident.ToRow();

        Assert.Equal(TimeSpan.Zero, row.WindowStart.Offset);
        Assert.Equal(TimeSpan.Zero, row.WindowEnd.Offset);
        Assert.Equal(incident.WindowStart, row.WindowStart);
        Assert.Equal(incident.WindowEnd, row.WindowEnd);
    }

    [Fact]
    public void Webauthn_credential_round_trips_row_mapping()
    {
        var credential = new AdminWebAuthnCredential
        {
            Id = ViegardId.New(),
            UserId = ViegardId.New(),
            CredentialId = [1, 2, 3],
            PublicKey = [4, 5, 6],
            SignCount = 42,
            Aaguid = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Transports = "[\"usb\"]",
            Name = "desk key",
            CreatedAt = Local,
            LastUsedAt = Local.AddMinutes(1),
        };

        var row = credential.ToRow();
        var restored = row.ToDomain();

        Assert.Equal(TimeSpan.Zero, row.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, row.LastUsedAt!.Value.Offset);
        Assert.Equal(credential.CredentialId, restored.CredentialId);
        Assert.Equal(credential.PublicKey, restored.PublicKey);
        Assert.Equal(credential.SignCount, restored.SignCount);
        Assert.Equal(credential.Name, restored.Name);
    }
}
