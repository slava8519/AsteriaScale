using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AsteriaScale.Services;
using Drawing = System.Drawing;

/// <summary>
/// The Apple Vision observations of protocol v1 (roadmap 20.3; docs/MACOS.md
/// T18-T20, T27, T28, T32, T39), checked inside the existing macOS-only
/// registrations: "macOS: CoreHost protocol conformance" runs the stateless
/// part (ocr.select against direct OcrGeometry and OcrTextRules calls, the
/// decoder's ExtractRecognition rules, schema refusals, the T32 game key, the
/// fake engine's hover.loading and stale_frame, the guards of what CoreHost
/// copies from WindowsAiOcrService and the pinned frame fingerprints), and
/// "macOS: CoreHost answers a hover through Core" the Core-backed part
/// (ocr.plan, observations hovers in both modes, the frame cache, history,
/// and ocr.plan behind a held-back settings.update but not behind a running
/// hover). The test assembly does not reference CoreHost, so the decoder is
/// checked from outside: ocr.select's lines and selection against a
/// hand-built OcrRecognition, and its source text for the one rule that
/// cannot be seen from outside.
/// </summary>
internal static class CoreHostObservationTests
{
    internal const string DictionaryLabel = "APPLE VISION  •  СЛОВАРЬ";
    private const string StaleFrameMessage = "Кадр на экране изменился.";
    private const string RiverLine = "He sat on the bank of the river.";
    private const string WindowsOcrSource = "src/AsteriaScale/Services/WindowsAiOcrService.cs";
    private const string DecoderSource = "src/AsteriaScale.CoreHost/Host/Observations.cs";
    private const string DecoderCopy = DecoderSource + " (ObservationDecoder.Decode)";
    private const string DictionaryCaptureCopy =
        DecoderSource + " (ObservationRules.DictionaryCaptureWidth and DictionaryCaptureHeight)";

    // A 4K display and the smart capture ocr.plan gives for a cursor at
    // (1925, 1430) on it (2400x720 around the cursor, inside the display).
    private static readonly Scene Smart4K = new(7, 3840, 2160, new Drawing.Rectangle(725, 1070, 2400, 720));

    // The Sakuya layout of "Neural OCR wrapped sentence" in frame pixels.
    private const string FirstDialogue = "\"That is of little significance. More importantly, you";
    private const string SecondDialogue = "should be able to enter this establishment for no charge";
    private const string ThirdDialogue = "with these people.\"";
    // The sentence under «able» that Core assembles from the three lines.
    private const string SakuyaSentence =
        "More importantly, you should be able to enter this establishment for no charge with these people.\"";
    private static readonly Line Back = Cut("Back     Skip     Close", 250, 535, 1950, 570, decoded: "Back Skip Close");

    private static Line Nameplate(string name) => Cut(name, 155, 70, 390, 125);

    private static Line[] Sakuya(string? nameplate = "Sakuya") =>
    [
        .. nameplate is null ? Array.Empty<Line>() : [Nameplate(nameplate)],
        Cut(FirstDialogue, 135, 190, 1800, 238),
        Cut(SecondDialogue, 136, 270, 1900, 320),
        Cut(ThirdDialogue, 134, 350, 850, 400),
        Back,
    ];

    // The frame after the Sakuya one changed: another sentence under the
    // cursor, the nameplate kept.
    private static Line[] Changed() =>
    [
        Nameplate("Sakuya"),
        Cut("You will lead the", 120, 190, 690, 238),
        Cut("rest of your life tortured by paranoia, constantly", 136, 270, 1900, 320),
        Back,
    ];

    // ---- the copy guards and the pinned fingerprints -----------------------

    /// <summary>
    /// CoreHost copies two things from WindowsAiOcrService instead of moving
    /// them into Core (which would change Core.dll): ExtractRecognition's
    /// filters (T28) and the dictionary capture size (T39). The Windows source
    /// is read as text (it is not compiled here): any change to
    /// ExtractRecognition, or to RecognizeNearCursorAsync's 900x340 capture,
    /// fails and names the CoreHost copy to review with it. The copy itself is
    /// read too, because one of its rules (the word's Normalize) cannot be
    /// observed from outside: SelectNearPoint normalizes the hovered word
    /// again.
    /// </summary>
    public static void VerifyWindowsCopies()
    {
        string[] expected =
        [
            "private static OcrRecognition ExtractRecognition(RecognizedText recognized)",
            "{",
            "List<OcrLineWords> lines = [];",
            "var recognizedLines = recognized.Lines;",
            "if (recognizedLines is null)",
            "{",
            "return new OcrRecognition(lines);",
            "}",
            "foreach (var line in recognizedLines)",
            "{",
            "if (line?.Words is null)",
            "{",
            "continue;",
            "}",
            "List<OcrWordGeometry> words = [];",
            "foreach (var word in line.Words.Where(word => word is not null))",
            "{",
            "var boundingBox = word.BoundingBox;",
            "(double left, double top, double right, double bottom) = BoundsOf(boundingBox);",
            "if (right <= left || bottom <= top)",
            "{",
            "continue;",
            "}",
            "words.Add(new OcrWordGeometry(",
            "Normalize(word.Text),",
            "left,",
            "top,",
            "right,",
            "bottom,",
            "word.MatchConfidence));",
            "}",
            "string text = NormalizeLong(line.Text, 1200);",
            "if (words.Count > 0 && text.Length > 0)",
            "{",
            "lines.Add(new OcrLineWords(text, words));",
            "}",
            "}",
            "return new OcrRecognition(lines);",
            "}",
        ];
        string[] windows = File.ReadAllLines(Path.Combine(MacSmoke.FindRepositoryRoot(), WindowsOcrSource));
        string[] body = MethodLines(windows, expected[0], WindowsOcrSource, DecoderCopy);
        int differs = Enumerable.Range(0, Math.Max(body.Length, expected.Length))
            .FirstOrDefault(index => index >= body.Length || index >= expected.Length || body[index] != expected[index], -1);
        if (differs >= 0)
        {
            CoreHostFixtures.Check(false,
                $"ExtractRecognition in {WindowsOcrSource} changed at its line {differs + 1} " +
                $"(«{(differs < body.Length ? body[differs] : "end")}», was «{(differs < expected.Length ? expected[differs] : "end")}»). " +
                $"Update its copy in {DecoderCopy} to match, then this expectation (docs/MACOS.md T28).");
        }

        // The decoder keeps each filter statement, in ExtractRecognition's order.
        string[] decoder = MethodLines(
            File.ReadAllLines(Path.Combine(MacSmoke.FindRepositoryRoot(), DecoderSource)),
            "public static OcrRecognition Decode(ObservedFrame frame)",
            DecoderSource,
            DecoderCopy);
        int after = 0;
        foreach ((string original, string copy) in new[]
                 {
                     ("if (right <= left || bottom <= top)", "if (word.Right <= word.Left || word.Bottom <= word.Top)"),
                     ("Normalize(word.Text),", "OcrTextRules.Normalize(word.Text),"),
                     ("string text = NormalizeLong(line.Text, 1200);", "string text = OcrTextRules.NormalizeLong(line.Text, 1200);"),
                     ("if (words.Count > 0 && text.Length > 0)", "if (words.Count > 0 && text.Length > 0)"),
                     ("lines.Add(new OcrLineWords(text, words));", "lines.Add(new OcrLineWords(text, words));"),
                 })
        {
            int found = Array.IndexOf(decoder, copy, after);
            CoreHostFixtures.Check(expected.Contains(original) && found >= 0,
                $"{DecoderCopy} no longer copies ExtractRecognition's «{original}» as «{copy}» " +
                $"after its line {after} (docs/MACOS.md T28).");
            after = found + 1;
        }

        // ocr.plan's dictionary capture is WindowsAiOcrService's (T39).
        string[] recognize = MethodLines(windows, "internal async Task<HoverOcrResult?> RecognizeNearCursorAsync(",
            WindowsOcrSource, DictionaryCaptureCopy);
        CoreHostFixtures.Check(recognize.Contains(": CaptureAround(cursor, 900, 340);"),
            $"RecognizeNearCursorAsync in {WindowsOcrSource} no longer captures «: CaptureAround(cursor, 900, 340);» " +
            $"in dictionary mode. Update {DictionaryCaptureCopy} and the 900x340 this check expects of ocr.plan " +
            "to match (docs/MACOS.md T39).");
    }

