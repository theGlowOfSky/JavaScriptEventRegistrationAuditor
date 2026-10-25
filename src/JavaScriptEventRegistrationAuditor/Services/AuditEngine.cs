using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>Matches registrations to retrieved libraries/functions and derives findings. Pure logic: no Dataverse calls.</summary>
    public static class AuditEngine
    {
        /// <summary>
        /// Report ordering: Event Name (A-Z, case-insensitive), then Form Name, Control Name and finally the original
        /// position in the form XML, so handlers of the same event keep their execution order.
        /// </summary>
        public static List<HandlerRegistration> SortForReport(IEnumerable<HandlerRegistration> regs)
        {
            return regs
                .OrderBy(r => r.EventName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.FormName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.FormId)
                .ThenBy(r => r.ControlName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.OriginalOrder)
                .ToList();
        }

        public static void Run(EnvironmentData d, QualityOptions options)
        {
            var libByName = new Dictionary<string, WebResourceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in d.Libraries) if (!string.IsNullOrEmpty(l.Name) && !libByName.ContainsKey(l.Name)) libByName[l.Name] = l;

            // Index each retrieved library once.
            d.Definitions = new List<FunctionDef>();
            var defsByLib = new Dictionary<string, List<FunctionDef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var lib in d.Libraries.Where(l => l.Retrieved))
            {
                List<FunctionDef> defs;
                try { defs = FunctionScanner.Index(lib.Name, lib.Content); }
                catch (Exception ex)
                {
                    defs = new List<FunctionDef>();
                    d.Diagnostics.Add("Scan failed for library '" + lib.Name + "': " + ex.Message);
                }
                defsByLib[lib.Name] = defs;
                d.Definitions.AddRange(defs);
            }

            // Libraries available on each form: those attached to it plus those its handlers reference.
            var formLibs = new Dictionary<Guid, HashSet<string>>();
            foreach (var f in d.Forms)
            {
                if (!formLibs.ContainsKey(f.Id)) formLibs[f.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in f.LibraryNames) formLibs[f.Id].Add(n);
            }
            foreach (var r in d.Registrations)
            {
                if (!formLibs.ContainsKey(r.FormId)) formLibs[r.FormId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(r.LibraryName)) formLibs[r.FormId].Add(r.LibraryName);
            }

            // Duplicate detection (identical handler on the same form/event/control).
            var dupCounts = d.Registrations
                .GroupBy(r => DupKey(r))
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var r in d.Registrations)
            {
                r.ActionParts.Clear();
                r.QualityParts.Clear();
                r.Definition = null; r.FunctionCode = null; r.FunctionLine = null; r.FoundInLibrary = null; r.IsLibraryMismatch = false;
                if (r.IsPcf)
                {
                    // PCF controls are listed with the libraries from their manifest; there is no function to verify.
                    r.VerificationResult = VerificationResults.PcfControl;
                    if (string.Equals(r.LibraryName, PcfManifestParser.NotRetrieved, StringComparison.Ordinal))
                        r.ActionParts.Add("Control definition could not be retrieved; its libraries are unknown.");
                    continue;
                }
                Verify(r, libByName, defsByLib, formLibs.ContainsKey(r.FormId) ? formLibs[r.FormId] : new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                if (!r.Enabled) r.ActionParts.Add("Disabled handler (configuration, not necessarily a defect); confirm whether it is still needed.");
                if (dupCounts[DupKey(r)] > 1) r.ActionParts.Add("Needs Review: " + dupCounts[DupKey(r)] + " identical handlers on this form/event/control.");
                if (string.Equals(r.EventName, "onchange", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.ControlName))
                    r.ActionParts.Add("Needs Review: onchange registration has no attribute/control name.");
                CheckExecutionContext(r);
            }

            RefreshQuality(d, options);
            d.Dependencies = DependencyAnalyzer.Run(d, defsByLib);
            d.Tables = BuildTableSummaries(d);
        }

        /// <summary>Re-runs only the quality checks (cheap) so rule toggles in the UI apply without re-querying Dataverse.</summary>
        public static void RefreshQuality(EnvironmentData d, QualityOptions options)
        {
            d.Quality = QualityChecker.Run(d, options);
            foreach (var r in d.Registrations)
            {
                r.QualityParts.Clear();
                if (r.Definition == null) continue;
                var own = d.Quality.Where(q => string.Equals(q.Library, r.Definition.LibraryName, StringComparison.OrdinalIgnoreCase)
                                            && q.Function == r.Definition.FullName).Select(q => q.Check).Distinct().ToList();
                if (own.Count > 0) r.QualityParts.Add("Code checks flagged: " + string.Join(", ", own) + ".");
            }
        }

        private static string DupKey(HandlerRegistration r)
        {
            return string.Join("|", r.TableLogicalName, r.FormId, (r.EventName ?? "").ToLowerInvariant(),
                (r.ControlName ?? "").ToLowerInvariant(), (r.LibraryName ?? "").ToLowerInvariant(), r.FunctionName);
        }

        private static void Verify(HandlerRegistration r, Dictionary<string, WebResourceInfo> libByName, Dictionary<string, List<FunctionDef>> defsByLib, HashSet<string> formLibraries)
        {
            if (string.IsNullOrWhiteSpace(r.FunctionName))
            {
                r.VerificationResult = VerificationResults.NameMissing;
                r.ActionParts.Add("Handler has no function name; open the form and fix or remove the handler.");
                return;
            }

            WebResourceInfo lib;
            bool libRetrieved = r.LibraryName != null && libByName.TryGetValue(r.LibraryName, out lib) && lib.Retrieved;

            if (libRetrieved)
            {
                var inReg = FunctionScanner.Find(defsByLib[libByName[r.LibraryName].Name], r.FunctionName);
                if (inReg.Count == 1)
                {
                    Apply(r, inReg[0]);
                    if (!inReg[0].Complete)
                    {
                        r.VerificationResult = VerificationResults.Incomplete;
                        r.ActionParts.Add("Function was located but its end could not be determined by the regex scanner; review the library manually.");
                    }
                    else r.VerificationResult = VerificationResults.FoundInRegistered;
                    return;
                }
                if (inReg.Count > 1)
                {
                    Apply(r, inReg[0]);
                    r.VerificationResult = VerificationResults.Ambiguous;
                    r.ActionParts.Add("Needs Review: " + inReg.Count + " definitions match in this library (lines " + string.Join(", ", inReg.Select(x => x.StartLine)) + "); shown code is the first.");
                    return;
                }
            }

            // Not found in the registered library (or the library is unavailable): search the other libraries of the SAME FORM only.
            var others = defsByLib
                .Where(kv => formLibraries.Contains(kv.Key) && !string.Equals(kv.Key, r.LibraryName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(kv => FunctionScanner.Find(kv.Value, r.FunctionName)).ToList();
            var otherLibs = others.Select(o => o.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (!libRetrieved)
            {
                r.VerificationResult = VerificationResults.LibraryNotRetrieved;
                string why = libByName.ContainsKey(r.LibraryName ?? string.Empty) ? (libByName[r.LibraryName].Problem ?? "content unavailable") : "no JavaScript web resource with this name was returned";
                r.ActionParts.Add("Library '" + r.LibraryName + "' not retrieved (" + why + "); verify the web resource exists and you can read it.");
                if (otherLibs.Count == 1) { Apply(r, others[0]); r.ActionParts.Add("A matching function name exists in '" + otherLibs[0] + "'."); }
                return;
            }

            if (otherLibs.Count == 0)
            {
                r.VerificationResult = VerificationResults.NotFound;
                r.IsLibraryMismatch = true;
                r.ActionParts.Add("Function not found in referenced library '" + r.LibraryName + "' or in the other libraries of this form; check the function name/namespace and library assignment (the regex scanner may not recognise unusual patterns).");
            }
            else if (otherLibs.Count == 1 && others.Count == 1)
            {
                Apply(r, others[0]);
                r.VerificationResult = VerificationResults.FoundInOther;
                r.IsLibraryMismatch = true;
                r.ActionParts.Add("Function found in '" + otherLibs[0] + "' (line " + others[0].StartLine + ") on the same form, not in referenced library '" + r.LibraryName + "'; update the handler's library.");
            }
            else
            {
                r.VerificationResult = VerificationResults.Ambiguous;
                r.IsLibraryMismatch = true;
                r.ActionParts.Add("Needs Review: not found in referenced library '" + r.LibraryName + "'; name matches in " + otherLibs.Count + " other libraries of this form (" + string.Join(", ", otherLibs.Take(3)) + (otherLibs.Count > 3 ? ", ..." : "") + ").");
            }
        }

        private static void Apply(HandlerRegistration r, FunctionDef def)
        {
            r.Definition = def;
            r.FunctionCode = def.Code;
            r.FunctionLine = def.StartLine;
            r.FoundInLibrary = def.LibraryName;
        }

        private static void CheckExecutionContext(HandlerRegistration r)
        {
            var def = r.Definition;
            if (def == null || !def.Complete || r.PassExecutionContext || def.Parameters.Count == 0) return;
            if (!string.Equals(def.LibraryName, r.LibraryName, StringComparison.OrdinalIgnoreCase)) return;
            string p = Regex.Escape(def.Parameters[0]);
            var rx = new Regex(@"(?<![\w$.])" + p + @"\s*\.\s*(getFormContext|getEventSource|getEventArgs|getSharedVariable|setSharedVariable)\s*\(");
            if (rx.IsMatch(JsMasker.Mask(def.Code)))
                r.ActionParts.Add("Needs Review: source uses '" + def.Parameters[0] + "' as the execution context but 'Pass Execution Context' is off.");
        }

        public static List<TableSummary> BuildTableSummaries(EnvironmentData d)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in d.Tables) names[t.LogicalName] = t.DisplayName;

            var tables = d.Forms.Select(f => f.TableLogicalName).Concat(d.Registrations.Select(r => r.TableLogicalName))
                .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            var list = new List<TableSummary>();
            foreach (var t in tables)
            {
                var regs = d.Registrations.Where(r => string.Equals(r.TableLogicalName, t, StringComparison.OrdinalIgnoreCase)).ToList();
                string display;
                list.Add(new TableSummary
                {
                    LogicalName = t,
                    DisplayName = names.TryGetValue(t, out display) ? display : null,
                    Forms = d.Forms.Count(f => string.Equals(f.TableLogicalName, t, StringComparison.OrdinalIgnoreCase)),
                    Events = regs.Count,
                    Enabled = regs.Count(r => r.Enabled),
                    Disabled = regs.Count(r => !r.Enabled),
                    // Function not found in its referenced library (not found at all, or found only in another library of the form); each registration counted once.
                    Mismatches = regs.Count(r => r.IsLibraryMismatch)
                });
            }
            return list;
        }
    }
}
