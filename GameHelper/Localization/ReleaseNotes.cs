// <copyright file="ReleaseNotes.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Localization
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    internal static class ReleaseNotes
    {
        private static readonly Regex LanguageMarker = new(
            @"^[\t ]*<!--[\t ]*release-notes:([a-zA-Z]{2,3}(?:-[a-zA-Z0-9]+)*)[\t ]*-->[\t ]*\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string SelectLanguage(string notes, string languageCode)
        {
            var markers = LanguageMarker.Matches(notes);
            if (markers.Count == 0)
            {
                return notes;
            }

            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < markers.Count; i++)
            {
                var start = markers[i].Index + markers[i].Length;
                var end = i + 1 < markers.Count ? markers[i + 1].Index : notes.Length;
                var section = notes[start..end].Trim();
                if (section.Length > 0)
                {
                    var code = markers[i].Groups[1].Value;
                    sections[code] = sections.TryGetValue(code, out var previous)
                        ? previous + "\n\n" + section : section;
                }
            }

            if (sections.TryGetValue(languageCode, out var localized))
            {
                return localized;
            }

            // Marked releases never fall back to displaying unrelated language sections.
            return sections.TryGetValue("en-US", out var english) ? english : string.Empty;
        }
    }
}