    // The trimmed, non-empty lines of a method from its signature line to the
    // brace that closes its body.
    private static string[] MethodLines(string[] source, string signature, string file, string copy)
    {
        int start = Array.FindIndex(source, line => line.Trim() == signature);
        CoreHostFixtures.Check(start >= 0,
            $"{file} no longer declares «{signature}»; {copy} copies from it (docs/MACOS.md T28, T39).");
        List<string> body = [];
        int depth = 0;
        bool opened = false;
        for (int index = start; index < source.Length; index++)
        {
            string line = source[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            body.Add(line);
            depth += line.Count(character => character == '{') - line.Count(character => character == '}');
            opened |= depth > 0;
            if (opened && depth == 0)
            {
                break;
            }
        }

        return [.. body];
    }

    /// <summary>
    /// frame-fingerprint-v1.txt: Core's ComputeFrameFingerprint of each
    /// pattern is the pinned value, and the wire digits are the first 8
    /// SHA-256 bytes in reverse order (the little-endian ulong as x16), as the
    /// file's header says. The Swift FrameFingerprint test that comes with the
    /// app's screen capture is to read the same file.
    /// </summary>
    public static void VerifyFrameFingerprintFixture()
    {
        string path = CoreHostFixtures.GoldenPath("frame-fingerprint-v1.txt");
        CoreHostFixtures.Check(!File.ReadAllText(path).Contains('\r'), "frame-fingerprint-v1.txt must use LF line ends.");
        string[][] entries = File.ReadAllLines(path)
            .Where(line => line.Length != 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .ToArray();
        CoreHostFixtures.Check(entries.Length >= 5 && entries.Any(entry => entry is ["empty", "0", "zero", "141cfc9842c4b0e3"]),
            "frame-fingerprint-v1.txt lost its patterns or the documented empty-input value.");
        foreach (string[] entry in entries)
        {
            CoreHostFixtures.Check(
                entry.Length == 4 && int.TryParse(entry[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                entry[2] is "zero" or "ramp" or "opaque" or "mix" &&
                entry[3].Length == 16 && entry[3].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "frame-fingerprint-v1.txt has a malformed line: " + string.Join(' ', entry));
            int count = int.Parse(entry[1], CultureInfo.InvariantCulture);
            byte[] bytes = new byte[count];
            for (int index = 0; index < count; index++)
            {
                bytes[index] = entry[2] switch
                {
                    "zero" => 0,
                    "ramp" => (byte)(index % 256),
                    "opaque" => (byte)(index % 4 == 3 ? 255 : 0),
                    _ => (byte)((index * 31L + 7) % 251),
                };
            }

            string fingerprint = OcrTextRules.ComputeFrameFingerprint(bytes).ToString("x16", CultureInfo.InvariantCulture);
            byte[] prefix = SHA256.HashData(bytes)[..8];
            Array.Reverse(prefix);
            string reversed = Convert.ToHexStringLower(prefix);
            CoreHostFixtures.Check(fingerprint == entry[3] && reversed == entry[3],
                $"The frame fingerprint of {entry[0]} is {fingerprint} (reversed digest prefix {reversed}), " +
                $"frame-fingerprint-v1.txt pins {entry[3]}.");
        }
    }

    // ---- stateless: ocr.select, the decoder, refusals ---------------------

    /// <summary>
    /// Runs before core.init and before any hover engine: ocr.select needs
    /// neither, and every refusal is decided at admission.
    /// </summary>
    public static async Task VerifyStatelessAsync(CoreHostClient client)
    {
        await VerifySelectMatchesCoreAsync(client);
        await VerifyDecoderAsync(client);
        await VerifyRefusalsAsync(client);
    }

    // ocr.select on the Sakuya layout equals Core's own calls on the same
    // records: SelectNearPoint (radius, weight, nearest word and its index,
    // null in the void) in dictionary mode, StabilizeSentenceContext over one
    // or two observations in smart mode.
    private static async Task VerifySelectMatchesCoreAsync(CoreHostClient client)
    {
        Line[] sakuya = Sakuya();
        OcrRecognition recognition = Recognition(sakuya);
        foreach ((int x, int y, string? expected) in new (int, int, string?)[]
                 {
                     (520, 295, $"{SecondDialogue}|able|12|True|-"),
                     (920, 380, $"{ThirdDialogue}|people.\"|18|True|-"),
                     (580, 295, $"{SecondDialogue}|able|13|True|-"),
                     (1995, 295, $"{SecondDialogue}|charge|55|True|-"),
                     (1996, 295, null),
                 })
        {
            Drawing.Point point = new(x, y);
            JsonObject result = await SelectAsync(client, "dictionary", Smart4K.Source(point, [Frame("00000000000000a1", sakuya)]));
            string direct = Shown(OcrGeometry.SelectNearPoint(recognition, x, y, Smart4K.Bounds, sentenceContext: false));
            // An empty selection is an explicit null, never a missing member.
            CoreHostFixtures.Check(
                direct == (expected ?? "null") && result.ContainsKey("selection") && Shown(result["selection"]) == direct,
                $"ocr.select in dictionary mode at ({x}, {y}) differs from OcrGeometry.SelectNearPoint: " +
                $"{result.ToJsonString()} against {direct}.");
            CoreHostFixtures.Check(
                result["lines"]!.ToJsonString() == new JsonArray(recognition.Lines.Select(line => (JsonNode)line.Text).ToArray()).ToJsonString(),
                "ocr.select does not list the decoded lines of observation 1: " + result["lines"]!.ToJsonString());
        }

        Drawing.Point able = new(520, 295);
        HoverOcrResult? first = Near(recognition, able, sentenceContext: true);
        // Two that diverge keep the first observation's speaker: Core's
        // StabilizeSentenceContext returns first with IsSentenceContextStable
        // false, as on Windows, and CoreHost mirrors Core (DoD 20.3 item 4).
        // A Core change to that shows up here.
        (string Label, Line[]? Second, string Expected)[] smart =
        [
            ("one observation", null, Shown(OcrTextRules.StabilizeSentenceContext(first, null, out _))),
            ("two that agree", sakuya, $"{SakuyaSentence}|able|34|True|Sakuya"),
            ("nameplates that disagree", Sakuya("Sakuyo"), $"{SakuyaSentence}|able|34|True|-"),
            ("two that diverge", Changed(), $"{SakuyaSentence}|able|34|False|Sakuya"),
        ];
        foreach ((string label, Line[]? second, string expected) in smart)
        {
            JsonObject[] frames = second is null
                ? [Frame("00000000000000a1", sakuya)]
                : [Frame("00000000000000a1", sakuya), Frame("00000000000000a2", second)];
            JsonObject result = await SelectAsync(client, "smart", Smart4K.Source(able, frames));
            string direct = Shown(OcrTextRules.StabilizeSentenceContext(
                first,
                second is null ? null : Near(Recognition(second), able, sentenceContext: true),
                out _));
            CoreHostFixtures.Check(direct == expected && Shown(result["selection"]) == direct,
                $"ocr.select in smart mode with {label} differs from StabilizeSentenceContext: " +
                $"{Shown(result["selection"])} against {direct} (expected {expected}).");
        }
    }

    // The decoder applies ExtractRecognition's rules (T28): a degenerate word
    // box is dropped (here two of them sit under the cursor before «bank», so
    // keeping either would select it), a line left without words or with
    // empty text is dropped, and a line's text is collapsed and cut to 1200
    // characters by NormalizeLong. ocr.select shows both the lines and the
    // selection, and both must equal Core's calls on the hand-built
    // OcrRecognition those rules give. «bank» arrives with spaces round it
    // and leaves as Windows gives it; that the decoder's own Normalize does
    // it is pinned by VerifyWindowsCopies, since SelectNearPoint normalizes
    // the hovered word again.
    private static async Task VerifyDecoderAsync(CoreHostClient client)
    {
        Line river = Cut(RiverLine, 100, 150, 740, 180);
        int bank = Array.FindIndex(river.Words, word => word.Text == "bank");
        Word[] withDegenerate =
        [
            .. river.Words[..bank],
            new Word("zzz", 420, 150, 420, 180),
            new Word("yyy", 410, 172, 430, 160),
            river.Words[bank] with { Text = "  bank " },
            .. river.Words[(bank + 1)..],
        ];
        string longText = string.Join(' ', Enumerable.Repeat("word", 260));
        Line[] sent =
        [
            river with { Words = withDegenerate },
            new Line("Ghost line", [new Word("Ghost", 100, 300, 100, 330)]),
            new Line("   ", [new Word("x", 100, 360, 120, 390)]),
            new Line(longText, [new Word("word", 100, 600, 1300, 630)]),
            Back,
        ];
        OcrRecognition decoded = new(
        [
            Recognition([river]).Lines[0],
            new OcrLineWords(longText[..1200], [new OcrWordGeometry("word", 100, 600, 1300, 630, 1)]),
            Recognition([Back]).Lines[0],
        ]);
        Drawing.Point cursor = new(420, 165);
        foreach (string mode in new[] { "dictionary", "smart" })
        {
            JsonObject result = await SelectAsync(client, mode, Smart4K.Source(cursor, [Frame("00000000000000b1", sent)]));
            string[] lines = result["lines"]!.AsArray().Select(line => line!.GetValue<string>()).ToArray();
            CoreHostFixtures.Check(lines.SequenceEqual(decoded.Lines.Select(line => line.Text)),
                "The decoder did not keep exactly ExtractRecognition's lines: " +
                string.Join(" | ", lines.Select(line => line.Length > 40 ? line[..40] + $"…({line.Length})" : line)));
            HoverOcrResult? direct = mode == "dictionary"
                ? Near(decoded, cursor, sentenceContext: false)
                : OcrTextRules.StabilizeSentenceContext(Near(decoded, cursor, sentenceContext: true), null, out _);
            CoreHostFixtures.Check(direct?.HoveredWord == "bank" && Shown(result["selection"]) == Shown(direct),
                $"The decoder in {mode} mode selected {Shown(result["selection"])}, Core on ExtractRecognition's lines {Shown(direct)}.");
        }
    }

    private static async Task VerifyRefusalsAsync(CoreHostClient client)
    {
        Drawing.Point able = new(520, 295);
        JsonObject Valid() => Smart4K.Source(able, [Frame("00000000000000c1", Sakuya())]);

        // Accepted at the limits.
        foreach ((string label, Action<JsonObject> edit) in Edits(
                     ("display id 4294967295", source => source["display"]!["id"] = 4294967295L),
                     ("a 1x1 capture at the display's corner", source =>
                     {
                         source["display"] = Display(7, 1, 1);
                         source["captureBounds"] = Rectangle(new Drawing.Rectangle(0, 0, 1, 1));
                         source["cursor"] = Point(0, 0);
                         source["observations"] = new JsonArray(Frame("00000000000000c1", [new Line("a", [new Word("a", -1, -1, 2, 2)])]));
                     }),
                     ("a 32768-pixel display", source => source["display"]!["width"] = 32768),
                     ("a cursor outside the capture", source => source["cursor"] = Point(10, 10)),
                     ("256 lines", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         Enumerable.Range(0, 256).Select(index => new Line("a", [new Word("a", 1, 1, 2, 2)]))))),
                     ("256 words in a line and 4096 in all", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         Enumerable.Range(0, 16).Select(_ => new Line("a", Enumerable.Range(0, 256).Select(_ => new Word("a", 1, 1, 2, 2)).ToArray()))))),
                     ("a 4000-character line and a 256-character word", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         [new Line(new string('a', 4000), [new Word(new string('a', 256), 1, 1, 2, 2)])]))),
                     ("box edges one pixel outside the frame", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         [new Line("a", [new Word("a", -1, -1, 2401, 721)])]))),
                     ("a degenerate box", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         [new Line("a", [new Word("a", 5, 5, 2, 2)])]))),
                     ("confidence 0", source => source["observations"]![0]!["lines"]![0]!["confidence"] = 0.0)))
        {
            JsonObject source = Valid();
            edit(source);
            JsonObject terminal = await client.AwaitAsync(
                client.Send("ocr.select", CoreHostFixtures.Payload(("mode", "dictionary"), ("source", source))), 10_000);
            CoreHostFixtures.Check(terminal["ok"]!.GetValue<bool>(),
                $"ocr.select with {label} must be accepted: {terminal.ToJsonString()}");
        }

        // Refused: each limit, the counts per mode, the fingerprint form.
        foreach ((string label, Action<JsonObject> edit) in Edits(
                     ("a negative display id", source => source["display"]!["id"] = -1),
                     ("display id 4294967296", source => source["display"]!["id"] = 4294967296L),
                     ("a display 0 pixels wide", source => source["display"]!["width"] = 0),
                     ("a display 32769 pixels high", source => source["display"]!["height"] = 32769),
                     ("a capture past the display's right edge", source => source["captureBounds"]!["x"] = 1441),
                     ("a capture above the display", source => source["captureBounds"]!["y"] = -1),
                     ("an empty capture", source => source["captureBounds"]!["width"] = 0),
                     ("a fractional capture", source => source["captureBounds"]!["x"] = 725.5),
                     ("a cursor at the display's width", source => source["cursor"] = Point(3840, 10)),
                     ("a negative cursor", source => source["cursor"] = Point(10, -1)),
                     ("an uppercase fingerprint", source => source["observations"]![0]!["fingerprint"] = "00000000000000C1"),
                     ("a 15-digit fingerprint", source => source["observations"]![0]!["fingerprint"] = "0000000000000c1"),
                     ("a 17-digit fingerprint", source => source["observations"]![0]!["fingerprint"] = "000000000000000c1"),
                     ("a fingerprint that is not hex", source => source["observations"]![0]!["fingerprint"] = "00000000000000g1"),
                     ("257 lines", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         Enumerable.Range(0, 257).Select(_ => new Line("a", [new Word("a", 1, 1, 2, 2)]))))),
                     ("a line without words", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         [new Line("a", [])]))),
                     ("257 words in a line", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         [new Line("a", Enumerable.Range(0, 257).Select(_ => new Word("a", 1, 1, 2, 2)).ToArray())]))),
                     ("4097 words in all", source => source["observations"] = new JsonArray(Frame("00000000000000c1",
                         Enumerable.Range(0, 17).Select(index => new Line("a",
                             Enumerable.Range(0, index == 16 ? 1 : 256).Select(_ => new Word("a", 1, 1, 2, 2)).ToArray()))))),
                     ("a 4001-character line", source => source["observations"]![0]!["lines"]![0]!["text"] = new string('a', 4001)),
                     ("an empty word", source => source["observations"]![0]!["lines"]![0]!["words"]![0]!["text"] = ""),
                     ("a 257-character word", source => source["observations"]![0]!["lines"]![0]!["words"]![0]!["text"] = new string('a', 257)),
                     ("a negative confidence", source => source["observations"]![0]!["lines"]![0]!["confidence"] = -0.01),
                     ("a confidence over 1", source => source["observations"]![0]!["lines"]![0]!["confidence"] = 1.01),
                     ("a box left of the frame", source => source["observations"]![0]!["lines"]![0]!["words"]![0]!["left"] = -1.01),
                     ("a box below the frame", source => source["observations"]![0]!["lines"]![0]!["words"]![0]!["bottom"] = 721.01),
                     ("a box right of the frame", source => source["observations"]![0]!["lines"]![0]!["words"]![0]!["right"] = 2402),
                     ("an unknown member", source => source["observations"]![0]!["angle"] = 0),
                     ("a selection kind", source => source["kind"] = "selection"),
                     ("a cached fingerprint", source => source["cachedFingerprint"] = "00000000000000c1")))
        {
            JsonObject source = Valid();
            edit(source);
            await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("mode", "dictionary"), ("source", source)), label);
        }

        // A null array element passes the strict wire types (they refuse null
        // members only) and must still be bad_request at admission, never a
        // failed reader: for both commands, CoreHost answers and stays up.
        foreach ((string label, Action<JsonObject> edit) in Edits(
                     ("a null observation", source => source["observations"] = new JsonArray((JsonNode?)null)),
                     ("a null line", source => source["observations"]![0]!["lines"] = new JsonArray((JsonNode?)null)),
                     ("a null word", source => source["observations"]![0]!["lines"]![0]!["words"] = new JsonArray((JsonNode?)null))))
        {
            foreach (string command in new[] { "ocr.select", "hover.resolve" })
            {
                JsonObject source = Valid();
                edit(source);
                await ExpectBadRequestAsync(client, command, CoreHostFixtures.Payload(("mode", "dictionary"), ("source", source)), label);
            }
        }

        await client.ResultAsync(client.Send("core.ping"));

        // Non-finite numbers exist in JSON only as out-of-range literals: a
        // marked value in a valid source is replaced in the raw line.
        JsonObject infiniteEdge = Valid();
        infiniteEdge["observations"]![0]!["lines"]![0]!["words"]![0]!["left"] = 159.25;
        await ExpectRawBadRequestAsync(client, "ocr.select",
            CoreHostFixtures.Payload(("mode", "dictionary"), ("source", infiniteEdge)), "\"left\":159.25", "\"left\":1e999", "an infinite box edge");
        JsonObject infiniteConfidence = Valid();
        infiniteConfidence["observations"]![0]!["lines"]![0]!["confidence"] = 0.25;
        await ExpectRawBadRequestAsync(client, "ocr.select",
            CoreHostFixtures.Payload(("mode", "dictionary"), ("source", infiniteConfidence)), "\"confidence\":0.25", "\"confidence\":-1e999", "an infinite confidence");

        // The counts per mode, and mode itself.
        JsonObject one = Frame("00000000000000c1", Sakuya());
        JsonObject Source(int count, string? cached = null) => Smart4K.Source(able,
            Enumerable.Range(0, count).Select(_ => (JsonObject)one.DeepClone()).ToArray(), cached);
        await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("source", Source(1))), "ocr.select without mode");
        await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("mode", "Smart"), ("source", Source(1))), "ocr.select in mode Smart");
        await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("mode", "dictionary"), ("source", Source(2))), "ocr.select with 2 dictionary observations");
        await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("mode", "smart"), ("source", Source(0))), "ocr.select without observations");
        await ExpectBadRequestAsync(client, "ocr.select", CoreHostFixtures.Payload(("mode", "smart"), ("source", Source(3))), "ocr.select with 3 observations");
        await client.ResultAsync(client.Send("ocr.select", CoreHostFixtures.Payload(("mode", "smart"), ("source", Source(2)))));
        foreach ((string label, string? mode, JsonObject source) in new (string, string?, JsonObject)[]
                 {
                     ("an observations hover without mode", null, Source(1)),
                     ("a dictionary hover with 2 observations", "dictionary", Source(2)),
                     ("a dictionary hover with 0 observations", "dictionary", Source(0, "00000000000000c1")),
                     ("a dictionary hover with a cached fingerprint", "dictionary", Source(1, "00000000000000c1")),
                     ("a smart hover with 1 observation", "smart", Source(1)),
                     ("a smart hover with 3 observations", "smart", Source(3)),
                     ("a smart hover with 0 observations and no cached fingerprint", "smart", Source(0)),
                     ("a smart hover with 2 observations and a cached fingerprint", "smart", Source(2, "00000000000000c1")),
                     ("a smart hover with a bad cached fingerprint", "smart", Source(0, "00000000000000C1")),
                 })
        {
            JsonObject payload = CoreHostFixtures.Payload(("source", source));
            if (mode is not null)
            {
                payload["mode"] = mode;
            }

            await ExpectBadRequestAsync(client, "hover.resolve", payload, label);
        }

        // T32: a game key Core drops or changes on a second pass is refused at
        // admission for both source kinds; a valid hover meets the missing
        // engine (not_ready), so the check before it passed.
        JsonObject selection = CoreHostFixtures.Payload(("source", CoreHostFixtures.Payload(
            ("kind", "selection"), ("text", RiverLine), ("hoveredWord", "bank"), ("characterIndex", 14))));
        JsonObject observations = CoreHostFixtures.Payload(("mode", "smart"), ("source", Source(2)));
        foreach (JsonObject hover in new[] { selection, observations })
        {
            foreach (string key in new[] { "steam:42", "process:asteria-test.exe.exe", "process:asteria-test .exe", "path:/Games/Asteria Test.exe \"" })
            {
                JsonObject withGame = (JsonObject)hover.DeepClone();
                withGame["game"] = CoreHostFixtures.Payload(("key", key), ("displayName", "Asteria Test"));
                await ExpectBadRequestAsync(client, "hover.resolve", withGame, $"a {hover["source"]!["kind"]} hover with game.key {key}");
            }

            JsonObject valid = (JsonObject)hover.DeepClone();
            valid["game"] = CoreHostFixtures.Payload(("key", "process:asteria-test"), ("displayName", "Asteria Test"));
            await client.ErrorAsync(client.Send("hover.resolve", valid), "not_ready");
        }
    }

    // ---- the fake engine -----------------------------------------------------

    /// <summary>
    /// With test.fakeCore an observations hover is keyed by the first word of
    /// its first line and gets hover.loading before hover.primary; a selection
    /// hover keeps 20.2's events; zero observations are stale_frame, since the
    /// fake keeps no frame cache.
    /// </summary>
    public static async Task VerifyFakeEngineAsync(CoreHostClient client)
    {
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(("phrase", "none"))));
        CoreHostClient.Pending observed = client.Send("hover.resolve", CoreHostFixtures.Payload(
            ("mode", "dictionary"),
            ("source", Smart4K.Source(new Drawing.Point(520, 295), [Frame("00000000000000d1", Sakuya())]))));
        await client.ResultAsync(observed);
        JsonObject? loading = observed.Events.FirstOrDefault()?["payload"]?.AsObject();
        CoreHostFixtures.Check(
            Names(observed).SequenceEqual(["hover.loading", "hover.primary"]) &&
            loading is { Count: 2 } &&
            loading["source"]!.GetValue<string>() == "Sakuya" &&
            loading["engineLabel"]!.GetValue<string>() == "ПРОВЕРКА" &&
            observed.Events[1]["payload"]!["source"]!.GetValue<string>() == "Sakuya",
            "The fake engine's observations hover: " + string.Join(" ", observed.Events.Select(frame => frame.ToJsonString())));
        CoreHostClient.Pending selected = client.Send("hover.resolve", CoreHostFixtures.Payload(("source", CoreHostFixtures.Payload(
            ("kind", "selection"), ("text", RiverLine), ("hoveredWord", "bank"), ("characterIndex", 14)))));
        await client.ResultAsync(selected);
        CoreHostFixtures.Check(Names(selected).SequenceEqual(["hover.primary"]),
            "A selection hover got other events than hover.primary: " + string.Join(", ", Names(selected)));
        CoreHostClient.Pending stale = client.Send("hover.resolve", CoreHostFixtures.Payload(
            ("mode", "smart"),
            ("source", Smart4K.Source(new Drawing.Point(520, 295), [], "00000000000000d1"))));
        JsonObject error = await client.ErrorAsync(stale, "stale_frame");
        CoreHostFixtures.Check(error["message"]!.GetValue<string>() == StaleFrameMessage && stale.Events.Count == 0,
            "A hover with zero observations and no frame must be stale_frame without events: " + error.ToJsonString());
    }

    /// <summary>A valid ocr.select request, for the busy check.</summary>
    public static JsonObject SelectPayload() => CoreHostFixtures.Payload(
        ("mode", "dictionary"),
        ("source", Smart4K.Source(new Drawing.Point(520, 295), [Frame("00000000000000a1", Sakuya())])));

    // ---- through Core --------------------------------------------------------

    /// <summary>
    /// Its own launch without test commands: smart hovers without a ranker
    /// worker leave worker_missing in models.status, which the core-hover
    /// launch checks is clear.
    /// </summary>
    public static async Task VerifyThroughCoreAsync(string tempRoot, int initDeadlineMs, int hoverDeadlineMs)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "core-observations", testCommands: false);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);

        // Before core.init: ocr.plan and a hover are not_ready, ocr.select works.
        await client.ErrorAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, 1925, 1430)), "not_ready");
        await client.ErrorAsync(client.Send("hover.resolve", CoreHostFixtures.Payload(
            ("mode", "dictionary"),
            ("source", Smart4K.Source(new Drawing.Point(520, 295), [Frame("00000000000000e1", Sakuya())])))), "not_ready");
        await client.ResultAsync(client.Send("ocr.select", SelectPayload()));

        await client.ResultAsync(client.Send("core.init"), initDeadlineMs);
        // Translation is off on a first run.
        await client.ErrorAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, 1925, 1430)), "hover_disabled");
        // ocr.plan's own validation runs at admission after core.init, so it
        // wins over hover_disabled; each case is one step past a limit.
        foreach ((string label, JsonObject payload) in new (string, JsonObject)[]
                 {
                     ("display.id -1", Plan(-1, 3840, 2160, 1925, 1430)),
                     ("display.id 4294967296", Plan(4294967296L, 3840, 2160, 1925, 1430)),
                     ("display width 0", Plan(7, 0, 2160, 0, 0)),
                     ("display height 32769", Plan(7, 3840, 32769, 1925, 1430)),
                     ("cursor x -1", Plan(7, 3840, 2160, -1, 1430)),
                     ("cursor y -1", Plan(7, 3840, 2160, 1925, -1)),
                     ("cursor x == width", Plan(7, 3840, 2160, 3840, 1430)),
                     ("cursor y == height", Plan(7, 3840, 2160, 1925, 2160)),
                 })
        {
            await ExpectBadRequestAsync(client, "ocr.plan", payload, label);
        }

        await client.ResultAsync(client.Send("settings.update", Update(1, ("hoverEnabled", true), ("mode", "dictionary"))));

        await VerifyDictionaryPlanAsync(client);
        await VerifyDictionaryHoverAsync(client, hoverDeadlineMs);

        // Smart mode; keepWarm off, so no ranker starts and each card is rankerUnavailable.
        await client.ResultAsync(client.Send("settings.update", Update(2,
            ("mode", "smart"),
            ("sense", CoreHostFixtures.Payload(("keepWarm", false))))));
        await VerifySmartAsync(client, hoverDeadlineMs);

        TakeWorkerMissingLog(session.Sandbox.Logs);
        await session.ShutdownAsync("settings.json", "translation-history.v1.json", "Dictionaries");
    }

    // A smart hover over an ambiguous word asks the ranker, which this launch
    // has no worker for: Core logs each worker_missing to its hover log in the
    // log folder, which must otherwise stay empty. Anything else logged (an
    // exception from the observations path, say) fails here; the expected
    // entries are then removed so the sandbox check still covers the rest.
    private static void TakeWorkerMissingLog(string logs)
    {
        string log = Path.Combine(logs, "hover-translation.log");
        if (!File.Exists(log))
        {
            return;
        }

        string[] entries = File.ReadAllText(log)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.Trim().Length != 0)
            .ToArray();
        string[] other = entries
            .Where(entry => !entry.Contains("GlossBertRankerException: Не найден GlossBERT worker", StringComparison.Ordinal))
            .ToArray();
        CoreHostFixtures.Check(other.Length == 0,
            "Core logged more than the missing ranker worker during the observations hovers: " + string.Join(" | ", other));
        File.Delete(log);
    }

    // 900x340 around the cursor (T39), kept inside the display at its edge
    // and cut to a display smaller than that; never a cached fingerprint.
    private static async Task VerifyDictionaryPlanAsync(CoreHostClient client)
    {
        foreach ((int width, int height, int x, int y) in new[]
                 {
                     (3840, 2160, 1925, 1430),
                     (3840, 2160, 10, 2155),
                     (3840, 2160, 3839, 0),
                     (800, 300, 400, 150),
                 })
        {
            JsonObject plan = await client.ResultAsync(client.Send("ocr.plan", Plan(7, width, height, x, y)));
            Drawing.Rectangle expected = OcrGeometry.CalculateCaptureBounds(
                new Drawing.Rectangle(0, 0, width, height), new Drawing.Point(x, y), 900, 340);
            CoreHostFixtures.Check(
                plan.ToJsonString() ==
                $"{{\"mode\":\"dictionary\",\"captureBounds\":{Rectangle(expected).ToJsonString()},\"observations\":1}}" &&
                new Drawing.Rectangle(0, 0, width, height).Contains(expected) &&
                expected.Width == Math.Min(900, width) && expected.Height == Math.Min(340, height),
                $"ocr.plan in dictionary mode at ({x}, {y}) on {width}x{height}: {plan.ToJsonString()}, " +
                $"OcrGeometry.CalculateCaptureBounds gives {expected}.");
        }
    }

    // One observation through Core: hover.loading (the recognized line, as the
    // Windows loading card shows it in dictionary mode) before hover.primary,
    // the Apple Vision label, and history only after hover.displayed.
    private static async Task VerifyDictionaryHoverAsync(CoreHostClient client, int hoverDeadlineMs)
    {
        JsonObject plan = await client.ResultAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, 1925, 1430)));
        Scene scene = new(7, 3840, 2160, ToRectangle(plan["captureBounds"]!));
        Line river = Cut(RiverLine, 100, 150, 740, 180);
        Drawing.Point onBank = new(420, 165);
        HoverOcrResult expected = Near(Recognition([river]), onBank, sentenceContext: false, scene.Bounds) ??
            throw new InvalidOperationException("SelectNearPoint found nothing on the river line.");
        CoreHostClient.Pending hover = client.Send("hover.resolve", CoreHostFixtures.Payload(
            ("mode", "dictionary"),
            ("source", scene.Source(onBank, [Frame("00000000000000e2", [river])]))));
        (JsonObject loading, JsonObject primary) = await LoadingAndPrimaryAsync(client, hover, hoverDeadlineMs);
        CoreHostFixtures.Check(
            expected.HoveredWord == "bank" &&
            loading.Count == 2 &&
            loading["source"]!.GetValue<string>() == RiverLine &&
            loading["engineLabel"]!.GetValue<string>() == DictionaryLabel &&
            primary["source"]!.GetValue<string>() == "bank" &&
            primary["translation"]!.GetValue<string>().Replace("́", "", StringComparison.Ordinal).Contains("берег", StringComparison.Ordinal) &&
            primary["provenance"]!.GetValue<string>() == "dictionary" &&
            primary["engineLabel"]!.GetValue<string>() == DictionaryLabel &&
            primary["context"]!.GetValue<string>() == expected.Text &&
            primary["speaker"] is null,
            $"The dictionary observations hover did not come through Core: {loading.ToJsonString()} {primary.ToJsonString()}");

        JsonArray before = (await client.ResultAsync(client.Send("history.list")))["entries"]!.AsArray();
        CoreHostFixtures.Check(before.Count == 0, "An observations hover reached history before hover.displayed: " + before.ToJsonString());
        JsonObject displayed = await client.ResultAsync(client.Send("hover.displayed",
            CoreHostFixtures.Payload(("hoverId", hover.Id), ("part", "primary"))));
        JsonArray after = (await client.ResultAsync(client.Send("history.list")))["entries"]!.AsArray();
        CoreHostFixtures.Check(
            displayed["recorded"]!.GetValue<bool>() &&
            after.Count == 1 &&
            after[0]!["source"]!.GetValue<string>() == "bank" &&
            after[0]!["context"]!.GetValue<string>() == RiverLine &&
            after[0]!["engine"]!.GetValue<string>() == "APPLE VISION • СЛОВАРЬ",
            "History does not hold exactly the shown observations card: " + after.ToJsonString());
    }

    private static async Task VerifySmartAsync(CoreHostClient client, int hoverDeadlineMs)
    {
        Drawing.Point c0 = new(1925, 1430);
        JsonObject first = await client.ResultAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, c0.X, c0.Y)));
        Drawing.Rectangle fresh = OcrGeometry.CalculateSentenceCaptureBounds(new Drawing.Rectangle(0, 0, 3840, 2160), c0);
        CoreHostFixtures.Check(
            fresh == Smart4K.Bounds &&
            first.ToJsonString() == $"{{\"mode\":\"smart\",\"captureBounds\":{Rectangle(fresh).ToJsonString()},\"observations\":2}}",
            $"ocr.plan in smart mode with an empty cache: {first.ToJsonString()}, OcrGeometry gives {fresh}.");
        Drawing.Point able = new(520, 295);

        // The frame changed between the two captures: no nameplate in the
        // first, and another sentence under the cursor in the second. Core
        // keeps the first observation, unstable and without a speaker, and
        // nothing is cached.
        HoverOcrResult? diverged = await SmartHoverAsync(client, hoverDeadlineMs, "frames that differ",
            Sakuya(nameplate: null), "0000000000000d11", Changed(), "0000000000000d12", able);
        CoreHostFixtures.Check(diverged is { IsSentenceContextStable: false, CurrentSpeaker: null },
            "The diverging fixture no longer gives Core an unstable result without a speaker: " + Shown(diverged));
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, cached: null, "after a hover whose observations diverged");

        // The same divergence with the nameplate in the first frame: Core keeps
        // that speaker on the unstable result and the card shows it, as the
        // WPF card does (DoD 20.3 item 4). It is unconfirmed: nothing is
        // cached, so no later card inherits it.
        HoverOcrResult? divergedNamed = await SmartHoverAsync(client, hoverDeadlineMs, "frames that differ, the first named",
            Sakuya(), "0000000000000d13", Changed(), "0000000000000d14", able);
        CoreHostFixtures.Check(divergedNamed is { IsSentenceContextStable: false, CurrentSpeaker.Name: "Sakuya" },
            "Core no longer keeps the first observation's speaker on a divergent pair; update DoD 20.3 item 4 " +
            "in docs/ROADMAP.md, docs/DEVELOPMENT.md and this check: " + Shown(divergedNamed));
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, cached: null, "after a divergent hover whose first frame was named");

        // Two observations that agree: stable, with the speaker, cached under
        // the first fingerprint (the round trip of its hex digits).
        HoverOcrResult? agreed = await SmartHoverAsync(client, hoverDeadlineMs, "frames that agree",
            Sakuya(), "0000000000000a01", Sakuya(), "0000000000000a01", able);
        CoreHostFixtures.Check(agreed is { IsSentenceContextStable: true, CurrentSpeaker.Name: "Sakuya" },
            "The agreeing fixture no longer gives Core a stable result with the speaker: " + Shown(agreed));
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, "0000000000000a01", "after a stable hover");

        // The cached bounds while the cursor stays in their reuse area (a
        // fresh plan there would move), fresh bounds outside it, and nothing
        // cached on another display of the same size.
        Drawing.Rectangle display = new(0, 0, 3840, 2160);
        Drawing.Point inside = new(2225, 1530);
        CoreHostFixtures.Check(
            OcrGeometry.IsInsideReuseArea(Smart4K.Bounds, inside) &&
            OcrGeometry.CalculateSentenceCaptureBounds(display, inside) != Smart4K.Bounds &&
            OcrGeometry.CalculateSentenceCaptureBounds(display, inside, Smart4K.Bounds) == Smart4K.Bounds,
            "The reuse-area cursor no longer separates reused from fresh bounds.");
        await ExpectPlanAsync(client, inside, Smart4K.Bounds, "0000000000000a01", "inside the reuse area");
        Drawing.Point outside = new(800, 1430);
        Drawing.Rectangle moved = OcrGeometry.CalculateSentenceCaptureBounds(display, outside, Smart4K.Bounds);
        CoreHostFixtures.Check(moved != Smart4K.Bounds && !OcrGeometry.IsInsideReuseArea(Smart4K.Bounds, outside),
            "The cursor outside the reuse area no longer moves the capture.");
        await ExpectPlanAsync(client, outside, moved, cached: null, "outside the reuse area");
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, cached: null, "on another display", displayId: 8);
        // The reuse area belongs to the display too: on display 8 the same
        // cursor gets fresh bounds, not display 7's cached ones.
        await ExpectPlanAsync(client, inside, OcrGeometry.CalculateSentenceCaptureBounds(display, inside), cached: null,
            "inside the reuse area on another display", displayId: 8);

        // Zero observations against the cached frame: Core's selection on the
        // cached recognition with the per-selection gates (another word, the
        // confirmed speaker kept). Two observations whose first fingerprint is
        // the cached one take the same path, whatever they hold.
        Drawing.Point little = new(550, 214);
        HoverOcrResult? gated = OcrTextRules.ApplyConfirmedFrameGates(
            Near(Recognition(Sakuya()), little, sentenceContext: true), "Sakuya");
        CoreHostFixtures.Check(gated is { HoveredWord: "little", CurrentSpeaker.Name: "Sakuya" },
            "The cached-frame fixture no longer selects «little» with the confirmed speaker: " + Shown(gated));
        await ExpectCardAsync(client, hoverDeadlineMs, "zero observations on the cached frame",
            Smart4K.Source(little, [], "0000000000000a01"), gated);
        await ExpectCardAsync(client, hoverDeadlineMs, "two observations whose first is the cached frame",
            Smart4K.Source(little, [Frame("0000000000000a01", []), Frame("0000000000000a02", [])]), gated);

        // A miss is stale_frame before Core: no event, no status, no outcome.
        foreach ((string label, JsonObject source) in new[]
                 {
                     ("on another display", new Scene(8, 3840, 2160, Smart4K.Bounds).Source(little, [], "0000000000000a01")),
                     ("with another fingerprint", Smart4K.Source(little, [], "0000000000000a02")),
                     ("at other bounds", new Scene(7, 3840, 2160, moved).Source(new Drawing.Point(100, 100), [], "0000000000000a01")),
                 })
        {
            int statuses = client.Unsolicited.Count;
            CoreHostClient.Pending stale = client.Send("hover.resolve", CoreHostFixtures.Payload(("mode", "smart"), ("source", source)));
            JsonObject error = await client.ErrorAsync(stale, "stale_frame", hoverDeadlineMs);
            // A failed hover would report itself: hover_failed, and Core's
            // own «Перевод под курсором: …».
            string[] failures = client.Unsolicited
                .Skip(statuses)
                .Where(frame => frame["event"]!.GetValue<string>() == "status")
                .Select(frame => frame["payload"]!)
                .Where(status =>
                    status["code"]?.GetValue<string>() == "hover_failed" ||
                    status["text"]!.GetValue<string>().StartsWith("Перевод под курсором", StringComparison.Ordinal))
                .Select(status => status.ToJsonString())
                .ToArray();
            CoreHostFixtures.Check(
                error["message"]!.GetValue<string>() == StaleFrameMessage &&
                stale.Events.Count == 0 &&
                failures.Length == 0,
                $"A hover with zero observations {label} must be stale_frame before Core: {error.ToJsonString()} {string.Join(" ", failures)}");
        }

        // Nameplates that disagree on a complete sentence: stable without a
        // speaker, so the cached frame confirms none, and the gates keep the
        // nameplate the cached recognition still holds off the card.
        HoverOcrResult? unnamed = await SmartHoverAsync(client, hoverDeadlineMs, "nameplates that disagree",
            Sakuya(), "0000000000000d21", Sakuya("Sakuyo"), "0000000000000d22", able);
        CoreHostFixtures.Check(unnamed is { IsSentenceContextStable: true, CurrentSpeaker: null },
            "The disagreeing-nameplate fixture no longer gives Core a stable result without a speaker: " + Shown(unnamed));
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, "0000000000000d21", "after a stable hover without a confirmed speaker");
        HoverOcrResult? ungated = Near(Recognition(Sakuya()), able, sentenceContext: true);
        HoverOcrResult? confirmedNone = OcrTextRules.ApplyConfirmedFrameGates(ungated, null);
        CoreHostFixtures.Check(ungated?.CurrentSpeaker?.Name == "Sakuya" && confirmedNone is { CurrentSpeaker: null },
            "The cached recognition no longer holds the nameplate the gates must drop.");
        await ExpectCardAsync(client, hoverDeadlineMs, "zero observations on a frame with no confirmed speaker",
            Smart4K.Source(able, [], "0000000000000d21"), confirmedNone);

        // The second observation wins: it holds the first's sentence and more
        // (the first frame lacks the third dialogue line). The cache keeps the
        // chosen observation, as Windows stores selectedFingerprint and
        // selectedRecognition: ocr.plan names the second fingerprint, and a
        // hover with zero observations over «people."», which only the second
        // frame holds, selects in the second recognition.
        Line[] partial = [.. Sakuya()[..^2], Back];
        OcrTextRules.StabilizeSentenceContext(
            Near(Recognition(partial), able, sentenceContext: true),
            Near(Recognition(Sakuya()), able, sentenceContext: true),
            out bool useSecondObservation);
        HoverOcrResult? chosen = await SmartHoverAsync(client, hoverDeadlineMs, "a second frame that holds more",
            partial, "0000000000000b01", Sakuya(), "0000000000000b02", able);
        CoreHostFixtures.Check(
            useSecondObservation && chosen is { IsSentenceContextStable: true, CurrentSpeaker.Name: "Sakuya", Text: SakuyaSentence },
            "The fixture no longer makes Core choose the second observation, stable with the speaker: " + Shown(chosen));
        await ExpectPlanAsync(client, c0, Smart4K.Bounds, "0000000000000b02", "after a stable hover that chose the second observation");
        Drawing.Point people = new(700, 375);
        HoverOcrResult? onSecond = OcrTextRules.ApplyConfirmedFrameGates(
            Near(Recognition(Sakuya()), people, sentenceContext: true), "Sakuya");
        HoverOcrResult? onFirst = OcrTextRules.ApplyConfirmedFrameGates(
            Near(Recognition(partial), people, sentenceContext: true), "Sakuya");
        CoreHostFixtures.Check(onSecond is { HoveredWord: "people.\"" } && Shown(onFirst) != Shown(onSecond),
            $"The «people» cursor no longer tells the two frames apart: {Shown(onSecond)} against {Shown(onFirst)}.");
        await ExpectCardAsync(client, hoverDeadlineMs, "zero observations on the second frame the cache chose",
            Smart4K.Source(people, [], "0000000000000b02"), onSecond);
    }

    // A smart hover with two observations; returns Core's own result on the
    // same records and requires the card to carry it.
    private static async Task<HoverOcrResult?> SmartHoverAsync(
        CoreHostClient client,
        int hoverDeadlineMs,
        string label,
        Line[] first,
        string firstFingerprint,
        Line[] second,
        string secondFingerprint,
        Drawing.Point cursor)
    {
        HoverOcrResult? expected = OcrTextRules.StabilizeSentenceContext(
            Near(Recognition(first), cursor, sentenceContext: true),
            Near(Recognition(second), cursor, sentenceContext: true),
            out _);
        await ExpectCardAsync(client, hoverDeadlineMs, label,
            Smart4K.Source(cursor, [Frame(firstFingerprint, first), Frame(secondFingerprint, second)]), expected);
        return expected;
    }

    private static async Task ExpectCardAsync(
        CoreHostClient client,
        int hoverDeadlineMs,
        string label,
        JsonObject source,
        HoverOcrResult? expected)
    {
        CoreHostFixtures.Check(expected is not null, $"The {label} fixture selects nothing.");
        CoreHostClient.Pending hover = client.Send("hover.resolve", CoreHostFixtures.Payload(("mode", "smart"), ("source", source)));
        (JsonObject loading, JsonObject primary) = await LoadingAndPrimaryAsync(client, hover, hoverDeadlineMs);
        string? speaker = expected!.CurrentSpeaker?.Name;
        string View(string name) => $"{{\"name\":\"{name}\",\"gender\":\"unknown\",\"observedName\":\"{name}\"}}";
        CoreHostFixtures.Check(
            loading["source"]!.GetValue<string>() == expected.HoveredWord &&
            primary["context"]!.GetValue<string>() == expected.Text &&
            loading["speaker"]?.ToJsonString() == (speaker is null ? null : View(speaker)) &&
            primary["speaker"]?.ToJsonString() == (speaker is null ? null : View(speaker)),
            $"The smart hover over {label} does not carry Core's selection {Shown(expected)}: " +
            $"{loading.ToJsonString()} {primary.ToJsonString()}");
    }

    private static async Task ExpectPlanAsync(
        CoreHostClient client,
        Drawing.Point cursor,
        Drawing.Rectangle bounds,
        string? cached,
        string label,
        long displayId = 7)
    {
        JsonObject plan = await client.ResultAsync(client.Send("ocr.plan", Plan(displayId, 3840, 2160, cursor.X, cursor.Y)));
        string expected = $"{{\"mode\":\"smart\",\"captureBounds\":{Rectangle(bounds).ToJsonString()},\"observations\":2" +
                          (cached is null ? "}" : $",\"cachedFingerprint\":\"{cached}\"}}");
        CoreHostFixtures.Check(plan.ToJsonString() == expected,
            $"ocr.plan {label}: {plan.ToJsonString()}, expected {expected}.");
    }

    /// <summary>
    /// ocr.plan right behind a held-back settings.update (the admission-order
    /// launch, test.delayNextWork): it waits for the settings queue, so it
    /// sees dictionary mode and plans 900x340.
    /// </summary>
    public static async Task VerifyPlanBehindSettingsAsync(
        CoreHostClient client,
        long revision,
        Func<CoreHostClient, Task> holdBack)
    {
        JsonObject enabled = await client.ResultAsync(client.Send("settings.update", Update(revision, ("hoverEnabled", true))));
        CoreHostFixtures.Check(enabled["settings"]!["mode"]!.GetValue<string>() == "smart",
            "The admission-order launch must be in smart mode before the switch: " + enabled.ToJsonString());
        await holdBack(client);
        CoreHostClient.Pending toDictionary = client.Send("settings.update", Update(revision + 1, ("mode", "dictionary")));
        JsonObject plan = await client.ResultAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, 1925, 1430)));
        CoreHostFixtures.Check(
            plan.ToJsonString() == "{\"mode\":\"dictionary\",\"captureBounds\":{\"x\":1475,\"y\":1260,\"width\":900,\"height\":340},\"observations\":1}",
            "ocr.plan right behind the switch to dictionary mode did not see it: " + plan.ToJsonString());
        CoreHostFixtures.Check((await client.ResultAsync(toDictionary))["revision"]!.GetValue<long>() == revision + 2,
            "The held-back switch to dictionary mode did not apply.");
    }

    /// <summary>
    /// ocr.plan waits for the settings queue only, never for the hover queue,
    /// so a capture plan is not stuck behind a slow hover. The last step of
    /// the admission-order launch: test.fakeCore puts its engine in place of
    /// the Core session's for hovers (ocr.plan still runs on the session),
    /// holds a hover for 5 s, and the plan sent right behind it must be
    /// answered while that hover is still running.
    /// </summary>
    public static async Task VerifyPlanBesideHoverAsync(CoreHostClient client)
    {
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(
            ("phrase", "none"),
            ("primaryDelaysMs", CoreHostFixtures.Payload(("slow", 5_000))))));
        CoreHostClient.Pending hover = client.Send("hover.resolve", CoreHostFixtures.Payload(("source", CoreHostFixtures.Payload(
            ("kind", "selection"), ("text", "the slow river"), ("hoveredWord", "slow"), ("characterIndex", 4)))));
        JsonObject plan = await client.ResultAsync(client.Send("ocr.plan", Plan(7, 3840, 2160, 1925, 1430)), 15_000);
        bool hoverRunning = !hover.Terminal.Task.IsCompleted;
        CoreHostFixtures.Check(
            hoverRunning &&
            plan.ToJsonString() == "{\"mode\":\"dictionary\",\"captureBounds\":{\"x\":1475,\"y\":1260,\"width\":900,\"height\":340},\"observations\":1}",
            $"ocr.plan waited for the hover queue (hover still running: {hoverRunning}): {plan.ToJsonString()}");
        await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", hover.Id))));
        await client.ErrorAsync(hover, "cancelled", 2_000);
    }

    // ---- helpers ---------------------------------------------------------------

    private sealed record Word(string Text, double Left, double Top, double Right, double Bottom);

    // Decoded is the text ExtractRecognition's NormalizeLong leaves, when it differs.
    private sealed record Line(string Text, Word[] Words, string? Decoded = null)
    {
        public string DecodedText => Decoded ?? Text;
    }

    private sealed record Scene(long DisplayId, int Width, int Height, Drawing.Rectangle Bounds)
    {
        // A source whose cursor is the frame point plus the capture's origin.
        public JsonObject Source(Drawing.Point framePoint, JsonObject[] frames, string? cachedFingerprint = null)
        {
            JsonObject source = CoreHostFixtures.Payload(
                ("kind", "observations"),
                ("display", Display(DisplayId, Width, Height)),
                ("captureBounds", Rectangle(Bounds)),
                ("cursor", Point(Bounds.X + framePoint.X, Bounds.Y + framePoint.Y)),
                ("observations", new JsonArray(frames.Select(frame => (JsonNode)frame).ToArray())));
            if (cachedFingerprint is not null)
            {
                source["cachedFingerprint"] = cachedFingerprint;
            }

            return source;
        }
    }

    // The words of a line box cut at the character positions of its
    // space-separated words, as "Neural OCR wrapped sentence" builds them.
    private static Line Cut(string text, double left, double top, double right, double bottom, string? decoded = null)
    {
        double width = right - left;
        List<Word> words = [];
        int end = 0;
        while (end < text.Length)
        {
            if (text[end] == ' ')
            {
                end++;
                continue;
            }

            int start = end;
            while (end < text.Length && text[end] != ' ')
            {
                end++;
            }

            words.Add(new Word(text[start..end], left + (width * start / text.Length), top, left + (width * end / text.Length), bottom));
        }

        return new Line(text, [.. words], decoded);
    }

    private static JsonObject Frame(string fingerprint, IEnumerable<Line> lines) => CoreHostFixtures.Payload(
        ("fingerprint", fingerprint),
        ("lines", new JsonArray(lines.Select(line => (JsonNode)CoreHostFixtures.Payload(
            ("text", line.Text),
            ("confidence", 1),
            ("words", new JsonArray(line.Words.Select(word => (JsonNode)CoreHostFixtures.Payload(
                ("text", word.Text),
                ("left", word.Left),
                ("top", word.Top),
                ("right", word.Right),
                ("bottom", word.Bottom))).ToArray())))).ToArray())));

    // What ExtractRecognition makes of lines whose word boxes are all proper.
    private static OcrRecognition Recognition(IEnumerable<Line> lines) => new(lines
        .Select(line => new OcrLineWords(
            line.DecodedText,
            line.Words.Select(word => new OcrWordGeometry(word.Text, word.Left, word.Top, word.Right, word.Bottom, 1)).ToArray()))
        .ToArray());

    private static HoverOcrResult? Near(OcrRecognition recognition, Drawing.Point framePoint, bool sentenceContext, Drawing.Rectangle? bounds = null) =>
        OcrGeometry.SelectNearPoint(recognition, framePoint.X, framePoint.Y, bounds ?? Smart4K.Bounds, sentenceContext);

    private static JsonObject Display(long id, int width, int height) =>
        CoreHostFixtures.Payload(("id", id), ("width", width), ("height", height));

    private static JsonObject Rectangle(Drawing.Rectangle bounds) =>
        CoreHostFixtures.Payload(("x", bounds.X), ("y", bounds.Y), ("width", bounds.Width), ("height", bounds.Height));

    private static Drawing.Rectangle ToRectangle(JsonNode bounds) => new(
        bounds["x"]!.GetValue<int>(),
        bounds["y"]!.GetValue<int>(),
        bounds["width"]!.GetValue<int>(),
        bounds["height"]!.GetValue<int>());

    private static JsonObject Point(int x, int y) => CoreHostFixtures.Payload(("x", x), ("y", y));

    private static JsonObject Plan(long displayId, int width, int height, int x, int y) =>
        CoreHostFixtures.Payload(("display", Display(displayId, width, height)), ("cursor", Point(x, y)));

    private static JsonObject Update(long baseRevision, params (string Name, JsonNode? Value)[] patch) =>
        CoreHostFixtures.Payload(("baseRevision", baseRevision), ("patch", CoreHostFixtures.Payload(patch)));

    private static (string, Action<JsonObject>)[] Edits(params (string, Action<JsonObject>)[] edits) => edits;

    private static async Task<JsonObject> SelectAsync(CoreHostClient client, string mode, JsonObject source) =>
        await client.ResultAsync(client.Send("ocr.select", CoreHostFixtures.Payload(("mode", mode), ("source", source))));

    private static async Task ExpectBadRequestAsync(CoreHostClient client, string command, JsonObject payload, string label)
    {
        JsonObject terminal = await client.AwaitAsync(client.Send(command, payload), 10_000);
        CoreHostFixtures.Check(
            !terminal["ok"]!.GetValue<bool>() && terminal["error"]!["code"]!.GetValue<string>() == "bad_request",
            $"{command} with {label} must be bad_request: {terminal.ToJsonString()}");
    }

    // Sends the payload with one literal replaced, as a raw line: JsonNode
    // cannot write a number JSON has no value for.
    private static async Task ExpectRawBadRequestAsync(
        CoreHostClient client,
        string command,
        JsonObject payload,
        string placeholder,
        string literal,
        string label)
    {
        CoreHostClient.Pending ping = client.Send("core.ping");
        await client.ResultAsync(ping);
        JsonObject frame = new() { ["version"] = 1, ["id"] = ping.Id + 1, ["command"] = command, ["payload"] = payload };
        string line = frame.ToJsonString();
        CoreHostFixtures.Check(line.Contains(placeholder, StringComparison.Ordinal), $"The {label} request has no {placeholder}.");
        JsonObject terminal = await client.AwaitAsync(client.SendRawRequest(line.Replace(placeholder, literal, StringComparison.Ordinal)), 10_000);
        CoreHostFixtures.Check(
            !terminal["ok"]!.GetValue<bool>() && terminal["error"]!["code"]!.GetValue<string>() == "bad_request",
            $"{command} with {label} must be bad_request: {terminal.ToJsonString()}");
    }

    private static async Task<(JsonObject Loading, JsonObject Primary)> LoadingAndPrimaryAsync(
        CoreHostClient client,
        CoreHostClient.Pending hover,
        int deadlineMs)
    {
        JsonObject result = await client.ResultAsync(hover, deadlineMs);
        List<JsonObject> events;
        lock (hover.Events)
        {
            events = [.. hover.Events];
        }

        CoreHostFixtures.Check(
            result["outcome"]!.GetValue<string>() == "completed" &&
            events.Select(frame => frame["event"]!.GetValue<string>()).SequenceEqual(["hover.loading", "hover.primary"]),
            "An observations hover must end completed after hover.loading and then hover.primary: " +
            $"{result.ToJsonString()} events {string.Join(" ", events.Select(frame => frame.ToJsonString()))}");
        return (events[0]["payload"]!.AsObject(), events[1]["payload"]!.AsObject());
    }

    private static string[] Names(CoreHostClient.Pending pending)
    {
        lock (pending.Events)
        {
            return pending.Events.Select(frame => frame["event"]!.GetValue<string>()).ToArray();
        }
    }

    private static string Shown(HoverOcrResult? result) =>
        result is null
            ? "null"
            : $"{result.Text}|{result.HoveredWord}|{result.CharacterIndex}|{result.IsSentenceContextStable}|{result.CurrentSpeaker?.Name ?? "-"}";

    private static string Shown(JsonNode? selection) =>
        selection is null
            ? "null"
            : $"{selection["text"]!.GetValue<string>()}|{selection["hoveredWord"]!.GetValue<string>()}|" +
              $"{selection["characterIndex"]!.GetValue<int>()}|{selection["sentenceStable"]!.GetValue<bool>()}|" +
              $"{selection["speakerName"]?.GetValue<string>() ?? "-"}";
}
