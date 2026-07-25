"""Authors ranker-register-benchmark.json and validates it against the shipped
dictionary. Every expected translation and register is taken from the real
sense inventory, never invented: a case the dictionary cannot represent is a
broken oracle, not a hard case.

Ethnic and sexuality slurs present in the source are deliberately excluded.
They exercise nothing the ordinary coarse and sexual vocabulary below does not
already exercise, and the buckets in roadmap 14.2C do not call for them.
"""
import gzip
import json
import collections
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SIDECAR = ROOT / "assets/Dictionaries/eng-rus-enwiktionary-senses.tsv.gz"

# (id, bucket, context, target, expected translation, expected register)
CASES = [
    # ---- neutral <-> vulgar: the marked reading is coarse, not sexual --------
    ("hell-place", "neutral-vulgar", "The priest spoke of hell and judgement.", "hell", "ад", "neutral"),
    ("hell-curse", "neutral-vulgar", "Oh, hell! I forgot the keys again.", "hell", "чёрт", "vulgar"),
    ("damn-condemn", "neutral-vulgar", "The court will damn him for his crimes.", "damn", "проклинать", "slang"),
    ("damn-curse", "neutral-vulgar", "Damn! The engine died on the bridge.", "damn", "чёрт побери", "vulgar"),
    ("crap-junk", "neutral-vulgar", "He threw all that crap into a box.", "crap", "фигня", "vulgar"),
    ("crap-nonsense", "neutral-vulgar", "That is complete crap and you know it.", "crap", "чепуха", "vulgar"),
    ("ass-animal", "neutral-vulgar", "The old ass carried water from the well.", "ass", "осёл", "neutral"),
    ("ass-body", "neutral-vulgar", "He landed flat on his ass in the mud.", "ass", "жопа", "vulgar"),
    ("bugger-person", "neutral-vulgar", "The word bugger once named a sodomite.", "bugger", "содомит", "neutral"),
    ("bugger-ruin", "neutral-vulgar", "Do not bugger the machine any further.", "bugger", "ломать", "vulgar"),
    ("git-fetch", "neutral-vulgar", "Go git the rope from the shed.", "git", "приносить", "neutral"),
    ("git-insult", "neutral-vulgar", "That lazy git never answers the door.", "git", "мерзавец", "slang"),
    ("balls-sport", "neutral-vulgar", "The children lost two balls in the river.", "balls", "мяч", "neutral"),
    ("balls-anatomy", "neutral-vulgar", "The kick caught him right in the balls.", "balls", "яйца", "neutral"),
    ("bread-food", "neutral-vulgar", "She baked bread for the whole village.", "bread", "хлеб", "neutral"),
    ("bread-money", "neutral-vulgar", "He blew all his bread on that car.", "bread", "бабки", "slang"),
    ("dope-drug", "neutral-vulgar", "The police found dope in the trunk.", "dope", "наркотик", "neutral"),
    ("dope-cool", "neutral-vulgar", "That new track is absolutely dope.", "dope", "клёвый", "slang"),
    ("beef-meat", "neutral-vulgar", "The stew needs more beef and salt.", "beef", "говядина", "slang"),
    ("gut-belly", "neutral-vulgar", "He patted his gut and laughed.", "gut", "брюхо", "neutral"),
    ("guts-courage", "neutral-vulgar", "She had the guts to say it aloud.", "guts", "храбрость", "neutral"),
    ("guts-entrails", "neutral-vulgar", "The fisherman pulled out the guts.", "guts", "кишки", "neutral"),
    ("dude-man", "neutral-vulgar", "Some dude was waiting by the gate.", "dude", "мужик", "colloquial"),
    ("dude-address", "neutral-vulgar", "Dude, you have to see this.", "dude", "чувак", "slang"),
    ("bro-sibling", "neutral-vulgar", "My bro is two years older than me.", "bro", "братан", "slang"),
    ("crib-bed", "neutral-vulgar", "The baby slept in a wooden crib.", "crib", "детская кроватка", "neutral"),
    ("crib-home", "neutral-vulgar", "Come by my crib after the show.", "crib", "хата", "slang"),
    ("grass-lawn", "neutral-vulgar", "The grass needs cutting again.", "grass", "трава", "neutral"),
    ("grass-drug", "neutral-vulgar", "They were smoking grass behind the shed.", "grass", "травка", "slang"),
    ("dough-baking", "neutral-vulgar", "Knead the dough until it is smooth.", "dough", "тесто", "neutral"),

    # ---- sexual <-> ordinary -------------------------------------------------
    ("come-move", "sexual-ordinary", "Please come closer to the fire.", "come", "приходить", "neutral"),
    ("come-arrive", "sexual-ordinary", "They come to the village every spring.", "come", "прибывать", "neutral"),
    ("come-orgasm", "sexual-ordinary", "She felt him come inside her.", "come", "кончать", "vulgar"),
    ("blow-air", "sexual-ordinary", "Blow gently on the hot soup.", "blow", "дуть", "neutral"),
    ("blow-hit", "sexual-ordinary", "The blow knocked him off his feet.", "blow", "удар", "neutral"),
    ("blow-oral", "sexual-ordinary", "She offered to blow him in the car.", "blow", "сосать", "vulgar"),
    ("blow-squander", "sexual-ordinary", "Do not blow your savings on that.", "blow", "продувать", "slang"),
    ("bang-noise", "sexual-ordinary", "The door shut with a loud bang.", "bang", "удар", "neutral"),
    ("bang-hair", "sexual-ordinary", "Her bang covered half her forehead.", "bang", "чёлка", "neutral"),
    ("bang-sex", "sexual-ordinary", "He bragged about who he would bang.", "bang", "трахать", "vulgar"),
    ("box-container", "sexual-ordinary", "Put the letters back in the box.", "box", "коробка", "neutral"),
    ("box-theatre", "sexual-ordinary", "They watched from the upper box.", "box", "ложа", "neutral"),
    ("box-anatomy", "sexual-ordinary", "His hand slid toward her box.", "box", "манда", "vulgar"),
    ("bush-plant", "sexual-ordinary", "A rabbit hid under the bush.", "bush", "куст", "neutral"),
    ("bush-hair", "sexual-ordinary", "She had not trimmed her bush in weeks.", "bush", "волосня", "vulgar"),
    ("crack-fissure", "sexual-ordinary", "A crack ran across the ceiling.", "crack", "трещина", "neutral"),
    ("crack-joke", "sexual-ordinary", "He made a crack about her hat.", "crack", "острота", "neutral"),
    ("crack-anatomy", "sexual-ordinary", "He stared at the crack between her legs.", "crack", "щель", "vulgar"),
    ("muff-warmer", "sexual-ordinary", "She warmed her hands in a fur muff.", "muff", "муфта", "neutral"),
    ("muff-anatomy", "sexual-ordinary", "His mouth moved down to her muff.", "muff", "мохнатка", "vulgar"),
    ("spunk-courage", "sexual-ordinary", "The girl showed real spunk today.", "spunk", "отвага", "neutral"),
    ("spunk-semen", "sexual-ordinary", "There was spunk on the sheets.", "spunk", "конча", "vulgar"),
    ("sucker-fish", "sexual-ordinary", "The sucker clung to the glass wall.", "sucker", "сосун", "neutral"),
    ("sucker-fool", "sexual-ordinary", "They played him for a sucker.", "sucker", "дурить", "neutral"),
    ("heat-warmth", "sexual-ordinary", "The heat of the stove filled the room.", "heat", "тепло", "neutral"),
    ("heat-estrus", "sexual-ordinary", "The mare came into heat that week.", "heat", "течка", "neutral"),
    ("heat-arouse", "sexual-ordinary", "Her voice alone could heat him.", "heat", "возбуждать", "slang"),
    ("head-body", "sexual-ordinary", "He rested his head on the table.", "head", "голова", "neutral"),
    ("head-leader", "sexual-ordinary", "The head of the guild refused.", "head", "директор", "neutral"),
    ("ass-sex", "sexual-ordinary", "He only ever thinks about ass.", "ass", "ебля", "vulgar"),

    # ---- slang / contractions / prostorechie --------------------------------
    ("cool-temp", "slang-colloquial", "The evening air felt cool and damp.", "cool", "прохладный", "neutral"),
    ("cool-calm", "slang-colloquial", "He stayed cool through the whole mess.", "cool", "хладнокровный", "neutral"),
    ("cool-fashion", "slang-colloquial", "That jacket is really cool.", "cool", "крутой", "neutral"),
    ("cool-audacious", "slang-colloquial", "It was a cool piece of impudence.", "cool", "дерзкий", "archaic"),
    ("fly-insect", "slang-colloquial", "A fly kept circling the lamp.", "fly", "муха", "neutral"),
    ("fly-travel", "slang-colloquial", "We fly to the coast tomorrow.", "fly", "летать", "neutral"),
    ("fly-sharp", "slang-colloquial", "That kid is fly and knows it.", "fly", "сообразительный", "slang"),
    ("fly-work", "slang-colloquial", "That excuse will not fly here.", "fly", "проходить", "colloquial"),
    ("buck-deer", "slang-colloquial", "A buck stepped out of the treeline.", "buck", "самец", "neutral"),
    ("buck-money", "slang-colloquial", "It only cost me a buck.", "buck", "бакс", "neutral"),
    ("buck-dandy", "slang-colloquial", "The old buck wore a velvet coat.", "buck", "щёголь", "archaic"),
    ("buck-resist", "slang-colloquial", "He will buck any order he dislikes.", "buck", "сопротивляться", "neutral"),
    ("ice-frozen", "slang-colloquial", "The ice cracked under his boot.", "ice", "лёд", "neutral"),
    ("ice-drug", "slang-colloquial", "They were selling ice on that corner.", "ice", "айс", "slang"),
    ("gig-concert", "slang-colloquial", "The band has a gig on Friday.", "gig", "концерт", "neutral"),
    ("gig-oddjob", "slang-colloquial", "He took a gig driving at night.", "gig", "халтура", "neutral"),
    ("hustle-hurry", "slang-colloquial", "You had better hustle to the station.", "hustle", "спешить", "neutral"),
    ("hustle-swindle", "slang-colloquial", "They tried to hustle the old man.", "hustle", "обманывать", "neutral"),
    ("dig-earth", "slang-colloquial", "They dig a trench along the fence.", "dig", "копать", "neutral"),
    ("gal-girl", "slang-colloquial", "A young gal answered the door.", "gal", "девушка", "neutral"),
    ("guy-man", "slang-colloquial", "That guy fixed the roof for us.", "guy", "парень", "neutral"),
    ("guy-people", "slang-colloquial", "Come on, guys, we are late.", "guys", "люди", "neutral"),
    ("bloke-man", "slang-colloquial", "Some bloke asked about you.", "bloke", "мужик", "neutral"),
    ("buddy-address", "slang-colloquial", "Easy there, buddy.", "buddy", "дружок", "neutral"),
    ("chap-man", "slang-colloquial", "A quiet chap moved in downstairs.", "chap", "мужик", "neutral"),
    ("chap-skin", "slang-colloquial", "The cold will chap your hands.", "chap", "трескаться", "neutral"),
    ("chap-jaw", "slang-colloquial", "He struck him under the chap.", "chap", "челюсть", "archaic"),
    ("folks-people", "slang-colloquial", "The folks of that valley kept to themselves.", "folks", "народ", "archaic"),
    ("gas-fuel", "slang-colloquial", "We need gas before the pass.", "gas", "бензин", "neutral"),
    ("gas-state", "slang-colloquial", "The gas escaped through the valve.", "gas", "газ", "neutral"),

    # ---- masked / euphemistic forms -----------------------------------------
    ("masked-fck-verb", "masked-euphemistic", "He told them to f*ck off.", "f*ck", "трахать", "vulgar"),
    ("masked-fck-act", "masked-euphemistic", "It was a quick f*ck and nothing more.", "f*ck", "ебля", "vulgar"),
    ("masked-fck-person", "masked-euphemistic", "That f*ck stole my wallet.", "f*ck", "долбоёб", "vulgar"),
    ("masked-fck-intj", "masked-euphemistic", "F*ck, the door is locked.", "f*ck", "блядь", "vulgar"),
    ("masked-fkk-verb", "masked-euphemistic", "Do not f**k with the wiring.", "f**k", "трахать", "vulgar"),
    ("masked-fkk-partner", "masked-euphemistic", "She was just a f**k to him.", "f**k", "ёбарь", "vulgar"),
    ("masked-cnt", "masked-euphemistic", "He used the word c*nt without thinking.", "c*nt", "пизда", "vulgar"),
    ("masked-bollocks-anatomy", "masked-euphemistic", "The dog bit him in the b****cks.", "b****cks", "яйца", "vulgar"),
    ("masked-bollocks-nonsense", "masked-euphemistic", "That report is complete b****cks.", "b****cks", "ерунда", "vulgar"),
    ("masked-bollocks-idiot", "masked-euphemistic", "The b****cks forgot the tickets.", "b****cks", "придурок", "vulgar"),
    ("masked-bloody-blood", "masked-euphemistic", "His bl**dy shirt lay on the floor.", "bl**dy", "окровавленный", "neutral"),
    ("masked-bloody-battle", "masked-euphemistic", "It was a bl**dy campaign.", "bl**dy", "кровавый", "neutral"),
    ("masked-bloody-intens", "masked-euphemistic", "Where is my bl**dy coat?", "bl**dy", "чёртовый", "vulgar"),
    ("masked-dogshit", "masked-euphemistic", "He stepped in dogs**t on the path.", "dogs**t", "собачье дерьмо", "vulgar"),
    ("masked-god", "masked-euphemistic", "The scribe wrote g*d instead of the name.", "g*d", "Б-г", "neutral"),
    ("masked-shit-feces", "masked-euphemistic", "There was s**t all over the floor.", "s**t", "дерьмо", "vulgar"),
    ("masked-shit-junk", "masked-euphemistic", "Clear this s**t off the table.", "s**t", "хлам", "vulgar"),
    ("masked-shit-stuff", "masked-euphemistic", "Pack your s**t and go.", "s**t", "шмотки", "vulgar"),
    ("masked-shit-best", "masked-euphemistic", "That record is the s**t.", "s**t", "класс", "vulgar"),
    ("masked-shit-nonsense", "masked-euphemistic", "Everything he said was s**t.", "s**t", "чушь", "vulgar"),
    ("masked-shit-person", "masked-euphemistic", "That s**t left without paying.", "s**t", "гнида", "vulgar"),
    ("masked-shithead", "masked-euphemistic", "The s**thead cut me off again.", "s**thead", "мудак", "vulgar"),
    ("masked-shithole", "masked-euphemistic", "They live in a s**thole near the docks.", "s**thole", "жопа", "vulgar"),
    ("masked-twat-idiot", "masked-euphemistic", "Some tw*t parked across two spaces.", "tw*t", "идиот", "vulgar"),
    ("masked-twat-anatomy", "masked-euphemistic", "He wrote the word tw*t on the wall.", "tw*t", "манда", "vulgar"),
    ("masked-wanker-idiot", "masked-euphemistic", "That w**ker never returns a call.", "w**ker", "мудак", "vulgar"),
    ("masked-wanker-poser", "masked-euphemistic", "He is a w**ker who loves an audience.", "w**ker", "позёр", "vulgar"),
    ("masked-whore-prostitute", "masked-euphemistic", "She played a wh*re in the second act.", "wh*re", "проститутка", "vulgar"),
    ("masked-motherfucker", "masked-euphemistic", "The motherf**ker took my seat.", "motherf**ker", "мудак", "vulgar"),
    ("masked-cunt-alt", "masked-euphemistic", "He shouted c**t across the yard.", "c**t", "пизда", "vulgar"),
    ("masked-fuck-alt-verb", "masked-euphemistic", "They would fu*k until dawn.", "fu*k", "трахаться", "vulgar"),
    ("masked-fuck-alt-intj", "masked-euphemistic", "Oh fu*k, the roof is leaking.", "fu*k", "блядь", "vulgar"),
    ("masked-slut-untidy", "masked-euphemistic", "The old word sl*t meant a slovenly woman.", "sl*t", "неряха", "archaic"),
    ("masked-slut-promiscuous", "masked-euphemistic", "They called her a sl*t behind her back.", "sl*t", "шлюха", "neutral"),
    ("masked-porn", "masked-euphemistic", "The laptop was full of p*rn.", "p*rn", "порно", "neutral"),
    ("masked-sex-act", "masked-euphemistic", "They never talked about s*x.", "s*x", "секс", "neutral"),
    ("masked-sex-category", "masked-euphemistic", "The form asked for s*x and age.", "s*x", "пол", "neutral"),
    ("masked-job-task", "masked-euphemistic", "The j*b took all afternoon.", "j*b", "работа", "neutral"),
    ("masked-job-surgery", "masked-euphemistic", "She had a nose j*b last spring.", "j*b", "пластическая операция", "neutral"),
    ("masked-kink-bend", "masked-euphemistic", "There is a k*nk in the hose.", "k*nk", "перегиб", "neutral"),
    ("masked-kink-taste", "masked-euphemistic", "Everyone has some k*nk or other.", "k*nk", "заскок", "neutral"),

    # ---- more neutral <-> vulgar --------------------------------------------
    ("damn-intj", "neutral-vulgar", "Well, damn, that was fast.", "damn", "чёрт побери", "vulgar"),
    ("damn-intens", "neutral-vulgar", "It is damn cold out there.", "damn", "чертовски", "slang"),
    ("crap-adj", "neutral-vulgar", "The food there is crap.", "crap", "дерьмовый", "vulgar"),
    ("balls-vulgar", "neutral-vulgar", "The kick caught him right in the balls.", "balls", "муде", "vulgar"),

    # ---- more sexual <-> ordinary -------------------------------------------
    ("blow-breath", "sexual-ordinary", "Just blow on the embers.", "blow", "дуть", "neutral"),
    ("dude-vocative", "sexual-ordinary", "Hey dude, wait for me.", "dude", "чувак", "slang"),
    ("box-slang", "sexual-ordinary", "He stared at the box in the corner.", "box", "ящик", "slang"),
    ("crack-vagina", "sexual-ordinary", "The word crack can mean the vagina.", "crack", "щель", "vulgar"),
]


