using System.Text;
using System.Text.RegularExpressions;
using ToolGood.Words;

namespace Vesper.Core;

/// <summary>Offline search-only keys. Display values and library identities are never rewritten.</summary>
public static class LocalSearchText
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string Separators = " -_./\\·•・,，、:：;；()（）[]【】{}'’\"“”《》<>!?！？";
    // Explicit phrase readings rather than combinatorial readings of each polyphonic character.
    private static readonly (string Phrase, string[] Syllables)[] PhraseReadings =
    [
        ("重庆", ["CHONG", "QING"]), ("重慶", ["CHONG", "QING"]),
        ("长安", ["CHANG", "AN"]), ("長安", ["CHANG", "AN"]),
        ("音乐", ["YIN", "YUE"]), ("音樂", ["YIN", "YUE"])
    ];

    public static string Normalize(string? value) => Spaces.Replace((value ?? "").Normalize(NormalizationForm.FormKC).Trim(), " ").ToUpperInvariant();
    public static string Fold(string value) => WordsHelper.ToSimplifiedChinese(value);
    public static string Compact(string value) => string.Concat(value.Where(character => !Separators.Contains(character) && !char.IsWhiteSpace(character)));
    public static (string Full, string Initials) Pinyin(string value)
    {
        var full = new StringBuilder();
        var initials = new StringBuilder();
        int index = 0;
        void Add(string syllable)
        {
            string normalized = Compact(Normalize(syllable));
            full.Append(normalized);
            if (normalized.Length > 0) initials.Append(normalized[0]);
        }
        while (index < value.Length)
        {
            var phrase = PhraseReadings.FirstOrDefault(item => value.AsSpan(index).StartsWith(item.Phrase, StringComparison.Ordinal));
            if (phrase.Phrase is not null)
            {
                foreach (string syllable in phrase.Syllables) Add(syllable);
                index += phrase.Phrase.Length;
                continue;
            }
            if (!IsHan(value[index]))
            {
                if (char.IsLetterOrDigit(value[index])) { full.Append(value[index]); initials.Append(value[index]); }
                index++;
                continue;
            }
            int end = index + 1;
            while (end < value.Length && IsHan(value[end]) && !PhraseReadings.Any(item => value.AsSpan(end).StartsWith(item.Phrase, StringComparison.Ordinal))) end++;
            foreach (string syllable in WordsHelper.GetPinyinList(value[index..end])) Add(syllable);
            index = end;
        }
        return (full.ToString(), initials.ToString());
    }
    internal static bool IsHan(char character) => character is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';
    internal static bool IsLatinQuery(string value) => value.Length >= 2 && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9');
}

internal sealed record SearchKey(string Literal, string Folded, string Compact, string Pinyin, string Initials)
{
    public static SearchKey Create(string value, bool withPinyin = false)
    {
        string literal = LocalSearchText.Normalize(value);
        string folded = LocalSearchText.Fold(literal);
        var pinyin = withPinyin && folded.Any(LocalSearchText.IsHan) ? LocalSearchText.Pinyin(folded) : ("", "");
        return new(literal, folded, LocalSearchText.Compact(folded), pinyin.Item1, pinyin.Item2);
    }
}
