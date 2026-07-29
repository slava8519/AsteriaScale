using System.Text.Json;
using Tokenizers.DotNet;

namespace AsteriaScale.RankerWorker;

internal interface IConsecPieceTokenizer
{
    IReadOnlyList<long> EncodePiece(string text);

    long ClassifierTokenId { get; }

    long SeparatorTokenId { get; }

    long DefinitionMarkerTokenId { get; }
}

internal sealed class ConsecTokenizer : IConsecPieceTokenizer, IDisposable
{
    private const long ExpectedClassifierTokenId = 1;
    private const long ExpectedSeparatorTokenId = 2;
    private const long ExpectedDefinitionMarkerTokenId = 50_267;
    private readonly Tokenizer _tokenizer;

    internal ConsecTokenizer(string tokenizerPath, byte[] authenticatedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        ArgumentNullException.ThrowIfNull(authenticatedJson);
        ValidateAuthenticatedTokenizer(authenticatedJson);
        try
        {
            _tokenizer = new Tokenizer(tokenizerPath);
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "consec_tokenizer_load_failed",
                $"Could not load the authenticated ConSeC tokenizer: {ex.Message}",
                ex);
        }
    }

    public long ClassifierTokenId => ExpectedClassifierTokenId;

    public long SeparatorTokenId => ExpectedSeparatorTokenId;

    public long DefinitionMarkerTokenId => ExpectedDefinitionMarkerTokenId;

    public IReadOnlyList<long> EncodePiece(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RankerException(
                "consec_invalid_input",
                "ConSeC cannot encode an empty sentence token or definition.");
        }

        uint[] encoded;
        try
        {
            // The upstream tokenizer sets add_prefix_space=true at the ConSeC
            // wrapper layer, while the pinned tokenizer.json intentionally
            // leaves ByteLevel.add_prefix_space=false. Prefix exactly once.
            encoded = _tokenizer.Encode(" " + text.Trim());
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "consec_tokenization_failed",
                $"The authenticated ConSeC tokenizer rejected an input: {ex.Message}",
                ex);
        }

        if (encoded.Length < 3 ||
            encoded[0] != ExpectedClassifierTokenId ||
            encoded[^1] != ExpectedSeparatorTokenId)
        {
            throw new RankerException(
                "consec_tokenizer_incompatible",
                "The ConSeC tokenizer did not emit the pinned [CLS] text [SEP] layout.");
        }

        return encoded.AsSpan(1, encoded.Length - 2)
            .ToArray()
            .Select(id => (long)id)
            .ToArray();
    }

    private static void ValidateAuthenticatedTokenizer(byte[] json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out JsonElement version) ||
                !string.Equals(version.GetString(), "1.0", StringComparison.Ordinal) ||
                !root.TryGetProperty("truncation", out JsonElement truncation) ||
                truncation.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("padding", out JsonElement padding) ||
                padding.ValueKind != JsonValueKind.Null)
            {
                throw Incompatible(
                    "tokenizer must be unpadded and untruncated schema 1.0");
            }

            JsonElement preTokenizer = root.GetProperty("pre_tokenizer");
            if (!HasString(preTokenizer, "type", "ByteLevel") ||
                !HasBoolean(preTokenizer, "add_prefix_space", false) ||
                !HasBoolean(preTokenizer, "use_regex", true))
            {
                throw Incompatible("the pinned ByteLevel pre-tokenizer changed");
            }

            JsonElement postProcessor = root.GetProperty("post_processor");
            if (!HasString(postProcessor, "type", "TemplateProcessing"))
            {
                throw Incompatible("the pinned TemplateProcessing layout changed");
            }

            JsonElement model = root.GetProperty("model");
            if (!HasString(model, "type", "BPE"))
            {
                throw Incompatible("the pinned BPE model changed");
            }

            var required = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["[PAD]"] = 0,
                ["[CLS]"] = ExpectedClassifierTokenId,
                ["[SEP]"] = ExpectedSeparatorTokenId,
                ["[UNK]"] = 3,
                ["<d>"] = 50_265,
                ["</d>"] = 50_266,
                ["DEF_SEP"] = ExpectedDefinitionMarkerTokenId,
                ["DEF_END"] = 50_268,
                ["CONTEXT_DEFS"] = 50_269
            };
            var observed = new Dictionary<string, long>(StringComparer.Ordinal);
            JsonElement addedTokens = root.GetProperty("added_tokens");
            if (addedTokens.ValueKind != JsonValueKind.Array)
            {
                throw Incompatible("added_tokens is not an array");
            }

            foreach (JsonElement token in addedTokens.EnumerateArray())
            {
                if (token.ValueKind != JsonValueKind.Object ||
                    !token.TryGetProperty("content", out JsonElement content) ||
                    content.ValueKind != JsonValueKind.String ||
                    !token.TryGetProperty("id", out JsonElement id) ||
                    !id.TryGetInt64(out long tokenId))
                {
                    throw Incompatible("an added token record is malformed");
                }

                string value = content.GetString()!;
                if (required.ContainsKey(value) && !observed.TryAdd(value, tokenId))
                {
                    throw Incompatible($"special token '{value}' is duplicated");
                }
            }

            foreach ((string token, long expectedId) in required)
            {
                if (!observed.TryGetValue(token, out long actualId) ||
                    actualId != expectedId)
                {
                    throw Incompatible(
                        $"special token '{token}' has id {actualId}, expected {expectedId}");
                }
            }
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new RankerException(
                "consec_tokenizer_incompatible",
                $"The authenticated ConSeC tokenizer is invalid: {ex.Message}",
                ex);
        }
    }

    private static bool HasString(
        JsonElement element,
        string property,
        string expected) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool HasBoolean(
        JsonElement element,
        string property,
        bool expected) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean() == expected;

    private static RankerException Incompatible(string detail) => new(
        "consec_tokenizer_incompatible",
        $"The authenticated ConSeC tokenizer is incompatible: {detail}.");

    public void Dispose() => _tokenizer.Dispose();
}
