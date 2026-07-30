using System.Text.Json;
using AsteriaScale.Services;

internal static class SmartPhraseTranslationFoundationTests
{
    public static async Task RunAsync(string tempRoot)
    {
        VerifyTargetCenteredPhraseBuilder();
        VerifyIdiomRewriter();
        VerifyProperNameWhitelist();
        await VerifyTranslatorContractAsync();
        VerifyOutputGuard();
        await VerifyPersistentCacheAsync(tempRoot);
        await VerifyStructurallyCorruptCacheFailsOpenAsync(tempRoot);
    }

    private static void VerifyIdiomRewriter()
    {
        foreach ((string source, string target, string expected, string identity) in new[]
                 {
                     (
                         "You're trying to butter me up.",
                         "butter",
                        "You're flattering me to get what you want.",
                        "idiom:butter-object-up@v4"),
                     (
                         "He buttered me up.",
                         "buttered",
                        "He flattered me to get what he wanted.",
                        "idiom:butter-object-up@v4"),
                     (
                         "She is buttering him up.",
                         "buttering",
                        "She is flattering him to get what she wants.",
                        "idiom:butter-object-up@v4"),
                     (
                         "He butters them up.",
                         "butters",
                        "He flatters them to get what he wants.",
                        "idiom:butter-object-up@v4"),
                     (
                        "He tried to butter us up.",
                        "butter",
                        "He flattered us to get what he wanted.",
                        "idiom:butter-object-up@v4")
                 })
        {
            int index = source.IndexOf(target, StringComparison.Ordinal);
            SmartPhraseModelInput rewritten = SmartPhraseIdiomRewriter.Rewrite(
                source,
                target,
                index);
            Check(rewritten.SourcePhrase == source &&
                  rewritten.ModelInputPhrase == expected &&
                  rewritten.TransformIdentity == identity,
                $"The deterministic idiom rewrite failed: {source} -> {rewritten}");
        }

        foreach ((string source, string target) in new[]
                 {
                     ("She spread butter on the toast.", "butter"),
                     ("A buttercup grew outside.", "buttercup"),
                     ("Please butter me upside down.", "butter"),
                     ("I told John to butter me up.", "butter"),
                     ("Feel the surface first.", "Feel"),
                     ("This is the part that always feels off.", "feels"),
                     ("I feel off today.", "feel"),
                     ("The fabric feels off.", "feels"),
                     ("You're trying to butter me up.", "trying")
                 })
        {
            int index = source.IndexOf(target, StringComparison.Ordinal);
            SmartPhraseModelInput unchanged = SmartPhraseIdiomRewriter.Rewrite(
                source,
                target,
                index);
            Check(unchanged.ModelInputPhrase == source &&
                  unchanged.TransformIdentity == SmartPhraseIdiomRewriter.NoTransformIdentity,
                $"An unproven or non-anchored idiom was rewritten: {source}");
        }
    }

    private static void VerifyProperNameWhitelist()
    {
        const string namedSource = "Alice introduced Kotori to Michael.";
        IReadOnlyList<string> names = SmartHoverPipeline.ExtractStableProperNames(namedSource);
        SmartPhraseTranslationRequest namedRequest = CreateRequest(
            namedSource,
            properNames: names);
        Check(names.SequenceEqual(["Kotori", "Michael"]) &&
              SmartPhraseTranslationGuard.Validate(
                  namedRequest,
                  Result(namedRequest, "Алиса познакомила Kotori с Michael.")).Accepted,
            "High-confidence non-initial character names were not preserved by the phrase guard.");

        const string commonInitial = "Sounds professional to me.";
        IReadOnlyList<string> commonInitialNames =
            SmartHoverPipeline.ExtractStableProperNames(commonInitial);
        SmartPhraseTranslationRequest commonInitialRequest = CreateRequest(
            commonInitial,
            properNames: commonInitialNames);
        Check(commonInitialNames.Count == 0 &&
              SmartPhraseTranslationGuard.Validate(
                  commonInitialRequest,
                  Result(commonInitialRequest, "Sounds профессионально.")).Reason ==
              SmartPhraseRejectionReason.ResidualEnglish,
            "A sentence-initial common word was whitelisted as a proper name.");

        const string mixedCaseName = "LeMU welcomes everyone.";
        Check(SmartHoverPipeline.ExtractStableProperNames(mixedCaseName)
                .SequenceEqual(["LeMU"]),
            "A sentence-initial mixed-case entity lost its high-confidence name signal.");

        const string uppercaseSource = "THE BANK IS CLOSED.";
        IReadOnlyList<string> uppercaseNames =
            SmartHoverPipeline.ExtractStableProperNames(uppercaseSource);
        SmartPhraseTranslationRequest uppercaseRequest = CreateRequest(
            uppercaseSource,
            properNames: uppercaseNames);
        Check(!uppercaseNames.Contains("BANK", StringComparer.Ordinal) &&
              SmartPhraseTranslationGuard.Validate(
                  uppercaseRequest,
                  Result(uppercaseRequest, "BANK закрыт.")).Reason ==
              SmartPhraseRejectionReason.ResidualEnglish,
            "An all-caps OCR common word was whitelisted as a proper name.");
    }

