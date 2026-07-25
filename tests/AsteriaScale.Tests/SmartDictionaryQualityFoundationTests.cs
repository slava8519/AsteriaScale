using AsteriaScale.Services;

internal static class SmartDictionaryQualityFoundationTests
{
    public static Task RunAsync()
    {
        VerifySurfaceQualityPreference();
        VerifyRegisterNormalization();
        VerifyRegisterFidelity();
        VerifyDurationAgoSurfacePreference();
        VerifyStandaloneSaveSurfacePreference();
        VerifyWhileGrammar();
        VerifyThatGrammar();
        VerifyDoGrammar();
        VerifyStandaloneSaveFastPath();
        return Task.CompletedTask;
    }

    private static void VerifySurfaceQualityPreference()
    {
        SmartDictionarySenseGroup technical = new(
            0,
            "A device used to open or close an electric circuit.",
            "noun",
            [
                new SmartDictionarySenseSurface(0, "выключатель m on/off", ""),
                new SmartDictionarySenseSurface(1, "выключатель", "")
            ]);
        SmartDictionarySurfaceSelection? technicalSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He flipped the switch.",
                "switch",
                15,
                technical);
        Check(technicalSelection is
            {
                Translation: "выключатель",
                Reason: "cleaner Russian dictionary surface"
            },
            $"A technical Latin annotation beat the clean surface: {technicalSelection}");

