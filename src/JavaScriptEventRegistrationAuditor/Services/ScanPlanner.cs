using System;
using System.Collections.Generic;
using System.Linq;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// SDK-free decisions behind the registration scan: what still has to be retrieved from Dataverse.
    /// Kept separate so they can be unit tested without a Dataverse connection.
    /// </summary>
    public static class ScanPlanner
    {
        /// <summary>Selected tables (distinct, original order) whose forms are not cached yet.</summary>
        public static List<string> MissingTables(IEnumerable<string> selected, Func<string, bool> isCached)
        {
            return selected.Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !isCached(t)).ToList();
        }

        /// <summary>Library names referenced by the registrations (distinct, case-insensitive) that are not known yet.</summary>
        public static List<string> LibraryNamesToFetch(IEnumerable<HandlerRegistration> registrations, Func<string, bool> isKnown, IEnumerable<string> additionalNames = null)
        {
            return registrations
                .Where(r => !r.IsPcf)
                .Select(r => r.LibraryName)
                .Concat(additionalNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !isKnown(n))
                .ToList();
        }
    }
}