def load_inventory():
    inventory = collections.defaultdict(list)
    with gzip.open(SIDECAR, "rt", encoding="utf-8") as handle:
        for line in handle:
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 5:
                inventory[parts[0]].append(tuple(parts[1:5]))
    return inventory


def register_of(label: str) -> str:
    label = label.lower()
    if "груб" in label or "табу" in label:
        return "vulgar"
    if "секс" in label:
        return "sexual-domain"
    if "жарг" in label:
        return "slang"
    if "разг" in label or "прост" in label:
        return "colloquial"
    if "диал" in label:
        return "dialect"
    if "устар" in label or "архаич" in label:
        return "archaic"
    if "лит" in label:
        return "literary"
    return "neutral"


def normalize(value: str) -> str:
    return " ".join(value.lower().replace("ё", "е").split())


def main() -> None:
    inventory = load_inventory()
    good, bad = [], []
    for case_id, bucket, context, target, expected, expected_register in CASES:
        # Case-insensitive: a target opening a sentence is capitalised, and the
        # index must point at the real occurrence, not at a lowercase guess.
        index = context.lower().find(target.lower())
        rows = inventory.get(target.lower(), [])
        # One surface can belong to several senses with different labels
        # ("щель" is both a narrow opening and a coarse anatomical term), so a
        # surface maps to a set of registers, not to one.
        surfaces = collections.defaultdict(set)
        for row in rows:
            surfaces[normalize(row[0])].add(register_of(row[2]))
        actual = surfaces.get(normalize(expected))
        if index < 0:
            bad.append((case_id, "target not in context"))
        elif not rows:
            bad.append((case_id, f"'{target}' has no sense inventory"))
        elif actual is None:
            bad.append((case_id, f"'{expected}' is not a surface of '{target}'"))
        elif expected_register not in actual:
            bad.append((case_id, f"registers are {sorted(actual)}, case claims '{expected_register}'"))
        else:
            good.append({
                "id": case_id,
                "bucket": bucket,
                "context": context,
                "target": target,
                "characterIndex": index,
                "expectedTranslation": expected,
                "expectedRegister": expected_register,
            })

    print(f"verified {len(good)} / {len(CASES)}")
    for case_id, reason in bad:
        print(f"  REJECT {case_id}: {reason}")
    counts = collections.Counter(case["bucket"] for case in good)
    for bucket, count in sorted(counts.items()):
        print(f"  {bucket}: {count}")
    ROOT / "assets/Ranker/ranker-register-benchmark.cases.json".write_text(
        json.dumps({"version": 1, "cases": good}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