        SmartDictionarySenseGroup unbalanced = new(
            0,
            "To receive money for work.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "зарабатывать (заработать", ""),
                new SmartDictionarySenseSurface(1, "зарабатывать", "")
            ]);
        SmartDictionarySurfaceSelection? unbalancedSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "They earn enough money.",
                "earn",
                5,
                unbalanced);
        Check(unbalancedSelection is { Translation: "зарабатывать" },
            $"An unbalanced annotation beat the clean surface: {unbalancedSelection}");

        SmartDictionarySenseGroup onlyDirty = technical with
        {
            Surfaces = [technical.Surfaces[0]]
        };
        SmartDictionarySurfaceSelection? failOpen =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He flipped the switch.",
                "switch",
                15,
                onlyDirty);
        Check(failOpen is { Translation: "выключатель m on/off" },
            "Surface validation erased the only available dictionary result.");

        SmartDictionarySenseGroup balancedAnnotation = new(
            0,
            "To put money aside.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "сберегать (деньги)", ""),
                new SmartDictionarySenseSurface(1, "копить", "")
            ]);
        SmartDictionarySurfaceSelection? balancedSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "They store money.",
                "store",
                5,
                balancedAnnotation);
        Check(balancedSelection is { Translation: "сберегать (деньги)" },
            "A balanced explanatory surface was incorrectly treated as malformed.");
    }

    /// <summary>
    /// The sidecar packs several markers into one column, so the parser must
    /// resolve to the strongest and must not invent a register from a semantic
    /// label. Vocabulary is taken from the shipped English sidecar, which uses
    /// exactly seven markers.
    /// </summary>
    private static void VerifyRegisterNormalization()
    {
        Check(SenseRegisters.Parse("груб.") == SenseRegister.Vulgar,
            "The vulgar marker was not recognized.");
        Check(SenseRegisters.Parse("жарг.") == SenseRegister.Slang,
            "The slang marker was not recognized.");
        Check(SenseRegisters.Parse("разг.") == SenseRegister.Colloquial,
            "The colloquial marker was not recognized.");
        Check(SenseRegisters.Parse("устар.") == SenseRegister.Archaic,
            "The archaic marker was not recognized.");
        Check(SenseRegisters.Parse("диал.") == SenseRegister.Dialect,
            "The dialect marker was not recognized.");
        Check(SenseRegisters.Parse("жарг. груб.") == SenseRegister.Vulgar,
            "A combined label did not resolve to its strongest marker.");
        Check(SenseRegisters.Parse("перен.") == SenseRegister.Neutral,
            "A figurative label was misread as a register.");
        Check(SenseRegisters.Parse("") == SenseRegister.Neutral &&
              SenseRegisters.Parse(null) == SenseRegister.Neutral &&
              SenseRegisters.Parse("мед.") == SenseRegister.Neutral,
            "An unmarked or domain-only label must stay neutral.");
        Check(SenseRegisters.DisplayLabel(SenseRegister.Vulgar) == "ГРУБ." &&
              SenseRegisters.DisplayLabel(SenseRegister.Neutral) is null,
            "Register display labels are wrong.");
    }

    /// <summary>
    /// Roadmap #14.2C: the register belongs to the English sense, so the chosen
    /// surface must keep it. The previous policy ranked a coarse marker as a
    /// defect, which softened a sense that Wiktionary had explicitly tagged.
    /// </summary>
    private static void VerifyRegisterFidelity()
    {
        SmartDictionarySenseGroup vulgar = new(
            0,
            "To become extremely angry.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "рассердиться", ""),
                new SmartDictionarySenseSurface(1, "взбеситься", "груб.")
            ])
        {
            Register = SenseRegister.Vulgar
        };
        SmartDictionarySurfaceSelection? kept =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He was about to lose it.",
                "lose",
                16,
                vulgar);
        Check(kept is { Translation: "взбеситься" },
            $"A vulgar sense was softened to a neutral surface: {kept}");

        // The same inventory under a neutral sense must not be strengthened.
        SmartDictionarySenseGroup neutral = vulgar with
        {
            Register = SenseRegister.Neutral
        };
        SmartDictionarySurfaceSelection? plain =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He was about to lose it.",
                "lose",
                16,
                neutral);
        Check(plain is { Translation: "рассердиться" },
            $"A neutral sense was strengthened to a coarse surface: {plain}");

        // Grouping must carry the strongest marker of the rows it merged.
        SmartDictionarySenseInventory inventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "He was about to lose it.",
                "lose",
                16,
                [
                    new DictionarySenseCandidate(
                        "рассердиться", "To become extremely angry.", "", "verb"),
                    new DictionarySenseCandidate(
                        "взбеситься", "To become extremely angry.", "груб.", "verb")
                ]);
        Check(inventory.Groups.Count == 1 &&
              inventory.Groups[0].Register == SenseRegister.Vulgar,
            "A merged sense lost the register marking of its rows.");
    }

    private static void VerifyDurationAgoSurfacePreference()
    {
        SmartDictionarySenseGroup ago = new(
            0,
            "Before now, before the present time.",
            "postp",
            [
                new SmartDictionarySenseSurface(0, "тому назад", ""),
                new SmartDictionarySenseSurface(1, "назад", "")
            ]);
        const string durationContext = "It happened three days ago.";
        SmartDictionarySurfaceSelection? durationSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                durationContext,
                "ago",
                durationContext.IndexOf("ago", StringComparison.Ordinal),
                ago);
        Check(durationSelection is
            {
                Translation: "назад",
                Reason: "duration-before-now construction"
            },
            $"A duration + ago construction kept the awkward source form: {durationSelection}");

        SmartDictionarySurfaceSelection? noContextSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm("Ago", "ago", 0, ago);
        Check(noContextSelection is { Translation: "тому назад" },
            "The ago preference fired without a duration context instead of failing open.");
    }

    private static void VerifyStandaloneSaveSurfacePreference()
    {
        SmartDictionarySenseGroup fileSave = new(
            0,
            "To write a file to disk or other storage medium.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "сохранять", ""),
                new SmartDictionarySenseSurface(1, "сохранить", ""),
                new SmartDictionarySenseSurface(2, "записать", "")
            ]);
        SmartDictionarySurfaceSelection? uiSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm("Save", "Save", 0, fileSave);
        Check(uiSelection is
            {
                Translation: "сохранить",
                Reason: "standalone save command"
            },
            $"A standalone Save label did not prefer the command form: {uiSelection}");

        const string sentence = "They save files daily.";
        SmartDictionarySurfaceSelection? sentenceSelection =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                sentence,
                "save",
                sentence.IndexOf("save", StringComparison.Ordinal),
                fileSave);
        Check(sentenceSelection is { Translation: "сохранять" },
            "The UI-only Save preference leaked into normal sentence context.");
    }

    private static void VerifyWhileGrammar()
    {
        DictionarySenseCandidate[] candidates =
        [
            new("время", "An uncertain duration of time.", "", "noun"),
            new("промежуток времени", "An uncertain duration of time.", "", "noun"),
            new("пока", "During the same time that.", "", "conj"),
            new("хотя", "Although.", "", "conj")
        ];
        const string conjunctionContext = "Wait here while I call her.";
        SmartDictionarySenseInventory conjunctionInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                conjunctionContext,
                "while",
                conjunctionContext.IndexOf("while", StringComparison.Ordinal),
                candidates);
        Check(conjunctionInventory is
            {
                InferredPartOfSpeech: "conj",
                PartOfSpeechFilterApplied: true
            } && conjunctionInventory.Groups.All(group => group.PartOfSpeech == "conj"),
            "A clause-introducing while retained its noun sense inventory.");

        const string nounContext = "I waited for a while.";
        SmartDictionarySenseInventory nounInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                nounContext,
                "while",
                nounContext.IndexOf("while", StringComparison.Ordinal),
                candidates);
        SmartDictionaryRankerPlan nounPlan =
            SmartDictionarySenseResolver.CreateRankerPlan(
                nounContext,
                "while",
                nounContext.IndexOf("while", StringComparison.Ordinal),
                nounInventory.Groups);
        SmartDictionaryHeuristicSelection? nounSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                nounContext,
                "while",
                nounContext.IndexOf("while", StringComparison.Ordinal),
                nounPlan.Outcomes.Select(outcome => outcome.Translation).ToArray(),
                "English");
        Check(nounInventory.InferredPartOfSpeech == "noun" &&
              !nounInventory.PartOfSpeechFilterApplied &&
              nounSelection is { Translation: "время" },
            $"The narrow noun-while path did not resolve the visible result: {nounSelection}");

        SmartDictionaryHeuristicSelection? uncertain =
            SmartDictionaryHeuristicRanker.TrySelect(
                "While perhaps true, it is unhelpful.",
                "while",
                0,
                ["время", "пока", "хотя"],
                "English");
        Check(uncertain is null,
            "An uncertain while context was forced through the grammar fast path.");
    }

    private static void VerifyThatGrammar()
    {
        DictionarySenseCandidate[] candidates =
        [
            new("чтобы", "Introducing a purpose clause.", "", "conj"),
            new("что", "Connecting a noun clause.", "", "conj"),
            new("тот", "The thing indicated from context.", "", "det"),
            new("это", "That thing.", "", "pron"),
            new("настолько", "To a great extent.", "", "adv")
        ];
        const string clauseContext = "I know that he left.";
        SmartDictionarySenseInventory clauseInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                clauseContext,
                "that",
                clauseContext.IndexOf("that", StringComparison.Ordinal),
                candidates);
        SmartDictionaryRankerPlan clausePlan =
            SmartDictionarySenseResolver.CreateRankerPlan(
                clauseContext,
                "that",
                clauseContext.IndexOf("that", StringComparison.Ordinal),
                clauseInventory.Groups);
        SmartDictionaryHeuristicSelection? clauseSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                clauseContext,
                "that",
                clauseContext.IndexOf("that", StringComparison.Ordinal),
                clausePlan.Outcomes.Select(outcome => outcome.Translation).ToArray(),
                "English");
        Check(clauseInventory is
            {
                InferredPartOfSpeech: "conj",
                PartOfSpeechFilterApplied: true
            } && clauseSelection is { Translation: "что" },
            $"A content-clause that was not resolved conservatively: {clauseSelection}");

        const string determinerContext = "That book belongs to me.";
        SmartDictionarySenseInventory determinerInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                determinerContext,
                "That",
                0,
                candidates);
        SmartDictionaryRankerPlan determinerPlan =
            SmartDictionarySenseResolver.CreateRankerPlan(
                determinerContext,
                "That",
                0,
                determinerInventory.Groups);
        SmartDictionaryHeuristicSelection? determinerSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                determinerContext,
                "That",
                0,
                determinerPlan.Outcomes.Select(outcome => outcome.Translation).ToArray(),
                "English");
        Check(determinerInventory.InferredPartOfSpeech == "det" &&
              !determinerInventory.PartOfSpeechFilterApplied &&
              determinerSelection is { Translation: "тот" },
            $"A demonstrative determiner was not resolved safely: {determinerSelection}");

        const string discourseContext = "That said, we should leave.";
        SmartDictionarySenseInventory discourseInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                discourseContext,
                "That",
                0,
                candidates);
        SmartDictionaryHeuristicSelection? discourseSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                discourseContext,
                "That",
                0,
                ["чтобы", "что", "тот", "это", "настолько"],
                "English");
        Check(discourseInventory.InferredPartOfSpeech != "det" &&
              discourseSelection is null,
            "The discourse marker 'That said' was forced into a determiner result.");

        SmartDictionaryHeuristicSelection? uncertain =
            SmartDictionaryHeuristicRanker.TrySelect(
                "I doubt that perhaps.",
                "that",
                8,
                ["чтобы", "что", "тот", "это", "настолько"],
                "English");
        Check(uncertain is null,
            "An incomplete that-clause was forced through the grammar fast path.");
    }

    private static void VerifyDoGrammar()
    {
        string[] candidates =
            ["не", "же", "делать", "подходить", "сидеть", "трахать", "ли", "вечеринка"];

        const string negativeContext = "I do not know.";
        SmartDictionaryHeuristicSelection? negative =
            SmartDictionaryHeuristicRanker.TrySelect(
                negativeContext,
                "do",
                negativeContext.IndexOf("do", StringComparison.Ordinal),
                candidates,
                "English");
        Check(negative is { Translation: "не" },
            $"Negative auxiliary do was not separated from lexical do: {negative}");

        const string lexicalContext = "I do the work.";
        SmartDictionaryHeuristicSelection? lexical =
            SmartDictionaryHeuristicRanker.TrySelect(
                lexicalContext,
                "do",
                lexicalContext.IndexOf("do", StringComparison.Ordinal),
                candidates,
                "English");
        Check(lexical is { Translation: "делать" },
            $"Lexical do with a work object was not selected: {lexical}");

        const string questionContext = "Do you know?";
        SmartDictionaryHeuristicSelection? question =
            SmartDictionaryHeuristicRanker.TrySelect(
                questionContext,
                "Do",
                0,
                candidates,
                "English");
        Check(question is { Translation: "ли" },
            $"Question auxiliary do was not resolved: {question}");

        SmartDictionaryHeuristicSelection? uncertain =
            SmartDictionaryHeuristicRanker.TrySelect(
                "I do magic.",
                "do",
                2,
                candidates,
                "English");
        Check(uncertain is null,
            "An unrecognized do-object was forced through the lexical fast path.");
    }

    private static void VerifyStandaloneSaveFastPath()
    {
        string[] candidates = ["спасать", "копить", "сохранить", "экономить", "сейв", "Саве"];
        SmartDictionaryHeuristicSelection? standalone =
            SmartDictionaryHeuristicRanker.TrySelect(
                "Save",
                "Save",
                0,
                candidates,
                "English");
        Check(standalone is { Translation: "сохранить" },
            $"A standalone Save label did not select the file command: {standalone}");

        SmartDictionaryHeuristicSelection? sentence =
            SmartDictionaryHeuristicRanker.TrySelect(
                "Save Alice now!",
                "Save",
                0,
                candidates,
                "English");
        Check(sentence is null,
            "The standalone Save UI prior leaked into a rescue sentence.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