    private static void VerifyTargetCenteredPhraseBuilder()
    {
        const string context =
            "The first bank handled our money yesterday before lunch. " +
            "Later we quietly rested on the bank beside the river after sunset with old friends.";
        int selected = context.LastIndexOf("bank", StringComparison.Ordinal);
        SmartPhraseContext? built = SmartPhraseContextBuilder.Build(
            context,
            "bank",
            selected);
        Check(built is not null, "A valid target-centred phrase was not built.");
        SmartPhraseContext phrase = built ?? throw new InvalidOperationException(
            "A valid target-centred phrase was not built.");
        Check(CountWords(phrase.Text) <= SmartPhraseContextBuilder.MaximumWords,
            "The narrow phrase exceeded its twelve-word source budget.");
        Check(!phrase.Text.Contains("first bank", StringComparison.OrdinalIgnoreCase),
            "Phrase extraction crossed the selected occurrence's sentence boundary.");
        Check(phrase.Text.Contains("river", StringComparison.Ordinal),
            "Immediate sense-bearing context was dropped from the phrase.");
        Check(string.Equals(
                phrase.Text.Substring(phrase.CharacterIndex, "bank".Length),
                "bank",
                StringComparison.Ordinal),
            "The selected repeated occurrence was not preserved at the returned index.");

        const string repeated =
            "bank one two three four five six seven eight nine ten bank river";
        int second = repeated.LastIndexOf("bank", StringComparison.Ordinal);
        SmartPhraseContext? repeatedBuilt = SmartPhraseContextBuilder.Build(
            repeated,
            "bank",
            second);
        Check(repeatedBuilt is not null &&
              repeatedBuilt.Value.CharacterIndex ==
              repeatedBuilt.Value.Text.LastIndexOf("bank", StringComparison.Ordinal),
            "CharacterIndex did not select the requested repeated target occurrence.");
        SmartPhraseContext repeatedContext = repeatedBuilt ??
            throw new InvalidOperationException("A repeated target phrase was not built.");
        Check(string.Equals(repeatedContext.Text, repeated, StringComparison.Ordinal),
            "A complete sentence longer than the legacy twelve-word window was truncated.");

        const string multiword =
            "Ignore this sentence. She was on the verge of tears when we arrived.";
        SmartPhraseContext? multiwordBuilt = SmartPhraseContextBuilder.Build(
            multiword,
            "on the verge of",
            multiword.IndexOf("on the verge of", StringComparison.Ordinal));
        Check(multiwordBuilt is not null &&
              multiwordBuilt.Value.Text.Contains("on the verge of", StringComparison.Ordinal),
            "A multi-word target did not survive phrase extraction.");
        SmartPhraseContext multiwordContext = multiwordBuilt ??
            throw new InvalidOperationException("A multi-word target phrase was not built.");
        Check(!multiwordContext.Text.Contains("Ignore", StringComparison.Ordinal),
            "A multi-word target pulled text from the preceding sentence.");
        Check(multiwordContext.Text.EndsWith(".", StringComparison.Ordinal),
            "A complete short sentence lost punctuation needed by the translator.");

        const string titledAddressee =
            "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe. " +
            "I would appreciate it if you could forgive him.";
        const string expectedTitledSentence =
            "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe.";
        foreach (string titledTarget in new[] { "a lot", "Giraffe" })
        {
            SmartPhraseContext? titled = SmartPhraseContextBuilder.Build(
                titledAddressee,
                titledTarget,
                titledAddressee.IndexOf(titledTarget, StringComparison.Ordinal));
            Check(titled is not null &&
                  string.Equals(titled.Value.Text, expectedTitledSentence, StringComparison.Ordinal),
                $"The title abbreviation truncated or merged the sentence around '{titledTarget}'.");
        }

        const string scientificAbbreviations =
            "Dr. Rivera reviewed Fig. 2 carefully. The result was conclusive.";
        SmartPhraseContext? scientific = SmartPhraseContextBuilder.Build(
            scientificAbbreviations,
            "reviewed",
            scientificAbbreviations.IndexOf("reviewed", StringComparison.Ordinal));
        Check(scientific is not null &&
              scientific.Value.Text == "Dr. Rivera reviewed Fig. 2 carefully.",
            "Scientific abbreviations split or merged a complete sentence.");

        const string question =
            "Are you talking about these magnificent 88-wide hips?";
        SmartPhraseContext? questionBuilt = SmartPhraseContextBuilder.Build(
            question,
            "88-wide",
            question.IndexOf("88-wide", StringComparison.Ordinal));
        Check(questionBuilt is not null &&
              string.Equals(questionBuilt.Value.Text, question, StringComparison.Ordinal),
            "A short question lost its question mark in the phrase phase.");

        const string continuedEllipsis =
            "Sounds professional... and kind of like 'Cry Ant.'";
        foreach (string ellipsisTarget in new[] { "professional", "kind" })
        {
            SmartPhraseContext? ellipsisBuilt = SmartPhraseContextBuilder.Build(
                continuedEllipsis,
                ellipsisTarget,
                continuedEllipsis.IndexOf(ellipsisTarget, StringComparison.Ordinal));
            Check(ellipsisBuilt is not null &&
                  string.Equals(
                      ellipsisBuilt.Value.Text,
                      continuedEllipsis,
                      StringComparison.Ordinal),
                $"A continued ellipsis split the phrase around '{ellipsisTarget}'.");
        }

        const string speakerLine = "Kotori\r\n\"Boyquaintance.\"";
        SmartPhraseContext? speakerLineBuilt = SmartPhraseContextBuilder.Build(
            speakerLine,
            "Boyquaintance",
            speakerLine.IndexOf("Boyquaintance", StringComparison.Ordinal));
        Check(speakerLineBuilt is { Text: "\"Boyquaintance.\"", CharacterIndex: 1 },
            "A standalone OCR speaker line leaked into the translated CURRENT sentence.");

        const string bracketedSpeaker = "[Park staff] \"Wait here.\"";
        SmartPhraseContext? bracketedSpeakerBuilt = SmartPhraseContextBuilder.Build(
            bracketedSpeaker,
            "Wait",
            bracketedSpeaker.IndexOf("Wait", StringComparison.Ordinal));
        Check(bracketedSpeakerBuilt is { Text: "\"Wait here.\"", CharacterIndex: 1 },
            "A bracketed text-hook speaker label leaked into the translated CURRENT sentence.");

        const string danglingSpeakerQuote =
            "[Park staff] \"Why do you think it is necessary to raise the air pressure?";
        SmartPhraseContext? danglingSpeakerQuoteBuilt = SmartPhraseContextBuilder.Build(
            danglingSpeakerQuote,
            "necessary",
            danglingSpeakerQuote.IndexOf("necessary", StringComparison.Ordinal));
        Check(danglingSpeakerQuoteBuilt is
              {
                  Text: "Why do you think it is necessary to raise the air pressure?"
              } &&
              danglingSpeakerQuoteBuilt.Value.Text[
                      danglingSpeakerQuoteBuilt.Value.CharacterIndex..]
                  .StartsWith("necessary", StringComparison.Ordinal),
            "A dangling OCR dialogue quote survived speaker removal or displaced the target.");
        SmartPhraseTranslationRequest danglingSpeakerRequest = CreateRequest(
            danglingSpeakerQuoteBuilt?.Text ?? string.Empty);
        Check(SmartPhraseTranslationGuard.ValidateForDisplay(
                  danglingSpeakerRequest,
                  Result(
                      danglingSpeakerRequest,
                      "«Почему вы считаете необходимым повышать давление воздуха?»")) is
              {
                  Accepted: true,
                  Translation: "Почему вы считаете необходимым повышать давление воздуха?"
              },
            "The built dangling-quote context and display guard disagreed end to end.");

        const string unicodeEllipsis = "That's… not what I meant at all.";
        SmartPhraseContext? unicodeEllipsisBuilt = SmartPhraseContextBuilder.Build(
            unicodeEllipsis,
            "meant",
            unicodeEllipsis.IndexOf("meant", StringComparison.Ordinal));
        Check(unicodeEllipsisBuilt is not null &&
              string.Equals(
                  unicodeEllipsisBuilt.Value.Text,
                  unicodeEllipsis,
                  StringComparison.Ordinal),
            "A continued unicode ellipsis split the phrase.");

        const string closedEllipsis = "I don't know... Maybe we should just go.";
        SmartPhraseContext? beforeClosedEllipsis = SmartPhraseContextBuilder.Build(
            closedEllipsis,
            "know",
            closedEllipsis.IndexOf("know", StringComparison.Ordinal));
        SmartPhraseContext? afterClosedEllipsis = SmartPhraseContextBuilder.Build(
            closedEllipsis,
            "Maybe",
            closedEllipsis.IndexOf("Maybe", StringComparison.Ordinal));
        Check(beforeClosedEllipsis is { Text: "I don't know..." } &&
              afterClosedEllipsis is { Text: "Maybe we should just go." },
            "A sentence-final ellipsis stopped separating capitalized replies.");

        string overBudgetTarget = string.Join(
            " ",
            Enumerable.Range(0, SmartPhraseContextBuilder.MaximumWords + 1)
                .Select(index => "word" + index));
        Check(SmartPhraseContextBuilder.Build(
                  overBudgetTarget,
                  overBudgetTarget,
                  0) is null,
            "An oversized sentence was partially translated instead of failing open.");
        Check(SmartPhraseContextBuilder.Build("an embankment", "bank", 5) is null,
            "A target embedded inside another token was treated as an occurrence.");
    }

