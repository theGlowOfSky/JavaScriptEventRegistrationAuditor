using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Regex-based function locator. It deliberately does NOT build an AST. A function that is not recognised here
    /// is "not detected", never proof that the function does not exist or that a handler fails at runtime.
    /// Recognised forms: function declarations, "name = function", "ns.name = function", "name: function",
    /// arrow functions assigned to a name, and object/class method shorthand "name(args) {".
    /// </summary>
    public static class FunctionScanner
    {
        private const string Ident = @"[A-Za-z_$][\w$]*";
        private static readonly string QName = @"(?:" + Ident + @"\s*\.\s*)*" + Ident;

        private static readonly Regex DeclRx = new Regex(
            @"(?:async\s+)?function\s*\*?\s*(?<name>" + Ident + @")\s*\((?<args>[^)]*)\)", RegexOptions.Compiled);

        private static readonly Regex AssignRx = new Regex(
            @"(?<![\w$.])(?<name>" + QName + @")\s*(?:=(?!=)|:)\s*(?:async\s+)?function\s*\*?\s*(?:" + Ident + @")?\s*\((?<args>[^)]*)\)",
            RegexOptions.Compiled);

        private static readonly Regex ArrowRx = new Regex(
            @"(?<![\w$.])(?<name>" + QName + @")\s*(?:=(?!=)|:)\s*(?:async\s+)?(?:\((?<args>[^)]*)\)|(?<args>" + Ident + @"))\s*=>",
            RegexOptions.Compiled);

        private static readonly Regex MethodRx = new Regex(
            // No look-behind here: a variable-length look-behind made this pattern super-linear (minutes on large libraries).
            // "function name(...) {" is excluded in code instead (see FollowsFunctionKeyword).
            @"(?<![\w$.])(?<name>" + Ident + @")\s*\((?<args>[^)]*)\)\s*\{",
            RegexOptions.Compiled);

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","for","while","switch","catch","function","with","return","else","do","try","typeof","new","await","yield"
        };

        private class Candidate { public int Index; public int MatchEnd; public string Name; public string Args; public string Kind; public bool Arrow; }

        public static List<FunctionDef> Index(string libraryName, string content)
        {
            var result = new List<FunctionDef>();
            if (string.IsNullOrEmpty(content)) return result;

            string masked = JsMasker.Mask(content);
            var lineStarts = BuildLineStarts(content);
            var cands = new List<Candidate>();

            foreach (Match m in DeclRx.Matches(masked))
            {
                if (FollowsAssignment(masked, m.Index)) continue; // "x = function name(" is handled by AssignRx
                cands.Add(new Candidate { Index = m.Index, MatchEnd = m.Index + m.Length, Name = m.Groups["name"].Value, Args = m.Groups["args"].Value, Kind = "function declaration" });
            }
            foreach (Match m in AssignRx.Matches(masked))
                cands.Add(new Candidate { Index = m.Index, MatchEnd = m.Index + m.Length, Name = Compact(m.Groups["name"].Value), Args = m.Groups["args"].Value, Kind = "function expression" });
            foreach (Match m in ArrowRx.Matches(masked))
                cands.Add(new Candidate { Index = m.Index, MatchEnd = m.Index + m.Length, Name = Compact(m.Groups["name"].Value), Args = m.Groups["args"].Value, Kind = "arrow function", Arrow = true });
            foreach (Match m in MethodRx.Matches(masked))
            {
                string nm = m.Groups["name"].Value;
                if (Keywords.Contains(nm)) continue;
                if (FollowsFunctionKeyword(masked, m.Groups["name"].Index)) continue; // already found by DeclRx/AssignRx
                cands.Add(new Candidate { Index = m.Groups["name"].Index, MatchEnd = m.Index + m.Length - 1, Name = nm, Args = m.Groups["args"].Value, Kind = "method shorthand" });
            }

            foreach (var c in cands.GroupBy(x => x.Index).Select(g => g.First()).OrderBy(x => x.Index))
            {
                int end;
                bool ok = TryFindEnd(masked, c.MatchEnd, c.Arrow, out end);
                string code;
                if (ok) code = content.Substring(c.Index, end - c.Index + 1);
                else
                {
                    end = Math.Min(content.Length - 1, c.Index + 1999);
                    code = "// [Extraction incomplete - partial source shown]" + Environment.NewLine + content.Substring(c.Index, end - c.Index + 1);
                }

                string full = c.Name;
                result.Add(new FunctionDef
                {
                    LibraryName = libraryName,
                    FullName = full,
                    ShortName = full.Contains(".") ? full.Substring(full.LastIndexOf('.') + 1) : full,
                    StartLine = LineOf(lineStarts, c.Index),
                    EndLine = LineOf(lineStarts, Math.Max(c.Index, end)),
                    Code = code,
                    Complete = ok,
                    Kind = c.Kind,
                    Parameters = ParseParams(c.Args)
                });
            }
            return result;
        }

        /// <summary>
        /// Finds definitions for a registered function name. Tiers: exact name, namespace-suffix match, then short-name match.
        /// </summary>
        public static List<FunctionDef> Find(IEnumerable<FunctionDef> defs, string registeredName)
        {
            if (string.IsNullOrWhiteSpace(registeredName)) return new List<FunctionDef>();
            string reg = registeredName.Trim();
            var list = defs as IList<FunctionDef> ?? defs.ToList();

            var exact = list.Where(d => string.Equals(d.FullName, reg, StringComparison.Ordinal)).ToList();
            if (exact.Count > 0) return exact;

            var suffix = list.Where(d => d.FullName != null && reg.EndsWith("." + d.FullName, StringComparison.Ordinal)).ToList();
            if (suffix.Count > 0) return suffix;

            string shortName = reg.Contains(".") ? reg.Substring(reg.LastIndexOf('.') + 1) : reg;
            return list.Where(d => string.Equals(d.ShortName, shortName, StringComparison.Ordinal)).ToList();
        }

        // ---------- helpers ----------
        private static string Compact(string s) { return Regex.Replace(s, @"\s+", string.Empty); }

        /// <summary>True when the identifier at <paramref name="index"/> is preceded by the keyword "function" (optionally "function*").</summary>
        private static bool FollowsFunctionKeyword(string masked, int index)
        {
            int i = index - 1;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            if (i >= 0 && masked[i] == '*')
            {
                i--;
                while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            }
            if (i < 7) return false;
            if (string.CompareOrdinal(masked, i - 7, "function", 0, 8) != 0) return false;
            if (i - 8 >= 0)
            {
                char before = masked[i - 8];
                if (char.IsLetterOrDigit(before) || before == '_' || before == '$') return false;
            }
            return true;
        }

        private static bool FollowsAssignment(string masked, int index)
        {
            int i = index - 1;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            if (i < 0) return false;
            if (masked[i] == ':') return true;
            if (masked[i] == '=') return !(i > 0 && (masked[i - 1] == '=' || masked[i - 1] == '!' || masked[i - 1] == '<' || masked[i - 1] == '>'));
            return false;
        }

        private static List<string> ParseParams(string args)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(args)) return list;
            foreach (var p in args.Split(','))
            {
                string t = p.Trim();
                int eq = t.IndexOf('=');
                if (eq >= 0) t = t.Substring(0, eq).Trim();
                t = t.TrimStart('.');
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        private static bool TryFindEnd(string masked, int from, bool arrow, out int end)
        {
            int n = masked.Length, i = from;
            while (i < n && char.IsWhiteSpace(masked[i])) i++;
            if (i >= n) { end = -1; return false; }

            if (masked[i] != '{')
            {
                if (!arrow) { end = -1; return false; }
                // Arrow function with an expression body: extend to the end of the statement (approximate).
                int depth = 0;
                while (i < n)
                {
                    char ch = masked[i];
                    if (ch == '(' || ch == '[' || ch == '{') depth++;
                    else if (ch == ')' || ch == ']' || ch == '}') { if (depth == 0) break; depth--; }
                    else if ((ch == ';' || ch == '\n') && depth == 0) break;
                    i++;
                }
                end = Math.Max(from, i - 1);
                return true;
            }

            int d = 0;
            for (; i < n; i++)
            {
                if (masked[i] == '{') d++;
                else if (masked[i] == '}') { d--; if (d == 0) { end = i; return true; } }
            }
            end = -1;
            return false;
        }

        private static int[] BuildLineStarts(string s)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static int LineOf(int[] starts, int index)
        {
            int pos = Array.BinarySearch(starts, index);
            if (pos < 0) pos = ~pos - 1;
            return pos + 1;
        }
    }
}
