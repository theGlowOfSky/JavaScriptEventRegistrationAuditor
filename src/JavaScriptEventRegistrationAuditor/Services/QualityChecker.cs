using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Transparent regex-based static checks over retrieved libraries (comments and string literals are masked first).
    /// Results are pattern matches for review, not confirmed defects.
    /// </summary>
    public static class QualityChecker
    {
        private static readonly Regex DebuggerRx = new Regex(@"(?<![\w$.])debugger\b", RegexOptions.Compiled);
        private static readonly Regex LooseEqRx = new Regex(@"(?<![=!<>])(==|!=)(?!=)", RegexOptions.Compiled);
        private static readonly Regex XrmPageRx = new Regex(@"(?<![\w$])Xrm\s*\.\s*Page\b", RegexOptions.Compiled);
        private static readonly Regex GlobalCtxRx = new Regex(@"(?<![\w$.])GetGlobalContext\s*\(", RegexOptions.Compiled);
        private static readonly Regex ServerUrlRx = new Regex(@"\.\s*getServerUrl\s*\(", RegexOptions.Compiled);

        public static List<QualityFinding> Run(EnvironmentData d, QualityOptions o)
        {
            var findings = new List<QualityFinding>();
            if (o == null) o = new QualityOptions();

            var defsByLib = d.Definitions.GroupBy(x => x.LibraryName, StringComparer.OrdinalIgnoreCase)
                                         .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var lib in d.Libraries.Where(l => l.Retrieved))
            {
                string masked = JsMasker.Mask(lib.Content);
                var lineStarts = LineStarts(masked);
                List<FunctionDef> defs;
                if (!defsByLib.TryGetValue(lib.Name, out defs)) defs = new List<FunctionDef>();

                if (o.DebuggerStatements)
                    Scan(findings, lib.Name, masked, lineStarts, defs, DebuggerRx, "Leftover debugger statement",
                        "A 'debugger' statement pauses execution when browser developer tools are open.",
                        "Remove it if it is not intentional.", "Pattern match");

                if (o.LooseEquality)
                    Scan(findings, lib.Name, masked, lineStarts, defs, LooseEqRx, "Loose equality (== / !=)",
                        "Loose comparison performs type coercion; this is sometimes intentional (e.g. null checks).",
                        "Review and use === / !== where coercion is not required.", "Needs Review");

                if (o.DeprecatedApis)
                {
                    Scan(findings, lib.Name, masked, lineStarts, defs, XrmPageRx, "Deprecated Xrm.Page usage",
                        "Microsoft documents Xrm.Page as deprecated for model-driven apps.",
                        "Pass the execution context and use executionContext.getFormContext() instead.", "Pattern match");
                    Scan(findings, lib.Name, masked, lineStarts, defs, GlobalCtxRx, "Deprecated GetGlobalContext usage",
                        "GetGlobalContext is part of the legacy client API surface.",
                        "Use Xrm.Utility.getGlobalContext() where applicable.", "Needs Review");
                    Scan(findings, lib.Name, masked, lineStarts, defs, ServerUrlRx, "Removed getServerUrl usage",
                        "context.getServerUrl() is a legacy method.",
                        "Use getClientUrl() from the global context.", "Needs Review");
                }
            }

            foreach (var def in d.Definitions.Where(x => !x.Complete))
                findings.Add(new QualityFinding
                {
                    Check = "Function extraction incomplete", Library = def.LibraryName, Function = def.FullName, Line = def.StartLine, Occurrences = 1,
                    Explanation = "The regex scanner located the start of this function but not its closing brace.",
                    Action = "Review the library manually; the scanner may not support a construct used here.", Confidence = "Needs Review"
                });

            return findings.OrderBy(f => f.Library, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Line ?? 0).ToList();
        }

        /// <summary>One finding per (check, enclosing function) with an occurrence count, to avoid flooding the results.</summary>
        private static void Scan(List<QualityFinding> sink, string library, string masked, int[] lineStarts, List<FunctionDef> defs,
            Regex rx, string check, string explanation, string action, string confidence)
        {
            var groups = new Dictionary<string, QualityFinding>();
            foreach (Match m in rx.Matches(masked))
            {
                int line = LineOf(lineStarts, m.Index);
                var enclosing = defs.Where(x => x.StartLine <= line && x.EndLine >= line).OrderByDescending(x => x.StartLine).FirstOrDefault();
                string fn = enclosing != null ? enclosing.FullName : null;
                string key = fn ?? "(outside detected functions)";
                QualityFinding f;
                if (!groups.TryGetValue(key, out f))
                {
                    f = new QualityFinding { Check = check, Library = library, Function = fn, Line = line, Occurrences = 0, Explanation = explanation, Action = action, Confidence = confidence };
                    groups[key] = f;
                }
                f.Occurrences++;
            }
            sink.AddRange(groups.Values);
        }

        private static int[] LineStarts(string s)
        {
            var l = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') l.Add(i + 1);
            return l.ToArray();
        }

        private static int LineOf(int[] starts, int index)
        {
            int p = Array.BinarySearch(starts, index);
            if (p < 0) p = ~p - 1;
            return p + 1;
        }
    }
}
