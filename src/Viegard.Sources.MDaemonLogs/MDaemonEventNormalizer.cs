using System.Text.Json;
using System.Collections.Concurrent;
using Viegard.Application.Sources;
using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>
/// Normalizes MDaemon file-tail DTOs into <see cref="MDaemonLogEvent"/> payloads.
/// Malformed payloads fail closed and never throw.
/// </summary>
public sealed class MDaemonEventNormalizer(MDaemonSourceOptions options) : IEventNormalizer
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public string SourceType => MDaemonLogSource.MDaemonSourceType;

    public NormalizationResult Normalize(RawObservation observation, string rawPayload)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return NormalizationResult.Failure("Empty MDaemon payload.");
        }

        MDaemonLineDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MDaemonLineDto>(rawPayload, MDaemonJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return NormalizationResult.Failure($"MDaemon payload is not valid MDaemonLineDto JSON: {ex.Message}");
        }

        if (dto is null)
        {
            return NormalizationResult.Failure("MDaemon payload deserialized to null.");
        }

        if (dto.SchemaVersion != 1)
        {
            return NormalizationResult.Failure($"Unsupported MDaemonLineDto schema version {dto.SchemaVersion}.");
        }

        if (dto.LogKind is null)
        {
            return NormalizationResult.Failure("MDaemonLineDto is missing LogKind.");
        }

        if (string.IsNullOrWhiteSpace(dto.LineText))
        {
            return NormalizationResult.Failure("MDaemonLineDto is missing LineText.");
        }

        var parsed = dto.LogKind.Value == MDaemonLogKind.DynamicScreening
            ? DynScrnParser.Parse(dto.LineText)
            : SessionTranscriptParser.Parse(dto.LineText, dto.LogKind.Value);

        if (parsed is null)
        {
            return NormalizationResult.Failure("MDaemon line is a banner or separator line.");
        }

        if (!options.IncludeNoise && parsed.IsNoise)
        {
            return NormalizationResult.Failure("MDaemon line is configured noise.");
        }

        parsed = ApplySessionContext(observation.SourceId, dto.FileName, parsed);

        var entities = new List<EntityRef>();
        if (!string.IsNullOrWhiteSpace(parsed.RemoteIp))
        {
            entities.Add(new EntityRef(EntityKind.IpAddress, parsed.RemoteIp));
        }

        var capturedAt = dto.CapturedAt == default ? observation.ObservedAt : dto.CapturedAt;
        var payload = new MDaemonLogEvent
        {
            LogKind = parsed.LogKind,
            EventKind = parsed.EventKind,
            RemoteIp = parsed.RemoteIp,
            Port = parsed.Port,
            Reason = parsed.Reason,
            SessionId = parsed.SessionId,
            Message = parsed.Message,
            ReportedAt = parsed.ReportedAt,
        };

        return NormalizationResult.Success(new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = observation.SourceId,
            SourceType = MDaemonLogSource.MDaemonSourceType,
            OccurredAt = parsed.ReportedAt ?? capturedAt,
            Entities = entities,
            Payload = payload,
            RawObservationId = observation.Id,
        });
    }

    private MDaemonParsedLine ApplySessionContext(string sourceId, string? fileName, MDaemonParsedLine parsed)
    {
        if (parsed.LogKind == MDaemonLogKind.DynamicScreening || string.IsNullOrWhiteSpace(fileName))
        {
            return parsed;
        }

        var key = sourceId + "|" + fileName;
        if (parsed.EventKind == MDaemonEventKind.SessionLine && !string.IsNullOrWhiteSpace(parsed.SessionId))
        {
            _sessions[key] = new SessionState(parsed.SessionId, null);
            return parsed;
        }

        if (parsed.EventKind == MDaemonEventKind.ConnectionAccepted)
        {
            _sessions.TryGetValue(key, out var existing);
            var sessionId = parsed.SessionId ?? existing?.SessionId;
            _sessions[key] = new SessionState(sessionId, parsed.RemoteIp);
            return parsed with { SessionId = sessionId };
        }

        if (parsed.EventKind == MDaemonEventKind.AuthenticationFailed && _sessions.TryGetValue(key, out var state))
        {
            return parsed with
            {
                RemoteIp = parsed.RemoteIp ?? state.RemoteIp,
                SessionId = parsed.SessionId ?? state.SessionId,
            };
        }

        return parsed;
    }

    private sealed record SessionState(string? SessionId, string? RemoteIp);
}
