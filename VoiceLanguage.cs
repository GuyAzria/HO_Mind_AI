// [ADDED v2026.9.24 | 2026-09-24] Purpose: One owner for the voice language.
// The display-name to culture-code mapping existed character for character in
// two files, so adding a sixth language meant editing both, and missing one
// gave speech recognition in one language with speech output in another.

using Microsoft.Maui.Storage;

namespace HO_Mind_AI
{
    /// <summary>
    /// The language picked in Settings, as a culture code.
    ///
    /// It drives three separate things, and it is NOT the language of the
    /// app own interface, which is English throughout:
    ///   - speech recognition (the SpeechToText culture)
    ///   - speech output (the TextToSpeech locale)
    ///   - the "language" field sent to /api/conversation/process
    ///
    /// The display strings must match Picker.Items in MainPage.xaml exactly.
    /// Anything unrecognised falls back to en-US rather than throwing, so a
    /// renamed entry degrades to English instead of breaking the microphone.
    /// </summary>
    internal static class VoiceLanguage
    {
        private const string Key = "voice_lang";

        public const string DefaultDisplayName = "English (US)";

        /// <summary>The stored display name, exactly as the picker shows it.</summary>
        public static string DisplayName() => Preferences.Get(Key, DefaultDisplayName) ?? DefaultDisplayName;

        /// <summary>Stores the display name the picker selected.</summary>
        public static void SetDisplayName(string? displayName) =>
            Preferences.Set(Key, string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName);

        /// <summary>The full culture code, for example "he-IL".</summary>
        public static string CultureCode() => DisplayName() switch
        {
            "Hebrew (Israel)" => "he-IL",
            "French (France)" => "fr-FR",
            "Russian (Russia)" => "ru-RU",
            "Arabic (SA)" => "ar-SA",
            _ => "en-US",
        };

        /// <summary>Just the two-letter part, for example "he".</summary>
        public static string LanguageCode() => CultureCode().Split('-')[0];
    }
}