    private static async Task VerifyTranslatorContractAsync()
    {
        SmartPhraseTranslationRequest request = CreateRequest();
        RecordingPhraseTranslator translator = new();
        SmartPhraseTranslationResult result = await translator.TranslateAsync(
            request,
            CancellationToken.None);
        Check(ReferenceEquals(translator.LastRequest, request),
            "The narrow translator DI seam did not receive its request.");
        Check(result.Translation == "Мы отдыхали на берегу реки." &&
              result.ModelIdentity == request.ModelIdentity,
            "The narrow translator DI seam changed its typed result.");
    }

    private static void VerifyOutputGuard()
    {
        SmartPhraseTranslationRequest request = CreateRequest();
        SmartPhraseGuardResult accepted = SmartPhraseTranslationGuard.Validate(
            request,
            Result(request, "Мы отдыхали у реки."));
        Check(accepted.Accepted,
            "An independent natural phrase translation was tied to a dictionary surface.");

        SmartPhraseTranslationRequest idiom = CreateRequest(
            "You're trying to butter me up",
            modelInput: "You're flattering me to get what you want",
            transformIdentity: "idiom:butter-object-up@v4");
        Check(SmartPhraseTranslationGuard.Validate(
                  idiom,
                  Result(idiom, "Ты пытаешься мне льстить.")).Accepted,
            "An idiomatic phrase translation was rejected for omitting the literal word surface.");
        Check(SmartPhraseTranslationGuard.Validate(
                  idiom,
                  Result(idiom, "You're flattering me to get what you want — ты пытаешься мне льстить.")).Reason ==
              SmartPhraseRejectionReason.PromptEcho,
            "The effective idiom paraphrase could be echoed into the visible result.");

        SmartPhraseTranslationRequest properName = CreateRequest(
            "LeMU lies near the bank",
            properNames: ["LeMU"]);
        Check(SmartPhraseTranslationGuard.Validate(
                  properName,
                  Result(properName, "LeMU стоит на берегу.")).Accepted,
            "An exactly copied proper name was treated as residual English.");
        Check(SmartPhraseTranslationGuard.Validate(
                  properName,
                  Result(properName, "lemu стоит на берегу.")).Accepted,
            "A case-only proper-name change was treated as residual English.");
        Check(SmartPhraseTranslationGuard.Validate(
                  properName,
                  Result(properName, "river стоит на берегу.")).Reason ==
              SmartPhraseRejectionReason.ResidualEnglish,
            "An unrelated Latin token was accepted as a copied proper name.");

        SmartPhraseTranslationRequest contextual = CreateRequest(
            "Hey, why do you think ants cry?") with
        {
            PreviousContext = ["Kotarou", "Kotori said that ants never cry."]
        };
        Check(SmartPhraseTranslationGuard.Validate(
                  contextual,
                  Result(contextual, "Котару: «Эй, почему, по-твоему, муравьи плачут?»")).Reason ==
              SmartPhraseRejectionReason.StructuralMismatch,
            "A speaker label inferred from hidden context reached CURRENT.");
        Check(SmartPhraseTranslationGuard.Validate(
                  contextual,
                  Result(contextual, "«Эй, почему, по-твоему, муравьи плачут?»")).Reason ==
              SmartPhraseRejectionReason.StructuralMismatch,
            "An outer quote wrapper absent from CURRENT was added by post-editing.");
        Check(SmartPhraseTranslationGuard.Validate(
                  contextual,
                  Result(contextual, "Эй, почему, по-твоему, муравьи плачут?")).Accepted,
            "A clean CURRENT-only translation was rejected because hidden context existed.");

        SmartPhraseTranslationRequest literalSpeaker = CreateRequest(
            "Kotarou: Hey, why do ants cry?",
            properNames: ["Kotarou"]);
        Check(SmartPhraseTranslationGuard.Validate(
                  literalSpeaker,
                  Result(literalSpeaker, "Котару: почему муравьи плачут?")).Accepted,
            "A speaker label literally present in CURRENT was rejected.");

        SmartPhraseTranslationRequest continued = CreateRequest(
            "Sounds professional... and kind of like 'Cry Ant.'",
            properNames: ["Cry", "Ant"]);
        Check(SmartPhraseTranslationGuard.Validate(
                  continued,
                  Result(continued, "...профессионально и похоже на Cry Ant.")).Reason ==
              SmartPhraseRejectionReason.StructuralMismatch,
            "A model-generated leading ellipsis hid the first source clause.");
        Check(SmartPhraseTranslationGuard.Validate(
                  continued,
                  Result(continued, "Звучит профессионально и похоже на Cry An.")).Reason ==
              SmartPhraseRejectionReason.StructuralMismatch,
            "A quoted English wordplay span was allowed to survive only partially.");
        Check(SmartPhraseTranslationGuard.Validate(
                  continued,
                  Result(continued, "Звучит профессионально и похоже на Cry Ant.")).Accepted,
            "A complete copied wordplay span was rejected.");

        SmartPhraseTranslationRequest leadingEllipsis = CreateRequest(
            "...and then she left.");
        Check(SmartPhraseTranslationGuard.Validate(
                  leadingEllipsis,
                  Result(leadingEllipsis, "...а затем она ушла.")).Accepted,
            "A leading ellipsis literally present in CURRENT was rejected.");

        SmartPhraseTranslationRequest oneWord = CreateRequest("Boyquaintance.");
        Check(SmartPhraseTranslationGuard.Validate(
                  oneWord,
                  Result(oneWord, "Парнезнакомый.")).Accepted,
            "A one-word OOV VN utterance could not use the independent sentence translator.");
        SmartPhraseTranslationRequest quotedOneWord = CreateRequest("\"Boyquaintance.\"");
        Check(SmartPhraseTranslationGuard.Validate(
                  quotedOneWord,
                  Result(quotedOneWord, "«Котори: мой знакомый»")).Reason ==
              SmartPhraseRejectionReason.StructuralMismatch,
            "A speaker inferred beside a quoted one-word utterance passed the structural guard.");

        SmartPhraseTranslationRequest uppercaseSource = CreateRequest(
            "LEMU BANK BY WATER",
            properNames: ["LEMU", "BANK", "BY", "WATER"]);
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Мы сидели on the берегу.")).Reason ==
              SmartPhraseRejectionReason.ResidualEnglish,
            "Residual English outside proper names reached the phrase result.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Мы отдыхали на берегу 日本語.")).Reason ==
              SmartPhraseRejectionReason.NonRussian,
            "A non-Russian letter script reached the phrase result.");
        SmartPhraseGuardResult labelled = SmartPhraseTranslationGuard.Validate(
            request,
            Result(request, "Перевод: мы отдыхали на берегу."));
        Check(labelled is { Accepted: true, Translation: "мы отдыхали на берегу." },
            "A safe one-line translation label was not normalized away.");
        SmartPhraseGuardResult numbered = SmartPhraseTranslationGuard.Validate(
            request,
            Result(request, "1. Мы отдыхали на берегу."));
        Check(numbered is { Accepted: true, Translation: "Мы отдыхали на берегу." },
            "A safe leading list marker was not normalized away.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "1. 2. Мы отдыхали на берегу.")).Reason ==
              SmartPhraseRejectionReason.Explanation,
            "Multiple list items were flattened into one visible translation.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "1 апреля мы отдыхали на берегу.")).Accepted,
            "A meaningful sentence-initial number was mistaken for list formatting.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Фраза означает: мы отдыхали на берегу.")).Reason ==
              SmartPhraseRejectionReason.Explanation,
            "A Russian explanatory preamble reached the phrase result.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "We rested on the bank by the river — мы отдыхали на берегу.")).Reason ==
              SmartPhraseRejectionReason.PromptEcho,
            "A prompt echo reached the phrase result.");
        Check(SmartPhraseTranslationGuard.Validate(
                  uppercaseSource,
                  Result(
                      uppercaseSource,
                      "LEMU / BANK, BY—WATER; мы отдыхали на берегу.")).Reason ==
              SmartPhraseRejectionReason.PromptEcho,
            "Punctuation changes let a complete all-caps prompt echo pass.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Мы отдыхали\nна берегу.")).Reason ==
              SmartPhraseRejectionReason.InvalidCharacters,
            "A multi-line answer reached the phrase result.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Мы отдыхали на\u200Bберегу.")).Reason ==
              SmartPhraseRejectionReason.InvalidCharacters,
            "A hidden Unicode format character reached the phrase result.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(
                      request,
                      string.Join(" ", Enumerable.Repeat("берег", 40)))).Reason ==
              SmartPhraseRejectionReason.TooLong,
            "An unbounded model answer reached the phrase result.");

        SmartPhraseTranslationRequest shortSource = CreateRequest("bank nearby");
        Check(SmartPhraseTranslationGuard.Validate(
                  shortSource,
                  Result(
                      shortSource,
                      "Мы очень долго спокойно отдыхали здесь на берегу реки")).Reason ==
              SmartPhraseRejectionReason.TooLong,
            "A short source was allowed an unrelated fixed-size output budget.");

        string overlongPhrase = string.Join(
            " ",
            new[] { "bank" }.Concat(Enumerable.Range(
                0,
                SmartPhraseContextBuilder.MaximumWords).Select(index => "context" + index)));
        SmartPhraseTranslationRequest overlongRequest = CreateRequest(overlongPhrase);
        Check(SmartPhraseTranslationGuard.Validate(
                  overlongRequest,
                  Result(overlongRequest, "Мы стояли на берегу.")).Reason ==
              SmartPhraseRejectionReason.SourceOutOfScope,
            "An oversized sentence entered the TranslateGemma phase.");

        SmartPhraseTranslationRequest termRequest = request with
        {
            SemanticTerms =
            [
                new ScaffoldTerm("gate", "ворота", 0.98)
            ]
        };
        Check(SmartPhraseTranslationGuard.Validate(
                  termRequest,
                  Result(termRequest, "Мы подошли к дому.")).Reason ==
              SmartPhraseRejectionReason.SemanticTermMissing,
            "A missing high-confidence sentence concept passed the semantic guard.");
        Check(SmartPhraseTranslationGuard.ValidateForDisplay(
                  termRequest,
                  Result(termRequest, "Мы подошли к дому.")).Reason ==
              SmartPhraseRejectionReason.SemanticTermMissing,
            "A mandatory deterministic term was weakened by the display policy.");
        Check(SmartPhraseTranslationGuard.Validate(
                  termRequest,
                  Result(termRequest, "Мы остановились перед воротами.")).Accepted,
            "A natural Russian inflection failed the semantic guard.");

        SmartPhraseTranslationRequest goodJoke = CreateRequest(
            "\"My friend told me a good joke the other day!\"") with
        {
            SemanticTerms =
            [
                new ScaffoldTerm(
                    "tell me",
                    "сказать",
                    0.96,
                    ScaffoldTermProvenance.AdvisoryDictionary)
            ]
        };
        SmartPhraseTranslationResult naturalGoodJoke = Result(
            goodJoke,
            "«Мой друг недавно рассказал мне отличную шутку!»");
        Check(SmartPhraseTranslationGuard.Validate(
                  goodJoke,
                  naturalGoodJoke).Reason == SmartPhraseRejectionReason.SemanticTermMissing &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  goodJoke,
                  naturalGoodJoke).Accepted,
            "A natural synonym was blocked by an advisory bundled-dictionary term.");

        SmartPhraseTranslationRequest startingNow = CreateRequest(
            "\"Starting from now, the air pressure in here is going to rise to six atmospheres.\"") with
        {
            SemanticTerms =
            [
                new ScaffoldTerm(
                    "now",
                    "сейчас",
                    0.95,
                    ScaffoldTermProvenance.AdvisoryDictionary)
            ]
        };
        SmartPhraseTranslationResult naturalStartingNow = Result(
            startingNow,
            "«Отныне давление воздуха здесь повысится до шести атмосфер»");
        Check(SmartPhraseTranslationGuard.Validate(
                  startingNow,
                  naturalStartingNow).Reason == SmartPhraseRejectionReason.SemanticTermMissing &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  startingNow,
                  naturalStartingNow).Accepted,
            "An advisory time-expression surface blocked a complete natural translation.");

        SmartPhraseTranslationRequest danglingQuote = CreateRequest(
            "\"But it's really funny...");
        SmartPhraseGuardResult strictDanglingQuote = SmartPhraseTranslationGuard.Validate(
            danglingQuote,
            Result(danglingQuote, "«Но это действительно смешно...»"));
        SmartPhraseGuardResult displayDanglingQuote =
            SmartPhraseTranslationGuard.ValidateForDisplay(
                danglingQuote,
                Result(danglingQuote, "«Но это действительно смешно...»"));
        Check(strictDanglingQuote.Reason == SmartPhraseRejectionReason.StructuralMismatch &&
              displayDanglingQuote is
              {
                  Accepted: true,
                  Translation: "Но это действительно смешно..."
              },
            "A balanced Russian wrapper for a dangling OCR quote hid a safe phrase translation.");

        SmartPhraseTranslationRequest danglingQuestion = CreateRequest(
            "\"Why do you think it is necessary to raise the air pressure?");
        SmartPhraseGuardResult displayDanglingQuestion =
            SmartPhraseTranslationGuard.ValidateForDisplay(
                danglingQuestion,
                Result(
                    danglingQuestion,
                    "«Почему вы считаете необходимым повышать давление воздуха?»"));
        Check(displayDanglingQuestion is
              {
                  Accepted: true,
                  Translation: "Почему вы считаете необходимым повышать давление воздуха?"
              },
            "A dangling OCR quote still blocked a complete question translation.");

        SmartPhraseTranslationRequest risingPressure = CreateRequest(
            "\"As the air pressure rises, you may start to feel a strange sensation in your ears.\"");
        Check(SmartPhraseTranslationGuard.ValidateForDisplay(
                  risingPressure,
                  Result(
                      risingPressure,
                      "«По мере повышения давления воздуха вы можете почувствовать странное ощущение в ушах»"))
                .Accepted,
            "A complete accepted air-pressure control sentence regressed.");

        SmartPhraseTranslationRequest senseRequest = request with
        {
            SenseHints =
            [
                new ScaffoldSenseHint(
                    "bank",
                    "land beside water",
                    "берег",
                    ["банк"],
                    0.91)
            ]
        };
        Check(SmartPhraseTranslationGuard.Validate(
                  senseRequest,
                  Result(senseRequest, "Мы зашли в банк.")).Reason ==
              SmartPhraseRejectionReason.SenseConflict,
            "An explicit rejected GlossBERT sense passed the semantic guard.");
        Check(SmartPhraseTranslationGuard.ValidateForDisplay(
                  senseRequest,
                  Result(senseRequest, "Мы зашли в банк.")).Accepted,
            "An advisory sense conflict blocked an otherwise safe display translation.");
        Check(TranslateGemmaTranslationService.IsDisplaySemanticImprovement(
                  senseRequest,
                  "Мы зашли в банк.",
                  "Мы подошли к берегу.") &&
              !TranslateGemmaTranslationService.IsDisplaySemanticImprovement(
                  senseRequest,
                  "Мы подошли к берегу.",
                  "Мы зашли в банк."),
            "Display-safe post-edit selection was not monotonic in semantic conflicts.");

        const string privateSource = "UniquePrivateSentence must never enter logs.";
        SmartPhraseTranslationRequest privateRequest = CreateRequest(privateSource);
        string diagnostic = SmartHoverPipeline.FormatPhraseRejectionDiagnostic(
            "display",
            SmartPhraseRejectionReason.SenseConflict,
            privateRequest,
            Result(privateRequest, "Закрытый перевод."));
        Check(diagnostic.Contains("reason=SenseConflict", StringComparison.Ordinal) &&
              diagnostic.Contains("fingerprint=", StringComparison.Ordinal) &&
              !diagnostic.Contains(privateSource, StringComparison.Ordinal) &&
              !diagnostic.Contains("Закрытый перевод", StringComparison.Ordinal),
            "Phrase rejection diagnostics leaked source or translated text.");

        string invalidPrivateSource = string.Join(
            ' ',
            Enumerable.Repeat(
                "PrivateInvalidWord",
                SmartPhraseContextBuilder.MaximumWords + 1));
        SmartPhraseTranslationRequest invalidPrivateRequest = CreateRequest(
            invalidPrivateSource);
        Check(SmartPhraseTranslationGuard.ValidateRequest(invalidPrivateRequest) ==
              SmartPhraseRejectionReason.SourceOutOfScope,
            "The diagnostic regression fixture unexpectedly became cache-valid.");
        string invalidDiagnostic = SmartHoverPipeline.FormatPhraseRejectionDiagnostic(
            "request",
            SmartPhraseRejectionReason.SourceOutOfScope,
            invalidPrivateRequest,
            result: null);
        Check(invalidDiagnostic.Contains("fingerprint=", StringComparison.Ordinal) &&
              !invalidDiagnostic.Contains(invalidPrivateSource, StringComparison.Ordinal),
            "An invalid request either escaped diagnostic formatting or leaked its source.");

        SmartPhraseTranslationRequest completenessRequest = CreateRequest(
            "One two three four five six seven eight nine ten");
        SmartPhraseTranslationResult incomplete = Result(
            completenessRequest,
            "Слишком коротко.");
        Check(SmartPhraseTranslationGuard.Validate(
                  completenessRequest,
                  incomplete).Reason == SmartPhraseRejectionReason.IncompleteTranslation &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  completenessRequest,
                  incomplete).Reason == SmartPhraseRejectionReason.IncompleteTranslation,
            "A truncated long-sentence translation passed the strict or display guard.");
        Check(SmartPhraseTranslationGuard.Validate(
                  completenessRequest,
                  Result(completenessRequest, "Это достаточно полный перевод.")).Accepted &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  completenessRequest,
                  Result(completenessRequest, "Это достаточно полный перевод.")).Accepted,
            "The conservative completeness boundary rejected a four-word Russian result.");

        SmartPhraseTranslationRequest shortCompletenessRequest = CreateRequest(
            "One two three four five six seven eight nine");
        Check(SmartPhraseTranslationGuard.Validate(
                  shortCompletenessRequest,
                  Result(shortCompletenessRequest, "Кратко.")).Accepted &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  shortCompletenessRequest,
                  Result(shortCompletenessRequest, "Кратко.")).Accepted,
            "The completeness ratio was applied below its ten-word source threshold.");

        SmartPhraseTranslationRequest exactBoundaryRequest = CreateRequest(
            "One two three four five six seven eight nine ten eleven twelve thirteen " +
            "fourteen fifteen sixteen seventeen eighteen nineteen twenty");
        Check(SmartPhraseTranslationGuard.Validate(
                  exactBoundaryRequest,
                  Result(exactBoundaryRequest, "Это ровно семь русских слов для перевода.")).Accepted &&
              SmartPhraseTranslationGuard.ValidateForDisplay(
                  exactBoundaryRequest,
                  Result(exactBoundaryRequest, "Это ровно семь русских слов для перевода.")).Accepted,
            "A Russian result at the exact 35-percent boundary was rejected.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request with { ProperNames = null! },
                  Result(request, "Мы стояли на берегу.")).Reason ==
              SmartPhraseRejectionReason.SourceOutOfScope,
            "Phrase generation was accepted with an invalid proper-name guard input.");
        Check(SmartPhraseTranslationGuard.Validate(
                  request,
                  Result(request, "Мы отдыхали на берегу.") with
                  {
                      ModelIdentity = "different-model"
                  }).Reason == SmartPhraseRejectionReason.ModelIdentityMismatch,
            "Output reported by a different model identity was accepted.");
    }

    private static async Task VerifyPersistentCacheAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-phrase-cache");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "persistent.json");
        SmartPhraseTranslationRequest request = CreateRequest();
        SmartPhraseTranslationRequest advisoryRequest = request with
        {
            SenseHints =
            [
                new ScaffoldSenseHint(
                    "bank",
                    "land beside water",
                    "берег",
                    ["банк"],
                    0.91)
            ]
        };

        await using (SmartPhraseTranslationCache cache = new(path, capacity: 4))
        {
            Check(!await cache.StoreAsync(
                    request,
                    Result(request, "Фраза означает: мы отдыхали у реки.")),
                "A guard-rejected generation was cached.");
            Check(await cache.StoreAsync(
                    request,
                    Result(request, "Мы отдыхали у реки.")),
                "A guarded phrase translation was not cached.");
            Check(await cache.StoreAsync(
                    advisoryRequest,
                    Result(advisoryRequest, "Мы зашли в банк.")),
                "A display-safe translation was rejected from cache by advisory evidence.");
        }

        string json = await File.ReadAllTextAsync(path);
        Check(!json.Contains(request.SourcePhrase, StringComparison.Ordinal) &&
              !json.Contains(request.ModelInputPhrase, StringComparison.Ordinal),
            "The persistent phrase cache leaked source dialogue instead of a SHA key.");
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            Check(document.RootElement.GetProperty("version").GetInt32() ==
                  SmartPhraseTranslationCache.SchemaVersion,
                "The phrase cache persisted an unexpected schema.");
        }

        await using (SmartPhraseTranslationCache reopened = new(path, capacity: 4))
        {
            SmartPhraseTranslationResult? hit = await reopened.TryGetAsync(request);
            Check(hit is { FromCache: true } &&
                  hit.Translation == "Мы отдыхали у реки.",
                "A guarded phrase translation did not survive restart.");
            SmartPhraseTranslationResult? advisoryHit =
                await reopened.TryGetAsync(advisoryRequest);
            Check(advisoryHit is { FromCache: true } &&
                  advisoryHit.Translation == "Мы зашли в банк.",
                "A cached display translation was invalidated by advisory evidence on read.");
        }

        string fingerprint = SmartPhraseTranslationCache.CreateFingerprint(request);
        void CheckDifferent(SmartPhraseTranslationRequest changed, string component) =>
            Check(fingerprint != SmartPhraseTranslationCache.CreateFingerprint(changed),
                $"The phrase-cache key omitted {component}.");

        CheckDifferent(
            request with
            {
                SourcePhrase = "we rested on the bank by the river",
                ModelInputPhrase = "we rested on the bank by the river"
            },
            "the exact source phrase");
        CheckDifferent(
            request with
            {
                ModelInputPhrase = "We relaxed beside the river bank",
                TransformIdentity = "idiom:test@v1"
            },
            "the effective model phrase");
        CheckDifferent(
            request with
            {
                ModelInputPhrase = "We relaxed beside the river bank",
                TransformIdentity = "idiom:test@v2"
            },
            "the transform identity");
        CheckDifferent(request with { ProperNames = ["LeMU"] }, "proper-name whitelist");
        CheckDifferent(request with { PreviousContext = ["She looked away."] },
            "ordered hidden context");
        CheckDifferent(request with
        {
            SemanticTerms = [new ScaffoldTerm("river", "река", 1)]
        }, "semantic terms");
        CheckDifferent(request with
        {
            SenseHints =
            [
                new ScaffoldSenseHint("bank", "land beside water", "берег", ["банк"], 0.9)
            ]
        }, "sense hints");
        CheckDifferent(request with { ModelIdentity = "translategemma:4b-q8" },
            "model identity");
        CheckDifferent(request with { PromptVersion = "direct-en-ru-v2" },
            "prompt version");

        string lruPath = Path.Combine(root, "lru.json");
        SmartPhraseTranslationRequest first = CreateRequest(
            "We rested on the bank by the river");
        SmartPhraseTranslationRequest second = CreateRequest(
            "They stood on the bank near the bridge");
        SmartPhraseTranslationRequest third = CreateRequest(
            "A cottage waited on the bank downstream");
        await using (SmartPhraseTranslationCache cache = new(lruPath, capacity: 2))
        {
            Check(await cache.StoreAsync(first, Result(first, "Мы отдыхали на берегу реки.")),
                "Could not seed the first phrase-cache entry.");
            Check(await cache.StoreAsync(second, Result(second, "Они стояли на берегу у моста.")),
                "Could not seed the second phrase-cache entry.");
            Check(await cache.TryGetAsync(first) is not null,
                "Could not touch the first phrase-cache entry.");
            Check(await cache.StoreAsync(third, Result(third, "Домик стоял ниже по берегу.")),
                "Could not seed the third phrase-cache entry.");
        }

        await using SmartPhraseTranslationCache lru = new(lruPath, capacity: 2);
        Check(await lru.TryGetAsync(first) is not null,
            "The recently used phrase-cache entry was evicted.");
        Check(await lru.TryGetAsync(second) is null,
            "The least recently used phrase-cache entry survived eviction.");
        Check(await lru.TryGetAsync(third) is not null,
            "The newest phrase-cache entry was evicted.");
    }

    private static SmartPhraseTranslationRequest CreateRequest(
        string phrase = "We rested on the bank by the river",
        string? modelInput = null,
        string transformIdentity = SmartPhraseIdiomRewriter.NoTransformIdentity,
        IReadOnlyList<string>? properNames = null)
    {
        return new SmartPhraseTranslationRequest(
            SourcePhrase: phrase,
            ModelInputPhrase: modelInput ?? phrase,
            TransformIdentity: transformIdentity,
            ProperNames: properNames ?? Array.Empty<string>(),
            ModelIdentity: "translategemma:4b-q4_K_M",
            PromptVersion: TranslateGemmaTranslationService.PromptIdentity);
    }

    private static SmartPhraseTranslationResult Result(
        SmartPhraseTranslationRequest request,
        string translation) =>
        new(translation, request.ModelIdentity, ElapsedMilliseconds: 17);

    private static int CountWords(string value) =>
        EnglishContextLexicon.TokenRegex().Matches(value).Count;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task VerifyStructurallyCorruptCacheFailsOpenAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-phrase-corrupt-cache");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "persistent.json");
        SmartPhraseTranslationRequest request = CreateRequest();
        string corruptJson = JsonSerializer.Serialize(new
        {
            version = SmartPhraseTranslationCache.SchemaVersion,
            entries = new object?[]
            {
                null,
                new
                {
                    key = (string?)null,
                    translation = "Перевод",
                    modelIdentity = request.ModelIdentity,
                    elapsedMilliseconds = 0,
                    lastAccess = 1
                },
                new
                {
                    key = new string('0', 64),
                    translation = (string?)null,
                    modelIdentity = request.ModelIdentity,
                    elapsedMilliseconds = 0,
                    lastAccess = 2
                },
                new
                {
                    key = new string('1', 64),
                    translation = "Перевод",
                    modelIdentity = (string?)null,
                    elapsedMilliseconds = 0,
                    lastAccess = 3
                }
            }
        });
        await File.WriteAllTextAsync(path, corruptJson);

        await using (SmartPhraseTranslationCache cache = new(path, capacity: 4))
        {
            Check(await cache.TryGetAsync(request) is null,
                "A structurally corrupt v3 phrase cache did not fail open as a miss.");
            Check(await cache.StoreAsync(
                    request,
                    Result(request, "Мы отдыхали у реки.")),
                "A structurally corrupt v3 phrase cache blocked a subsequent valid generation.");
            await cache.FlushAsync();
        }

        await using SmartPhraseTranslationCache reopened = new(path, capacity: 4);
        SmartPhraseTranslationResult? recovered = await reopened.TryGetAsync(request);
        Check(recovered is
        {
            Translation: "Мы отдыхали у реки.",
            FromCache: true
        }, "The phrase cache did not recover after skipping malformed v3 entries.");
    }

    private sealed class RecordingPhraseTranslator : ISmartPhraseTranslator
    {
        public SmartPhraseTranslationRequest? LastRequest { get; private set; }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(Result(request, "Мы отдыхали на берегу реки."));
        }
    }
}
