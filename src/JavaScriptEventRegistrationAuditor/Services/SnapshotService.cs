using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    public class SnapshotRow
    {
        public string Table { get; set; }
        public string FormName { get; set; }
        public Guid FormId { get; set; }
        public string FormType { get; set; }
        public string Control { get; set; }
        public string Event { get; set; }
        public string HandlerId { get; set; }
        public string Function { get; set; }
        public string Library { get; set; }
        public bool Enabled { get; set; }
        public bool PassExecutionContext { get; set; }
        public string VerificationResult { get; set; }
        /// <summary>SHA-256 of the extracted function source (line endings normalised); null when no source was extracted.</summary>
        public string CodeHash { get; set; }
    }

    public class Snapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string ToolVersion { get; set; }
        /// <summary>Name of the environment that produced this snapshot (never the current connection when loaded from a file).</summary>
        public string EnvironmentName { get; set; }
        public DateTime CapturedUtc { get; set; }
        public List<SnapshotRow> Rows { get; set; } = new List<SnapshotRow>();
    }

    public static class SnapshotService
    {
        public static Snapshot Create(EnvironmentData d, IEnumerable<string> tables, string toolVersion)
        {
            var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            return new Snapshot
            {
                ToolVersion = toolVersion,
                EnvironmentName = d.EnvironmentName,
                CapturedUtc = d.CapturedUtc,
                Rows = AuditEngine.SortForReport(d.Registrations.Where(r => set.Contains(r.TableLogicalName))).Select(r => new SnapshotRow
                {
                    Table = r.TableLogicalName, FormName = r.FormName, FormId = r.FormId, FormType = r.FormType, Control = r.ControlName,
                    Event = r.EventName, HandlerId = r.HandlerId, Function = r.FunctionName, Library = r.LibraryName, Enabled = r.Enabled,
                    PassExecutionContext = r.PassExecutionContext, VerificationResult = r.VerificationResult, CodeHash = Hash(r.FunctionCode)
                }).ToList()
            };
        }

        public static void Save(Snapshot s, string path) { File.WriteAllText(path, JsonConvert.SerializeObject(s, Formatting.Indented), new UTF8Encoding(false)); }

        public static Snapshot Load(string path)
        {
            var s = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
            if (s == null || s.Rows == null) throw new InvalidDataException("The file is not a valid snapshot.");
            return s;
        }

        public static string Hash(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(code.Replace("\r\n", "\n")))).Replace("-", "");
        }

        /// <summary>Compares a baseline snapshot (other environment/time) against the current audit. Added/Removed are relative to the baseline.</summary>
        public static List<DiffRow> Compare(Snapshot baseline, Snapshot current)
        {
            var diffs = new List<DiffRow>();
            Func<SnapshotRow, string> primary = r => string.Join("|", r.Table, r.FormId, (r.Event ?? "").ToLowerInvariant(), (r.Control ?? "").ToLowerInvariant(), (r.HandlerId ?? "").ToLowerInvariant());
            Func<SnapshotRow, string> fallback = r => string.Join("|", r.Table, r.FormId, (r.Event ?? "").ToLowerInvariant(), (r.Control ?? "").ToLowerInvariant(), r.Function, (r.Library ?? "").ToLowerInvariant());

            var baseRows = baseline.Rows.ToList();
            var curRows = current.Rows.ToList();
            var pairs = new List<Tuple<SnapshotRow, SnapshotRow>>();

            foreach (var keyFn in new[] { primary, fallback })
            {
                var bLookup = baseRows.Where(b => keyFn == primary ? !string.IsNullOrEmpty(b.HandlerId) : true).GroupBy(keyFn).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
                foreach (var c in curRows.ToList())
                {
                    if (keyFn == primary && string.IsNullOrEmpty(c.HandlerId)) continue;
                    SnapshotRow b;
                    if (bLookup.TryGetValue(keyFn(c), out b) && baseRows.Contains(b))
                    {
                        pairs.Add(Tuple.Create(b, c)); baseRows.Remove(b); curRows.Remove(c);
                    }
                }
            }

            foreach (var p in pairs)
            {
                var b = p.Item1; var c = p.Item2; var changes = new List<string>();
                if (b.Function != c.Function) changes.Add("function: " + b.Function + " -> " + c.Function);
                if (!string.Equals(b.Library, c.Library, StringComparison.OrdinalIgnoreCase)) changes.Add("library: " + b.Library + " -> " + c.Library);
                if (b.PassExecutionContext != c.PassExecutionContext) changes.Add("pass execution context: " + b.PassExecutionContext + " -> " + c.PassExecutionContext);
                if (b.CodeHash != c.CodeHash) changes.Add("function source differs");
                string change = b.Enabled != c.Enabled ? (c.Enabled ? "Enabled" : "Disabled") : (changes.Count > 0 ? "Changed" : null);
                if (b.Enabled != c.Enabled && changes.Count > 0) changes.Insert(0, "enabled state changed");
                if (change != null) diffs.Add(Row(change, c, string.Join("; ", changes)));
            }
            foreach (var c in curRows) diffs.Add(Row("Added", c, "Present in current environment, absent from snapshot."));
            foreach (var b in baseRows) diffs.Add(Row("Removed", b, "Present in snapshot, absent from current environment."));
            return diffs.OrderBy(x => x.Table, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Event, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Form, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static DiffRow Row(string change, SnapshotRow r, string detail)
        {
            return new DiffRow { Change = change, Table = r.Table, Form = r.FormName, Event = r.Event, Control = r.Control, Function = r.Function, Detail = detail };
        }
    }
}
