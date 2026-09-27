// [ADDED v2026.9.26 | 2026-09-26] Purpose: The people in this house and the
// phone each of them carries, so a reminder can be aimed at one of them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace HO_Mind_AI
{
    /// <summary>One person and the phone a reminder for them should reach.</summary>
    internal sealed class HoUser
    {
        /// <summary>What they are called out loud, e.g. the name spoken in a command.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The Home Assistant DEVICE id of their phone - the 32-character value
        /// from Settings > Devices, whose id appears in the page address.
        ///
        /// It has to be that and not a notify slug like "mobile_app_pixel". The
        /// reminder agent looks the id up in the device registry and derives the
        /// notify service from it; given anything else the lookup simply returns
        /// nothing and the reminder quietly goes to whoever asked for it instead.
        /// </summary>
        public string DeviceId { get; set; } = "";
    }

    /// <summary>
    /// Who a reminder is for.
    ///
    /// HOW THE NAME IS MATCHED, AND WHY THERE IS NO HEBREW IN HERE.
    /// The names are typed by the user in Settings, so this file compares against
    /// data and never against words of its own. That is also why a plain
    /// "contains" is the right test rather than a word boundary: Hebrew glues its
    /// prepositions on, so "ליוליה" contains "יוליה" and matches without the code
    /// knowing anything about Hebrew.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO.
    /// It does not read intent. "remind me to call Yulia" contains her name and
    /// will be aimed at her phone, which is wrong and cannot be told apart from
    /// "remind Yulia" without understanding the sentence - and understanding
    /// sentences is Home Assistant's job, not this app's. So the app never keeps
    /// this quiet: the chat shows who a message was aimed at whenever it was not
    /// the main phone.
    /// </summary>
    internal static class UserRoster
    {
        // The main phone keeps the key it has always had, so an install that is
        // already working keeps working and nothing has to be migrated.
        private const string PrimaryIdKey = "ha_notify_id";
        private const string PrimaryNameKey = "ho_primary_user_name";
        private const string OthersKey = "ho_extra_users";

        public static string PrimaryDeviceId() => (Preferences.Get(PrimaryIdKey, "") ?? "").Trim();

        public static void SetPrimaryDeviceId(string? id) =>
            Preferences.Set(PrimaryIdKey, (id ?? "").Trim());

        public static string PrimaryName() => (Preferences.Get(PrimaryNameKey, "") ?? "").Trim();

        public static void SetPrimaryName(string? name) =>
            Preferences.Set(PrimaryNameKey, (name ?? "").Trim());

        /// <summary>Everybody except the main phone, in the order they were added.</summary>
        public static List<HoUser> Others()
        {
            try
            {
                string raw = Preferences.Get(OthersKey, "") ?? "";
                if (string.IsNullOrWhiteSpace(raw)) return new List<HoUser>();
                var list = JsonSerializer.Deserialize<List<HoUser>>(raw);
                return list ?? new List<HoUser>();
            }
            catch (Exception)
            {
                // Unreadable stored text must not cost the user their settings
                // screen. An empty roster is recoverable; a crash is not.
                return new List<HoUser>();
            }
        }

        public static void SaveOthers(IEnumerable<HoUser> users)
        {
            var clean = users
                .Select(u => new HoUser { Name = (u.Name ?? "").Trim(), DeviceId = (u.DeviceId ?? "").Trim() })
                .Where(u => u.Name.Length > 0 || u.DeviceId.Length > 0)
                .ToList();
            try
            {
                Preferences.Set(OthersKey, JsonSerializer.Serialize(clean));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The person a command is aimed at, or null for the main phone.
        ///
        /// A name of one character is ignored: a single letter appears inside
        /// ordinary words and would send half the reminders to the wrong phone.
        /// </summary>
        public static HoUser? Addressee(string? commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText)) return null;
            string haystack = Normalise(commandText);
            foreach (var user in Others())
            {
                if (string.IsNullOrEmpty((user.DeviceId ?? "").Trim())) continue;
                foreach (string alias in AliasesOf(user))
                {
                    if (haystack.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return user;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Every form of a name worth matching. The field takes them separated by
        /// commas, because the same person is not said the same way in every
        /// language: "Yulia" spoken in English will never match a name stored in
        /// Hebrew, and a household that switches language mid-sentence needs both.
        ///
        /// One character is skipped: a single letter appears inside ordinary words
        /// and would send half the reminders to the wrong phone.
        /// </summary>
        public static IEnumerable<string> AliasesOf(HoUser user)
        {
            foreach (string part in (user.Name ?? "").Split(','))
            {
                string alias = Normalise(part);
                if (alias.Length >= 2) yield return alias;
            }
        }

        /// <summary>
        /// Composed Unicode, trimmed. Without this an accented letter written as
        /// one character does not equal the same letter written as a letter plus a
        /// combining mark - they look identical and compare as different, which is
        /// exactly what happens when one side is typed and the other is dictated.
        /// </summary>
        private static string Normalise(string? value)
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0) return v;
            try { return v.Normalize(NormalizationForm.FormC); }
            catch (Exception) { return v; }
        }

        /// <summary>Which phone this command should reach.</summary>
        public static string DeviceIdFor(string? commandText) =>
            Addressee(commandText)?.DeviceId ?? PrimaryDeviceId();

        /// <summary>
        /// Whether a value looks like a Home Assistant device id: 32 hexadecimal
        /// characters. Used to WARN, never to block - the registry is the only
        /// real authority on what is valid, and a future format should not lock
        /// the user out of their own settings.
        /// </summary>
        public static bool LooksLikeDeviceId(string? id)
        {
            string v = (id ?? "").Trim();
            if (v.Length != 32) return false;
            foreach (char c in v)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>Every entry whose id does not look like a device id, for a warning.</summary>
        public static List<string> SuspectEntries()
        {
            var bad = new List<string>();
            string primary = PrimaryDeviceId();
            if (primary.Length > 0 && !LooksLikeDeviceId(primary))
            {
                string who = PrimaryName();
                bad.Add(string.IsNullOrEmpty(who) ? "the main phone" : who);
            }
            foreach (var u in Others())
            {
                if ((u.DeviceId ?? "").Trim().Length > 0 && !LooksLikeDeviceId(u.DeviceId))
                {
                    bad.Add(string.IsNullOrWhiteSpace(u.Name) ? "(unnamed)" : u.Name.Trim());
                }
            }
            return bad;
        }
    }
}
