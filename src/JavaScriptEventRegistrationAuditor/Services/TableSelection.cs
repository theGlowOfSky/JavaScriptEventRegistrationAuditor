using System;
using System.Collections.Generic;
using System.Linq;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>UI-independent table list logic (filtering, stale-selection detection, chunking) so it can be unit tested.</summary>
    public static class TableSelection
    {
        /// <summary>
        /// Rows whose logical or display name contains the text (case-insensitive). Row objects are returned as-is,
        /// so their Selected flag is preserved while the filter changes.
        /// </summary>
        public static List<TableRow> Filter(IEnumerable<TableRow> rows, string text)
        {
            string f = (text ?? string.Empty).Trim();
            return rows.Where(r => f.Length == 0
                || (r.LogicalName ?? string.Empty).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.DisplayName ?? string.Empty).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        /// <summary>Selected tables that are not part of the last successful scan (i.e. whose registrations are not loaded).</summary>
        public static List<string> Unscanned(IEnumerable<string> selected, ISet<string> scanned)
        {
            return selected.Where(t => scanned == null || !scanned.Contains(t)).ToList();
        }

        /// <summary>
        /// Metadata rule for "can have model-driven form event registrations that a maker could normally manage".
        /// Excluded: tables without a name, many-to-many intersect tables, private (internal) tables, logical entities and
        /// tables for which forms cannot be created. Unknown (null) values are treated as "not excluded" so that a missing
        /// property never hides a legitimate table. Virtual tables are NOT excluded: they have forms in systemform.
        /// </summary>
        public static bool IsEligible(string logicalName, bool? isIntersect, bool? isPrivate, bool? isLogicalEntity, bool? canCreateForms, bool? isCustomizable)
        {
            if (string.IsNullOrEmpty(logicalName)) return false;
            if (isIntersect == true || isPrivate == true || isLogicalEntity == true) return false;
            if (canCreateForms == false) return false;
            if (canCreateForms == null && isCustomizable == false) return false;
            return true;
        }

        /// <summary>
        /// Message shown under each table in the list: an instruction until the table's registrations are loaded,
        /// then a short result summary.
        /// </summary>
        public static string RowMessage(int? registrations, int? libraryMismatches, bool loadFailed)
        {
            if (loadFailed) return "Could not load registrations.";
            if (registrations == null) return "Click View to see registrations.";

            string s = registrations + (registrations == 1 ? " registration" : " registrations");
            if (libraryMismatches > 0) s += ", " + libraryMismatches + (libraryMismatches == 1 ? " mismatch" : " mismatches");
            return s;
        }

        public static List<List<string>> Chunk(IEnumerable<string> tables, int size)
        {
            if (size < 1) throw new ArgumentOutOfRangeException("size");
            var result = new List<List<string>>();
            List<string> current = null;
            foreach (var t in tables)
            {
                if (current == null || current.Count == size) { current = new List<string>(); result.Add(current); }
                current.Add(t);
            }
            return result;
        }
    }
}
