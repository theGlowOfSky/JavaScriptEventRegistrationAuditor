using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Relationship report. "Detected" rows come directly from form registrations or library scans; "Inferred" rows come from
    /// call-like text matches (name followed by "(") and may be wrong. A function that is "Not registered in scanned forms" is
    /// NOT necessarily unused: ribbon commands, HTML web resources and other scripts are outside the scan.
    /// </summary>
    public static class DependencyAnalyzer
    {
        private static readonly Regex CallRx = new Regex(@"(?<![\w$])(?<n>[A-Za-z_$][\w$]*(?:\s*\.\s*[A-Za-z_$][\w$]*)*)\s*\(", RegexOptions.Compiled);

        public static List<DependencyRow> Run(EnvironmentData d, Dictionary<string, List<FunctionDef>> defsByLib)
        {
            var rows = new List<DependencyRow>();

            // 1. Registered functions -> library (detected)
            foreach (var g in d.Registrations.Where(r => r.Definition != null)
                         .GroupBy(r => new { r.Definition.LibraryName, Name = r.Definition.FullName }))
            {
                int forms = g.Select(x => x.FormId).Distinct().Count();
                rows.Add(new DependencyRow
                {
                    Function = g.Key.Name, Library = g.Key.LibraryName, Relationship = "Referenced by form registrations",
                    Detail = g.Count() + " registration(s) on " + forms + " form(s)", Basis = "Detected"
                });
            }

            // 2. Duplicate function names across libraries (detected)
            foreach (var g in d.Definitions.GroupBy(x => x.FullName, StringComparer.Ordinal)
                         .Where(g => g.Select(x => x.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                rows.Add(new DependencyRow
                {
                    Function = g.Key, Library = string.Join(", ", g.Select(x => x.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Relationship = "Duplicate function name across libraries", Detail = "Defined in " + g.Count() + " places.", Basis = "Detected"
                });
            }

            // 3. Functions never matched by a scanned registration
            var matched = new HashSet<FunctionDef>(d.Registrations.Where(r => r.Definition != null).Select(r => r.Definition));
            foreach (var def in d.Definitions.Where(x => !matched.Contains(x)))
                rows.Add(new DependencyRow
                {
                    Function = def.FullName, Library = def.LibraryName, Relationship = "Not registered in scanned forms",
                    Detail = "Line " + def.StartLine + ". May still be called by ribbon commands, HTML web resources or other scripts.", Basis = "Detected"
                });

            // 4. Possible cross-library calls (inferred)
            var byName = d.Definitions.GroupBy(x => x.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var def in d.Definitions.Where(x => x.Complete))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in CallRx.Matches(JsMasker.Mask(def.Code)))
                {
                    string called = Regex.Replace(m.Groups["n"].Value, @"\s+", string.Empty);
                    if (called == def.FullName || !seen.Add(called)) continue;
                    List<FunctionDef> targets;
                    if (!byName.TryGetValue(called, out targets)) continue;
                    var other = targets.Where(t => !string.Equals(t.LibraryName, def.LibraryName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (other.Count == 0) continue;
                    rows.Add(new DependencyRow
                    {
                        Function = def.FullName, Library = def.LibraryName, Relationship = "Possible call to function in another library",
                        Detail = called + " (" + string.Join(", ", other.Select(o => o.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase)) + ")", Basis = "Inferred"
                    });
                }
            }
            return rows;
        }
    }
}
