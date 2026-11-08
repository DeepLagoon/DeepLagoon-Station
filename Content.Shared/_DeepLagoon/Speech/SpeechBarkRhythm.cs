using System.Globalization;
using System.Text;

namespace Content.Shared._DeepLagoon.Speech;

public static class SpeechBarkRhythm
{
    public const int MaxNotes = 12;
    public const int LastNoteMs = 1850;
    private const int NoteIntervalMs = 137;
    private const int WordPauseMs = 26;

    public static ushort[] Build(string message, SpeechBarkSettings? settings = null)
    {
        var voice = settings?.Sanitize() ?? new SpeechBarkSettings();
        var interval = NoteIntervalMs * voice.Speed / SpeechBarkSettings.DefaultSpeed;
        var notes = new List<ushort>(MaxNotes);
        var nextNote = 0;
        var pause = 0;
        var letters = 0;
        var syllables = 0;
        var lastLatinVowel = false;

        foreach (var rune in message.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                letters++;
                var letter = Rune.ToLowerInvariant(rune).Value;
                var latinVowel = letter is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
                if (latinVowel && !lastLatinVowel ||
                    letter is 'а' or 'е' or 'ё' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я')
                {
                    syllables++;
                }

                lastLatinVowel = latinVowel;
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ||
                rune.Value is '\'' or '’')
                continue;

            AddWord();
            if (notes.Count == MaxNotes || nextNote > LastNoteMs)
                break;

            var gap = rune.Value switch
            {
                ',' or ';' or ':' => 120,
                '.' or '!' or '?' or '…' => 220,
                _ => WordPauseMs,
            };
            if (notes.Count > 0)
                pause = Math.Max(pause, gap);
        }

        AddWord();
        return notes.ToArray();

        void AddWord()
        {
            if (letters == 0)
                return;

            nextNote += pause * voice.Pause / 100;
            var count = syllables > 0 ? syllables : (letters + 3) / 4;
            for (var i = 0; i < count && notes.Count < MaxNotes && nextNote <= LastNoteMs; i++)
            {
                notes.Add((ushort) nextNote);
                nextNote += interval;
            }

            letters = 0;
            syllables = 0;
            lastLatinVowel = false;
            pause = WordPauseMs;
        }
    }
}
