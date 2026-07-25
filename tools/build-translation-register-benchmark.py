#!/usr/bin/env python3
"""Authors assets/Translation/translation-register-benchmark.json.

Register fidelity on free-form translation cannot be judged by string equality:
a reply has many valid renderings. Each case therefore carries stem anchors.

  faithful   - at least one must appear; its presence means the register survived
  softened   - any appearance means the line was sanitised (the failure the
               roadmap card is about: a blunt original rendered polite)
  strengthened - any appearance means coarseness was invented that the source
               does not have; only meaningful on plain sources

Stems are prefixes so ordinary inflection is tolerated without a morphology
pass. Sources are original CC0 lines written for Asteria Scale.
"""
import json
import collections
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "assets/Translation/translation-register-benchmark.json"

# (id, bucket, source, reference, expectedRegister, faithful, softened, strengthened)
CASES = [
    # ---- coarse, non-sexual: swearing, insults, intensifiers ----------------
    ("reg-v-01", "neutral-vulgar", "Fuck off and don't come back.",
     "Отъебись и не возвращайся.", "vulgar",
     ["отъеб", "иди на", "пошёл на", "пошел на", "съеб", "отвали"], ["уйди", "оставь меня", "прошу"], []),
    ("reg-v-02", "neutral-vulgar", "That bastard took the last of my money.",
     "Этот ублюдок забрал последние мои деньги.", "vulgar",
     ["ублюд", "сволоч", "мерзав", "гад", "подон"], ["человек", "господин"], []),
    ("reg-v-03", "neutral-vulgar", "What the hell is wrong with you?",
     "Какого чёрта с тобой не так?", "vulgar",
     ["чёрт", "черт", "хрен", "хера"], ["что случилось", "извини"], []),
    ("reg-v-04", "neutral-vulgar", "This is complete bullshit and you know it.",
     "Это полное дерьмо, и ты это знаешь.", "vulgar",
     ["дерьм", "хрень", "чушь", "херн", "брехн"], ["неправда", "ошибка"], []),
    ("reg-v-05", "neutral-vulgar", "Shut your damn mouth for once.",
     "Закрой уже свой поганый рот.", "vulgar",
     ["поган", "чёртов", "чертов", "долба", "хренов"], ["пожалуйста", "будьте добры"], []),
    ("reg-v-06", "neutral-vulgar", "He is a lazy piece of shit.",
     "Он ленивый кусок дерьма.", "vulgar",
     ["дерьм", "говн", "мраз"], ["неприятный", "ленивый человек"], []),
    ("reg-v-07", "neutral-vulgar", "I don't give a damn what they think.",
     "Мне плевать, что они думают.", "vulgar",
     ["плева", "насра", "похер", "пофиг"], ["не важно", "безразлично мне"], []),
    ("reg-v-08", "neutral-vulgar", "Get your ass over here right now.",
     "Тащи свою задницу сюда немедленно.", "vulgar",
     ["задниц", "жопу", "жоп"], ["подойди", "пожалуйста"], []),
    ("reg-v-09", "neutral-vulgar", "The whole plan went to hell.",
     "Весь план полетел к чёрту.", "vulgar",
     ["к чёрт", "к черт", "коту под хвост", "псу под хвост"], ["не удался", "провалился"], []),
    ("reg-v-10", "neutral-vulgar", "Stop being such an idiot about it.",
     "Хватит вести себя как идиот.", "colloquial",
     ["идиот", "дурак", "болван", "придур"], ["неразумно"], ["мудак", "хуйл", "долбоёб"]),

    # ---- sexual content that must not be sanitised --------------------------
    ("reg-s-01", "sexual-ordinary", "She wanted him inside her.",
     "Она хотела его в себе.", "sexual-domain",
     ["в себе", "внутри", "войти"], ["обнять", "быть рядом", "поцелов"], []),
    ("reg-s-02", "sexual-ordinary", "They slept together after the festival.",
     "После фестиваля они переспали.", "colloquial",
     ["переспал", "спали вместе", "провели ночь"], ["подружились", "поговорили"], []),
    ("reg-s-03", "sexual-ordinary", "He could not stop staring at her breasts.",
     "Он не мог оторвать взгляд от её груди.", "neutral",
     ["груд", "бюст"], ["лицо", "глаза"], ["сиськ", "буфер"]),
    ("reg-s-04", "sexual-ordinary", "I want to fuck you right here.",
     "Я хочу трахнуть тебя прямо здесь.", "vulgar",
     ["трахн", "трахат", "выеб", "отыме"], ["любить", "быть с тобой", "обнять"], []),
    ("reg-s-05", "sexual-ordinary", "She came with a shudder and went quiet.",
     "Она кончила, вздрогнув, и затихла.", "vulgar",
     ["кончил", "оргазм", "спустил"], ["уснула", "успокоилась", "пришла"], []),
    ("reg-s-06", "sexual-ordinary", "His hand slid under her skirt.",
     "Его рука скользнула ей под юбку.", "neutral",
     ["под юбк", "юбку"], ["за руку", "по плечу"], []),
    ("reg-s-07", "sexual-ordinary", "We were naked before the door closed.",
     "Мы были голыми ещё до того, как закрылась дверь.", "neutral",
     ["гол", "раздет", "нагими"], ["одеты", "готовы"], []),
    ("reg-s-08", "sexual-ordinary", "He paid a whore in the next town.",
     "Он заплатил шлюхе в соседнем городе.", "vulgar",
     ["шлюх", "проститут", "блядь", "потаску"], ["девушке", "женщине", "знакомой"], []),
    ("reg-s-09", "sexual-ordinary", "That was the best sex of my life.",
     "Это был лучший секс в моей жизни.", "neutral",
     ["секс", "близост"], ["вечер", "свидание", "время"], ["еб", "трах"]),
    ("reg-s-10", "sexual-ordinary", "She kissed him on the cheek and left.",
     "Она поцеловала его в щёку и ушла.", "neutral",
     ["поцелов", "щёк", "щек"], [], ["трах", "переспал", "еб"]),

    # ---- slang and colloquial that must not become formal -------------------
    ("reg-c-01", "slang-colloquial", "Gonna grab some grub, wanna come?",
     "Схожу перекусить, пойдёшь?", "colloquial",
     ["перекус", "пожрат", "пойдёшь", "пойдешь", "хаваем"], ["не желаете ли", "приглашаю вас"], []),
    ("reg-c-02", "slang-colloquial", "That party was a total blast.",
     "Вечеринка была просто улёт.", "colloquial",
     ["улёт", "улет", "отпад", "огонь", "круто", "класс"], ["мероприятие прошло успешно"], []),
    ("reg-c-03", "slang-colloquial", "Dude, chill out, it's not a big deal.",
     "Чувак, расслабься, дело житейское.", "slang",
     ["чувак", "мужик", "братан", "расслаб", "остынь"], ["уважаемый", "сударь", "успокойтесь"], []),
    ("reg-c-04", "slang-colloquial", "He blew all his dough on that car.",
     "Он спустил все бабки на эту тачку.", "slang",
     ["бабки", "бабл", "тачк", "спустил", "просад"], ["денежные средства", "автомобиль"], []),
    ("reg-c-05", "slang-colloquial", "Ain't nobody got time for that.",
     "Ни у кого нет на это времени.", "colloquial",
     ["ни у кого", "некогда", "нет времени"], ["не располагаем возможностью"], []),
    ("reg-c-06", "slang-colloquial", "Quit messing around and help me.",
     "Хватит валять дурака, помоги мне.", "colloquial",
     ["валять дурак", "дурить", "балда", "хватит"], ["прекратите", "будьте любезны"], []),
    ("reg-c-07", "slang-colloquial", "My folks are coming over tonight.",
     "Мои предки сегодня приедут.", "colloquial",
     ["предки", "родител", "наши"], ["родственники прибудут"], []),
    ("reg-c-08", "slang-colloquial", "That guy is seriously creepy.",
     "Этот тип реально жуткий.", "colloquial",
     ["тип", "мужик", "жутк", "стрём", "стрем"], ["данный господин", "неприятен"], []),
    ("reg-c-09", "slang-colloquial", "We crashed at his crib after the show.",
     "После концерта мы завалились к нему на хату.", "slang",
     ["хату", "хате", "завалил", "переночев"], ["в его квартире", "разместились"], []),
    ("reg-c-10", "slang-colloquial", "I'm beat, let's call it a night.",
     "Я вымотался, давай закругляться.", "colloquial",
     ["вымот", "устал", "закругл", "по домам"], ["утомлён", "предлагаю завершить"], []),

    # ---- masked spellings in the source -------------------------------------
    ("reg-m-01", "masked-euphemistic", "What the f*ck was that noise?",
     "Что это, блядь, был за шум?", "vulgar",
     ["блядь", "бля", "чёрт", "черт", "хрен", "какого хера"], ["что это было"], []),
    ("reg-m-02", "masked-euphemistic", "This is complete bulls**t.",
     "Это полная херня.", "vulgar",
     ["херн", "дерьм", "чушь", "хрень"], ["неверно", "ошибочно"], []),
    ("reg-m-03", "masked-euphemistic", "He called her a b*tch and walked out.",
     "Он назвал её сукой и ушёл.", "vulgar",
     ["сук", "стерв", "тварь"], ["грубо", "нехорошо"], []),
    ("reg-m-04", "masked-euphemistic", "Get the f**k out of my house.",
     "Убирайся на хер из моего дома.", "vulgar",
     ["на хер", "нахер", "вон", "проваливай", "убирайся"], ["покиньте", "пожалуйста"], []),
    ("reg-m-05", "masked-euphemistic", "That was a sh*tty thing to do.",
     "Это было дерьмовым поступком.", "vulgar",
     ["дерьмов", "паскуд", "подл", "хренов"], ["некрасиво", "неправильно"], []),
    ("reg-m-06", "masked-euphemistic", "I don't care what the f*ck he thinks.",
     "Мне насрать, что он там думает.", "vulgar",
     ["насра", "плева", "похер", "пофиг"], ["не важно", "безразлично"], []),
    ("reg-m-07", "masked-euphemistic", "She is one hell of a b*tch when angry.",
     "В гневе она та ещё стерва.", "vulgar",
     ["стерв", "сук", "мегер"], ["раздражительна", "неприятна"], []),
    ("reg-m-08", "masked-euphemistic", "You look like sh*t this morning.",
     "Ты сегодня выглядишь дерьмово.", "vulgar",
     ["дерьмов", "херов", "погано", "хреново"], ["неважно", "устало"], []),
    ("reg-m-09", "masked-euphemistic", "The whole f*cking building shook.",
     "Всё чёртово здание тряхнуло.", "vulgar",
     # No softened marker: here the failure is the *absence* of an intensifier,
     # which the faithful list already detects. A marker that is a substring of
     # the correct rendering would fire on every answer, right or wrong.
     ["чёртов", "чертов", "долбан", "хренов", "ёбан"], [], []),
    ("reg-m-10", "masked-euphemistic", "He is a complete a**hole about it.",
     "Он ведёт себя как полный мудак.", "vulgar",
     ["муда", "козёл", "козел", "урод", "гнид"], ["невежлив", "неприятен"], []),
]


