namespace FlexyParse.Recognition;

/// <summary>Normalizes the public CLI/API OCR language contract without selecting engine models.</summary>
public static class OcrLanguageProfiles
{
    public static IReadOnlyList<string> ModelNames { get; } = Array.AsReadOnly(new[]
    {
        "arabic", "ch", "ch_server", "cyrillic", "devanagari", "el", "en", "eslav", "korean", "latin", "ta", "te", "th"
    });

    /// <summary>Language coverage of the pinned PP-OCRv5 recognition models.</summary>
    public const string ModelLanguagesDescription =
        "arabic: Arabic, Persian, Uyghur, Urdu, Pashto, Kurdish, Sindhi, Balochi, English; "
        + "ch / ch_server: Simplified Chinese, Traditional Chinese, Chinese Pinyin, English, Japanese; "
        + "cyrillic: Russian, Belarusian, Ukrainian, Serbian (Cyrillic), Bulgarian, Mongolian, Abkhazian, Adyghe, "
        + "Kabardian, Avar, Dargwa, Ingush, Chechen, Lak, Lezgian, Tabasaran, Kazakh, Kyrgyz, Tajik, Macedonian, "
        + "Tatar, Chuvash, Bashkir, Mari, Moldovan, Udmurt, Komi, Ossetian, Buryat, Kalmyk, Tuvan, Sakha, Karakalpak, English; "
        + "devanagari: Hindi, Marathi, Nepali, Bihari, Maithili, Angika, Bhojpuri, Magahi, Santali, Newari, Konkani, Sanskrit, Haryanvi, English; "
        + "el: Greek, English; en: English; eslav: Russian, Belarusian, Ukrainian, English; korean: Korean, English; "
        + "latin: French, German, Afrikaans, Italian, Spanish, Bosnian, Portuguese, Czech, Welsh, Danish, Estonian, Irish, "
        + "Croatian, Uzbek, Hungarian, Serbian (Latin), Indonesian, Occitan, Icelandic, Lithuanian, Maori, Malay, Dutch, "
        + "Norwegian, Polish, Slovak, Slovenian, Albanian, Swedish, Swahili, Tagalog, Turkish, Latin, Azerbaijani, Kurdish, "
        + "Latvian, Maltese, Pali, Romanian, Vietnamese, Finnish, Basque, Galician, Luxembourgish, Romansh, Catalan, Quechua; "
        + "ta: Tamil, English; te: Telugu, English; th: Thai, English.";

    /// <summary>Normalizes supported model prefixes; language aliases and regional tags are not accepted.</summary>
    /// <exception cref="ArgumentException">The language is empty, invalid, or unsupported.</exception>
    public static string Normalize(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var value = language.Trim().ToLowerInvariant();
        if (ModelNames.Contains(value, StringComparer.Ordinal))
        {
            return value;
        }

        throw new ArgumentException($"Unsupported local OCR language. Supported model names: {string.Join(", ", ModelNames)}.", nameof(language));
    }
}
