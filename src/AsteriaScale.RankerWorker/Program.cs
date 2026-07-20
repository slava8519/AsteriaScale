using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.RankerWorker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.Error.WriteLine("[ranker] starting");

        if (!WorkerOptions.TryParse(args, out var options, out var argumentError))
        {
            Console.Error.WriteLine($"[ranker] {argumentError}");
            return 2;
        }

        using var input = new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false)
        {
            AutoFlush = true
        };

        using var engine = new RankerEngine(options!);
        if (options!.Smoke)
        {
            return await RunSmokeAsync(engine, output);
        }

        while (await input.ReadLineAsync() is { } line)
        {
            RankerRequest? request = null;
            RankerResponse response;
            var shouldExit = false;

            try
            {
                request = JsonSerializer.Deserialize<RankerRequest>(line, RankerProtocolV2.JsonOptions);
                if (request is null)
                {
                    throw new RankerException("invalid_request", "Request must be a JSON object.");
                }

                (response, shouldExit) = await HandleAsync(engine, request);
            }
            catch (JsonException ex)
            {
                response = Error(request?.RequestId, "invalid_json", ex.Message, engine);
            }
            catch (RankerException ex)
            {
                Console.Error.WriteLine($"[ranker] {ex.ErrorCode}: {ex.Message}");
                response = Error(request?.RequestId, ex.ErrorCode, ex.Message, engine);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ranker] unexpected: {ex}");
                response = Error(request?.RequestId, "internal_error", ex.Message, engine);
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, RankerProtocolV2.JsonOptions));
            if (shouldExit)
            {
                break;
            }
        }

        Console.Error.WriteLine("[ranker] stopped");
        return 0;
    }

    private static async Task<int> RunSmokeAsync(
        RankerEngine engine,
        TextWriter output)
    {
        const string requestId = "smoke";
        var candidates = new[]
        {
            "a financial institution that accepts deposits and lends money",
            "the land alongside or sloping down to a river or lake",
            "a stock of something available for use when required",
            "a long seat for several people"
        };
        var candidateGroupIds = new[] { 10, 20, 30, 40 };

        try
        {
            var result = await engine.SelectAsync(new RankerRequest
            {
                Command = RankerProtocolV2.Select,
                RequestId = requestId,
                Context = "He sat on the bank of the river.",
                Target = "bank",
                CharacterIndex = 14,
                SourceLanguage = "English",
                Candidates = candidates,
                CandidateGroupIds = candidateGroupIds
            });
            if (result.Abstained || result.Index != 20)
            {
                throw new RankerException(
                    "smoke_invalid_selection",
                    $"Smoke selection expected stable group 20, received " +
                    $"index={result.Index?.ToString() ?? "null"}, abstained={result.Abstained}, " +
                    $"margin={result.Margin:F4}.");
            }

            var confusionResult = await engine.SelectAsync(new RankerRequest
            {
                Command = RankerProtocolV2.Select,
                RequestId = requestId,
                Context = "He felt like he was teetering on the verge of utter confusion.",
                Target = "confusion",
                CharacterIndex = 52,
                SourceLanguage = "English",
                Candidates =
                [
                    "A lack of clarity or order.",
                    "The state of being confused; misunderstanding."
                ],
                CandidateGroupIds = [10, 20]
            });
            if (confusionResult.Abstained || confusionResult.Index != 20)
            {
                throw new RankerException(
                    "smoke_confidence_regression",
                    $"Contextual confidence regression expected stable group 20, received " +
                    $"index={confusionResult.Index?.ToString() ?? "null"}, " +
                    $"abstained={confusionResult.Abstained}, " +
                    $"margin={confusionResult.Margin:F4}.");
            }

            var response = Success(requestId, engine) with
            {
                Index = result.Index,
                Abstained = result.Abstained,
                Margin = result.Margin,
                Score = result.Score,
                Confidence = result.Confidence
            };
            await output.WriteLineAsync(
                JsonSerializer.Serialize(response, RankerProtocolV2.JsonOptions));
            Console.Error.WriteLine(
                $"[ranker] smoke passed; index={result.Index}; " +
                $"confusionMargin={confusionResult.Margin:F4}; " +
                "selection and calibrated-confidence checks completed");
            return 0;
        }
        catch (RankerException ex)
        {
            Console.Error.WriteLine($"[ranker] smoke failed: {ex.ErrorCode}: {ex.Message}");
            await output.WriteLineAsync(
                JsonSerializer.Serialize(
                    Error(requestId, ex.ErrorCode, ex.Message, engine),
                    RankerProtocolV2.JsonOptions));
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ranker] smoke failed: {ex}");
            await output.WriteLineAsync(
                JsonSerializer.Serialize(
                    Error(requestId, "internal_error", ex.Message, engine),
                    RankerProtocolV2.JsonOptions));
            return 1;
        }
    }

    private static async Task<(RankerResponse Response, bool ShouldExit)> HandleAsync(
        RankerEngine engine,
        RankerRequest request)
    {
        ValidateEnvelope(request);
        switch (request.Command.Trim().ToLowerInvariant())
        {
            case RankerProtocolV2.Hello:
                return (Success(request.RequestId, engine), false);

            case RankerProtocolV2.Warm:
                await engine.WarmAsync();
                return (Success(request.RequestId, engine), false);

            case RankerProtocolV2.Select:
                var result = await engine.SelectAsync(request);
                return (Success(request.RequestId, engine) with
                {
                    Index = result.Index,
                    Abstained = result.Abstained,
                    Margin = result.Margin,
                    Score = result.Score,
                    Confidence = result.Confidence
                }, false);

            case RankerProtocolV2.Shutdown:
                return (Success(request.RequestId, engine), true);

            default:
                throw new RankerException("unknown_command", $"Unknown command '{request.Command}'.");
        }
    }

    private static void ValidateEnvelope(RankerRequest request)
    {
        if (!string.Equals(request.Version, RankerProtocolV2.Version, StringComparison.Ordinal))
        {
            throw new RankerException(
                "unsupported_protocol",
                $"Protocol version '{request.Version}' is not supported.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new RankerException("missing_request_id", "requestId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new RankerException("missing_command", "command is required.");
        }
    }

    private static RankerResponse Success(string requestId, RankerEngine engine) => new()
    {
        RequestId = requestId,
        Ok = true,
        Provider = engine.Provider,
        Device = engine.Device,
        ModelVersion = engine.ModelVersion
    };

    private static RankerResponse Error(
        string? requestId,
        string errorCode,
        string error,
        RankerEngine engine) => new()
    {
        RequestId = requestId ?? string.Empty,
        Ok = false,
        Provider = engine.Provider,
        Device = engine.Device,
        ModelVersion = engine.ModelVersion,
        ErrorCode = errorCode,
        Error = error
    };
}