def main() -> None:
    ids = [case[0] for case in CASES]
    if len(set(ids)) != len(ids):
        raise SystemExit("duplicate case id")

    problems = []
    for case_id, bucket, source, reference, register, faithful, softened, strengthened in CASES:
        if not source.strip() or not reference.strip():
            problems.append(f"{case_id}: empty source or reference")
        if not faithful:
            problems.append(f"{case_id}: no faithful anchor, register cannot be judged")
        overlap = set(faithful) & set(softened)
        if overlap:
            problems.append(f"{case_id}: anchor in both faithful and softened: {overlap}")
        # A faithful anchor must actually appear in the authored reference,
        # otherwise the case asserts a register its own reference does not show.
        lowered = reference.lower().replace("ё", "ё")
        if not any(stem.lower() in lowered for stem in faithful):
            problems.append(f"{case_id}: reference matches no faithful anchor")
        if any(stem.lower() in lowered for stem in softened):
            problems.append(f"{case_id}: reference matches a softened marker")
        if any(stem.lower() in lowered for stem in strengthened):
            problems.append(f"{case_id}: reference matches a strengthened marker")

    if problems:
        for problem in problems:
            print("  REJECT", problem)
        raise SystemExit(f"{len(problems)} problem(s)")

    counts = collections.Counter(case[1] for case in CASES)
    document = {
        "name": "Asteria Scale EN-RU full-text register fidelity set",
        "version": 1,
        "languagePair": "en-ru",
        "license": "CC0-1.0",
        "origin": (
            "Original replies written for Asteria Scale; no corpus lines were copied. "
            "Register is judged by stem anchors rather than string equality, because a "
            "reply has many valid renderings. Ethnic and sexuality slurs are deliberately "
            "excluded."
        ),
        "cases": [
            {
                "id": case_id,
                "bucket": bucket,
                "source": source,
                "sourceLanguage": "en",
                "reference": reference,
                "expectedRegister": register,
                "faithfulAnchors": faithful,
                "softenedMarkers": softened,
                "strengthenedMarkers": strengthened,
                "termAnchors": [],
            }
            for case_id, bucket, source, reference, register, faithful, softened, strengthened in CASES
        ],
    }
    TARGET.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"verified {len(CASES)} cases")
    for bucket, count in sorted(counts.items()):
        print(f"  {bucket}: {count}")
    print(f"wrote {TARGET.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
