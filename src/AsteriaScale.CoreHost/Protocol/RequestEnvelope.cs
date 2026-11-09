using System.Text.Json;
using System.Text.Unicode;

namespace AsteriaScale.CoreHost.Protocol;

/// <summary>
/// The envelope of one inbound frame. Anything wrong with the envelope itself
/// is connection-fatal (core.fault, exit 3): the peer is not speaking v1, and a
/// broken stream is never resynchronised. Problems inside a payload are
/// request-level (bad_request) because the id is known and can get a terminal.
/// </summary>
internal sealed class RequestEnvelope
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = WireProtocol.MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        AllowDuplicateProperties = false,
    };

    private RequestEnvelope(long id, string command, JsonElement payload)
    {
        Id = id;
        Command = command;
        Payload = payload;
    }

    public long Id { get; }

    public string Command { get; }

    /// <summary>The payload object, or an Undefined element when the frame has none.</summary>
    public JsonElement Payload { get; }

    public sealed record Violation(string Code, string Detail);

    /// <summary>
    /// Parses one frame. The document stays open in <paramref name="document"/>
    /// because <see cref="Payload"/> points into it; the caller disposes it
    /// after admission.
    /// </summary>
    public static RequestEnvelope? Parse(
        ReadOnlyMemory<byte> frame,
        long lastId,
        out JsonDocument? document,
        out Violation? violation)
    {
        document = null;
        ReadOnlySpan<byte> bytes = frame.Span;
        if (bytes.Length == 0)
        {
            violation = new(ErrorCodes.InvalidFrame, "an empty line is not a frame");
            return null;
        }

        // JsonDocument would skip a BOM, and \r is legal JSON whitespace, so a
        // CRLF or BOM-prefixed peer would otherwise pass unnoticed.
        if (bytes.StartsWith("﻿"u8) || bytes.Contains((byte)'\r'))
        {
            violation = new(ErrorCodes.InvalidFrame, "a frame must not contain a BOM or \\r");
            return null;
        }

        // Strict UTF-8 first: an invalid sequence inside a string must not
        // become a replacement character.
        if (!Utf8.IsValid(bytes))
        {
            violation = new(ErrorCodes.InvalidFrame, "the frame is not valid UTF-8");
            return null;
        }

        try
        {
            document = JsonDocument.Parse(frame, DocumentOptions);
        }
        catch (JsonException exception)
        {
            violation = new(ErrorCodes.InvalidFrame, "not JSON: " + exception.Message);
            return null;
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            violation = new(ErrorCodes.InvalidFrame, "a frame must be a JSON object");
            return null;
        }

        JsonElement version = default, id = default, command = default, payload = default;
        foreach (JsonProperty member in root.EnumerateObject())
        {
            switch (member.Name)
            {
                case "version": version = member.Value; break;
                case "id": id = member.Value; break;
                case "command": command = member.Value; break;
                case "payload": payload = member.Value; break;
                default:
                    violation = new(ErrorCodes.InvalidFrame, $"unknown envelope member '{member.Name}'");
                    return null;
            }
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int frameVersion))
        {
            violation = new(ErrorCodes.InvalidFrame, "the frame has no integer version");
            return null;
        }

        if (frameVersion != WireProtocol.Version)
        {
            violation = new(ErrorCodes.UnsupportedVersion, $"frame version {frameVersion} is not {WireProtocol.Version}");
            return null;
        }

        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long requestId) || requestId < 1)
        {
            violation = new(ErrorCodes.InvalidId, "the id must be an integer of at least 1");
            return null;
        }

        if (requestId <= lastId)
        {
            // Ids strictly increase for the life of the process, which makes
            // duplicate and stale ids an O(1) check on both sides.
            violation = new(ErrorCodes.InvalidId, $"id {requestId} does not increase (last {lastId})");
            return null;
        }

        if (command.ValueKind != JsonValueKind.String || command.GetString() is not { Length: > 0 } commandName)
        {
            violation = new(ErrorCodes.InvalidFrame, "the frame has no command");
            return null;
        }

        if (payload.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
        {
            violation = new(ErrorCodes.InvalidFrame, "the payload must be a JSON object");
            return null;
        }

        violation = null;
        return new RequestEnvelope(requestId, commandName, payload);
    }
}
