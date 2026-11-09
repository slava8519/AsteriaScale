using System.Text;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Services;
using Drawing = System.Drawing;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// One hover.resolve request as Core's coordinator sees it (its opaque
/// anchor): the selection the app sent, the game it names, where the cards go
/// and the request's cancellation. The hover stays current until its request
/// is cancelled or superseded; there is no cursor to move away.
/// </summary>
internal sealed class CoreHostHover
{
    /// <summary>
    /// The anchor of a speaker choice made outside any hover (speaker.set):
    /// Core carries the anchor of a speaker target but never reads it.
    /// </summary>
    public static readonly CoreHostHover Detached = new(
        0,
        new SelectionSource(),
        game: null,
        events: null,
        CancellationToken.None);

    public CoreHostHover(
        long id,
        SelectionSource selection,
        GameIdentity? game,
        IHoverEvents? events,
        CancellationToken cancellationToken)
    {
        Id = id;
        Selection = selection;
        Game = game;
        Events = events;
        CancellationToken = cancellationToken;
    }

    public long Id { get; }

    public SelectionSource Selection { get; }

    public GameIdentity? Game { get; }

    public IHoverEvents? Events { get; }

    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Turns a selection (text the app read itself: the «Проверка перевода»
/// window, the clipboard) into the recognized text a hover starts from, with
/// the rules every text source shares (<see cref="OcrTextRules"/>): the same
/// whitespace collapse and mixed-script repair, and in sentence mode the same
/// sentence extraction, speaker split and completeness gate as the OCR path.
/// The app sends the character index in UTF-16 units of its own text; the
/// collapse moves it with the characters it keeps.
/// </summary>
internal static class SelectionText
{
    public static HoverOcrResult? Recognize(SelectionSource selection, bool sentenceContext)
    {
        (string collapsed, int index) = Collapse(selection.Text, selection.CharacterIndex);
        // Normalize keeps the length of collapsed text (the repair maps one
        // character to one, and the contraction fix rewrites two characters
        // as two), so the index still points at the same character.
        string text = OcrTextRules.Normalize(collapsed);
        if (text.Length != collapsed.Length)
        {
            // A rule that changed lengths would move the index silently; the
            // hover fails visibly instead (Core logs it and reports it).
            throw new InvalidOperationException("OcrTextRules.Normalize changed the length of collapsed text.");
        }

        string hoveredWord = OcrTextRules.Normalize(selection.HoveredWord);
        if (!OcrTextRules.LooksTranslatable(text) || hoveredWord.Length == 0)
        {
            return null;
        }

        if (!sentenceContext)
        {
            return new HoverOcrResult(text, hoveredWord, Math.Clamp(index, 0, text.Length - 1), Drawing.Rectangle.Empty);
        }

        SentenceContext sentence = OcrTextRules.ExtractSentence(text, index);
        if (sentence.Text.Length == 0)
        {
            return null;
        }

        CurrentSpeakerContext? speaker = string.IsNullOrWhiteSpace(selection.SpeakerName)
            ? sentence.CurrentSpeaker
            : new CurrentSpeakerContext(selection.SpeakerName.Trim());
        return new HoverOcrResult(sentence.Text, hoveredWord, sentence.CharacterIndex, Drawing.Rectangle.Empty)
        {
            // The app's own text is one exact observation; the completeness
            // gate still decides whether the phrase model may see it.
            IsSentenceContextStable = selection.SentenceStable &&
                                      OcrTextRules.LooksLikeCompleteOcrSentence(sentence.Text),
            CurrentSpeaker = speaker,
        };
    }

    /// <summary>
    /// The text exactly as Normalize collapses it (runs of white space become
    /// one space, none at either end) and where the character at
    /// <paramref name="characterIndex"/> landed. An index on white space moves
    /// to the next kept character; one past the end stays past the end.
    /// </summary>
    internal static (string Text, int Index) Collapse(string text, int characterIndex)
    {
        StringBuilder builder = new(text.Length);
        int mapped = -1;
        bool pendingSpace = false;
        bool waiting = false;
        for (int position = 0; position < text.Length; position++)
        {
            if (position == characterIndex)
            {
                waiting = true;
            }

            char character = text[position];
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (waiting && mapped < 0)
            {
                mapped = builder.Length;
            }

            builder.Append(character);
        }

        return (builder.ToString(), mapped < 0 ? builder.Length : mapped);
    }
}
