using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Per-session cache so repeated scans do not download the same table forms or JavaScript libraries twice.
    /// Discarded whenever the table list is reloaded or the connection changes.
    /// </summary>
    public class ScanCache
    {
        /// <summary>Raw systemform rows per table (an empty list means "retrieved, table has no forms").</summary>
        public Dictionary<string, List<Entity>> FormsByTable { get; } = new Dictionary<string, List<Entity>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>JavaScript libraries that were referenced by scanned forms, by web resource name.</summary>
        public Dictionary<string, WebResourceInfo> LibrariesByName { get; } = new Dictionary<string, WebResourceInfo>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Library names already requested (found or not), so a missing library is not requested again.</summary>
        /// <summary>Libraries listed in the manifest of each PCF control (by custom control name).</summary>
        public Dictionary<string, string> PcfLibraries { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LibraryNamesQueried { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ScanResult
    {
        /// <summary>Audit data for the successfully scanned tables only.</summary>
        public EnvironmentData Data { get; set; }
        public List<string> ScannedTables { get; } = new List<string>();
        public List<string> FailedTables { get; } = new List<string>();
        /// <summary>Human-readable time spent per phase (also written to the trace output).</summary>
        public string TimingSummary { get; set; }
    }

    /// <summary>Loads table metadata, forms and JavaScript web resources using the connection supplied by XrmToolBox, then runs the audit.</summary>
    public class DataverseRetriever
    {
        private const int PageSize = 100;
        private static readonly int[] ThrottleCodes = { -2147015902, -2147015903, -2147015898 };

        public EnvironmentData Load(IOrganizationService svc, string environmentName, QualityOptions options, IProgress<ProgressInfo> progress, CancellationToken ct)
        {
            var data = new EnvironmentData { EnvironmentName = environmentName, CapturedUtc = DateTime.UtcNow };
            Action<int, string> report = (p, m) => { if (progress != null) progress.Report(new ProgressInfo { Percent = p, Message = m }); };

            // 1. Table display names (failure is non-fatal: logical names are still shown)
            report(5, "Reading table metadata...");
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var resp = (RetrieveAllEntitiesResponse)Retry(() => svc.Execute(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false }), ct);
                foreach (var em in resp.EntityMetadata)
                    names[em.LogicalName] = em.DisplayName != null && em.DisplayName.UserLocalizedLabel != null ? em.DisplayName.UserLocalizedLabel.Label : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { data.Diagnostics.Add("Table metadata could not be retrieved (display names unavailable): " + ex.Message); }

            // 2. JavaScript web resources
            report(15, "Retrieving JavaScript web resources...");
            RetrieveLibraries(svc, data, report, ct);

            // 3. Forms
            report(40, "Retrieving forms...");
            var formEntities = RetrieveForms(svc, data, report, ct);

            // 4. Parse form XML
            report(70, "Parsing form event registrations...");
            int order = 0;
            foreach (var e in formEntities)
            {
                ct.ThrowIfCancellationRequested();
                var type = e.GetAttributeValue<OptionSetValue>("type");
                var form = new FormInfo
                {
                    Id = e.Id,
                    Name = e.GetAttributeValue<string>("name"),
                    TableLogicalName = e.GetAttributeValue<string>("objecttypecode"),
                    TypeCode = type != null ? type.Value : 0
                };
                form.TypeName = FormInfo.TypeLabel(form.TypeCode);
                string problem;
                var regs = FormXmlParser.Parse(form, e.GetAttributeValue<string>("formxml"), ref order, out problem);
                form.XmlParsed = problem == null;
                form.Problem = problem;
                if (problem != null) data.Diagnostics.Add("Form '" + form.Name + "' (" + form.TableLogicalName + ", " + form.Id + "): " + problem);
                data.Forms.Add(form);
                data.Registrations.AddRange(regs);
            }

            // 5. Match / analyse
            report(85, "Matching functions and running checks...");
            foreach (var kv in names) data.Tables.Add(new TableSummary { LogicalName = kv.Key, DisplayName = kv.Value });
            AuditEngine.Run(data, options);

            report(100, "Done: " + data.Registrations.Count + " registrations in " + data.Forms.Count + " forms.");
            return data;
        }

        private const int LibraryNameChunkSize = 25;

        private static void Trace(string message)
        {
            System.Diagnostics.Trace.TraceInformation("[JSEventAuditor] " + message);
        }

        private static string Secs(Stopwatch sw) { return (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s"; }

        /// <summary>
        /// Step 1: table metadata only. Only the properties needed for filtering/display are requested
        /// (RetrieveMetadataChanges), and only tables that can have form event registrations are returned
        /// (see <see cref="TableSelection.IsEligible"/>). No forms, form XML or web resources are retrieved.
        /// Failures are thrown to the caller so the UI can report them.
        /// </summary>
        public List<TableSummary> LoadTables(IOrganizationService svc, IProgress<ProgressInfo> progress, CancellationToken ct)
        {
            Action<int, string> report = (p, m) => { if (progress != null) progress.Report(new ProgressInfo { Percent = p, Message = m }); };
            var sw = Stopwatch.StartNew();

            report(10, "Reading table metadata...");
            IEnumerable<EntityMetadata> all; // collection from RetrieveMetadataChanges, array from RetrieveAllEntities
            try
            {
                var query = new EntityQueryExpression { Properties = new MetadataPropertiesExpression { AllProperties = false } };
                query.Properties.PropertyNames.AddRange(new[] { "LogicalName", "DisplayName", "IsIntersect", "IsPrivate", "IsLogicalEntity", "CanCreateForms", "IsCustomizable" });
                var resp = (RetrieveMetadataChangesResponse)Retry(() => svc.Execute(new RetrieveMetadataChangesRequest { Query = query }), ct);
                all = resp.EntityMetadata;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Never silent: recorded in the trace output; the older full-metadata call is used instead.
                Trace("RetrieveMetadataChanges failed (" + ex.Message + "); falling back to RetrieveAllEntities.");
                var resp = (RetrieveAllEntitiesResponse)Retry(() => svc.Execute(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false }), ct);
                all = resp.EntityMetadata;
            }
            ct.ThrowIfCancellationRequested();

            report(80, "Filtering tables...");
            var list = new List<TableSummary>();
            int total = 0;
            foreach (var em in all)
            {
                total++;
                bool? canCreateForms = em.CanCreateForms != null ? (bool?)em.CanCreateForms.Value : null;
                bool? customizable = em.IsCustomizable != null ? (bool?)em.IsCustomizable.Value : null;
                if (!TableSelection.IsEligible(em.LogicalName, em.IsIntersect, em.IsPrivate, em.IsLogicalEntity, canCreateForms, customizable)) continue;
                list.Add(new TableSummary { LogicalName = em.LogicalName, DisplayName = DisplayLabel(em) });
            }
            list.Sort((a, b) => string.Compare(a.LogicalName, b.LogicalName, StringComparison.OrdinalIgnoreCase));

            Trace("Table metadata: " + total + " tables retrieved, " + list.Count + " eligible, " + sw.ElapsedMilliseconds + " ms.");
            report(100, "Loaded " + list.Count + " eligible tables (" + (total - list.Count) + " backend/system tables hidden) in " + Secs(sw) + ".");
            return list;
        }

        private static string DisplayLabel(EntityMetadata em)
        {
            if (em.DisplayName == null) return null;
            if (em.DisplayName.UserLocalizedLabel != null) return em.DisplayName.UserLocalizedLabel.Label;
            var first = em.DisplayName.LocalizedLabels.FirstOrDefault();
            return first != null ? first.Label : null;
        }

        /// <summary>
        /// Step 3: retrieves forms for the selected tables only, parses registrations and runs the existing audit.
        /// Only the JavaScript libraries registered on those forms are retrieved (by name, in chunks); no other web
        /// resource in the environment is downloaded or analysed. Tables/libraries already retrieved in this session are
        /// served from <paramref name="cache"/>. A failure for some tables is recorded in <see cref="ScanResult.FailedTables"/>;
        /// other tables still complete. All requests are sequential (no concurrent load on Dataverse).
        /// </summary>
        public ScanResult LoadRegistrations(IOrganizationService svc, string environmentName, IList<TableSummary> availableTables, IList<string> selectedTables,
            ScanCache cache, QualityOptions options, IProgress<ProgressInfo> progress, CancellationToken ct, ScanOptions scanOptions = null)
        {
            if (scanOptions == null) scanOptions = new ScanOptions(); // subgrid events and PCF controls are off unless requested
            Action<int, string> report = (p, m) => { if (progress != null) progress.Report(new ProgressInfo { Percent = p, Message = m }); };
            var overall = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            var result = new ScanResult();
            var data = new EnvironmentData { EnvironmentName = environmentName, CapturedUtc = DateTime.UtcNow };
            result.Data = data;
            foreach (var t in availableTables) data.Tables.Add(new TableSummary { LogicalName = t.LogicalName, DisplayName = t.DisplayName });

            // 1. Forms for tables not yet retrieved in this session (chunked so the IN filter stays small)
            var tables = selectedTables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = ScanPlanner.MissingTables(tables, cache.FormsByTable.ContainsKey);
            var chunks = TableSelection.Chunk(missing, FormTableChunkSize);
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                report(5 + 45 * i / Math.Max(1, chunks.Count), "Retrieving forms (" + Math.Min(missing.Count, (i + 1) * FormTableChunkSize) + " of " + missing.Count + " tables)...");
                FetchForms(svc, chunks[i], cache, data, result, ct);
            }
            if (missing.Count == 0) report(50, "Forms for the selected tables were retrieved earlier; reusing them.");
            string formsTime = Secs(sw);

            // 2. Parse form XML for the successfully retrieved tables
            sw.Restart();
            report(55, "Parsing form event registrations...");
            int order = 0;
            foreach (var t in tables.Where(x => !result.FailedTables.Contains(x, StringComparer.OrdinalIgnoreCase)))
            {
                result.ScannedTables.Add(t);
                foreach (var e in cache.FormsByTable[t])
                {
                    ct.ThrowIfCancellationRequested();
                    var type = e.GetAttributeValue<OptionSetValue>("type");
                    var form = new FormInfo
                    {
                        Id = e.Id,
                        Name = e.GetAttributeValue<string>("name"),
                        TableLogicalName = e.GetAttributeValue<string>("objecttypecode"),
                        TypeCode = type != null ? type.Value : 0
                    };
                    form.TypeName = FormInfo.TypeLabel(form.TypeCode);
                    string problem;
                    var regs = FormXmlParser.Parse(form, e.GetAttributeValue<string>("formxml"), ref order, out problem);
                    form.XmlParsed = problem == null;
                    form.Problem = problem;
                    if (problem != null) data.Diagnostics.Add("Form '" + form.Name + "' (" + form.TableLogicalName + ", " + form.Id + "): " + problem);
                    data.Forms.Add(form);
                    data.Registrations.AddRange(scanOptions.IncludeSubgridEvents ? regs : regs.Where(r => !r.IsControlEvent).ToList());
                }
            }
            string parseTime = Secs(sw);

            // 3. Only the JavaScript libraries registered on these forms
            sw.Restart();
            var formLibraryNames = data.Forms.SelectMany(f => f.LibraryNames).ToList();
            int distinctLibraries = data.Registrations.Select(r => r.LibraryName).Concat(formLibraryNames).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            report(58, data.Forms.Count + " form(s) of the selected table(s) register " + distinctLibraries + " distinct JavaScript librar" + (distinctLibraries == 1 ? "y" : "ies") + ".");
            var needed = ScanPlanner.LibraryNamesToFetch(data.Registrations, n => cache.LibraryNamesQueried.Contains(n), formLibraryNames);
            if (needed.Count > 0) FetchLibrariesByName(svc, needed, cache, ct, report);
            else report(85, "JavaScript libraries for these forms were retrieved earlier; reusing them.");
            foreach (var name in data.Registrations.Select(r => r.LibraryName).Concat(formLibraryNames).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                WebResourceInfo lib;
                if (cache.LibrariesByName.TryGetValue(name, out lib)) AddLibrary(data, lib);
            }
            string libTime = Secs(sw);

            // 3b. PCF (custom) controls found on these forms are listed with the libraries from their manifest
            sw.Restart();
            var pcfNames = (scanOptions.IncludePcfControls ? data.Forms.SelectMany(f => f.PcfControls) : Enumerable.Empty<PcfControlRef>()).Select(c => c.CustomControlName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (pcfNames.Count > 0)
            {
                try { FetchPcfLibraries(svc, pcfNames, cache, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    data.Diagnostics.Add("PCF control definitions could not be retrieved (" + ex.Message + "); their libraries are listed as unknown.");
                }
            }
            foreach (var form in data.Forms)
            {
                foreach (var pcf in scanOptions.IncludePcfControls ? form.PcfControls : new List<PcfControlRef>())
                {
                    string libs;
                    if (!cache.PcfLibraries.TryGetValue(pcf.CustomControlName, out libs)) libs = PcfManifestParser.NotRetrieved;
                    data.Registrations.Add(new HandlerRegistration
                    {
                        TableLogicalName = form.TableLogicalName,
                        FormName = form.Name,
                        FormId = form.Id,
                        FormType = form.TypeName,
                        ControlName = pcf.ControlName ?? string.Empty,
                        EventName = "PCF Control",
                        HandlerId = string.Empty,
                        FunctionName = pcf.CustomControlName,
                        LibraryName = libs,
                        Enabled = true,
                        IsPcf = true,
                        OriginalOrder = order++
                    });
                }
            }
            if (pcfNames.Count > 0) libTime = Secs(sw) + " (+PCF " + pcfNames.Count + ")";

            // 4. Existing matching / analysis, limited to the scanned tables and their libraries
            sw.Restart();
            report(88, "Matching functions and running checks...");
            AuditEngine.Run(data, options);
            string analysisTime = Secs(sw);

            result.TimingSummary = data.Forms.Count + " forms / " + data.Libraries.Count + " libraries: forms " + formsTime + ", parsing " + parseTime + ", libraries " + libTime + ", analysis " + analysisTime
                + ", total " + Secs(overall);
            Trace("Registration scan of " + tables.Count + " table(s): " + result.TimingSummary + ". " + data.Registrations.Count + " registrations, " + data.Libraries.Count + " libraries analysed.");
            report(100, "Done: " + data.Registrations.Count + " registrations in " + data.Forms.Count + " forms.");
            return result;
        }

        /// <summary>Reads the manifest of each PCF control (customcontrol) once per session, a small chunk of names per request.</summary>
        private void FetchPcfLibraries(IOrganizationService svc, List<string> names, ScanCache cache, CancellationToken ct)
        {
            foreach (var chunk in TableSelection.Chunk(names.Where(n => !cache.PcfLibraries.ContainsKey(n)).ToList(), LibraryNameChunkSize))
            {
                ct.ThrowIfCancellationRequested();

                var q = new QueryExpression("customcontrol") { ColumnSet = new ColumnSet("name", "manifest") };
                q.Criteria.AddCondition("name", ConditionOperator.In, chunk.Cast<object>().ToArray());

                foreach (var e in RetrieveAll(svc, q, ct, 50))
                {
                    string name = e.GetAttributeValue<string>("name");
                    if (!string.IsNullOrEmpty(name)) cache.PcfLibraries[name] = PcfManifestParser.Libraries(e.GetAttributeValue<string>("manifest"));
                }
                foreach (var n in chunk)
                    if (!cache.PcfLibraries.ContainsKey(n)) cache.PcfLibraries[n] = PcfManifestParser.NotRetrieved;
            }
        }

        private static void AddLibrary(EnvironmentData data, WebResourceInfo lib)
        {
            data.Libraries.Add(lib);
            if (!lib.Retrieved) data.Diagnostics.Add("Library '" + lib.Name + "': " + lib.Problem);
        }

        /// <summary>Retrieves the named JavaScript web resources (with content), a small chunk of names per request.</summary>
        private void FetchLibrariesByName(IOrganizationService svc, List<string> names, ScanCache cache, CancellationToken ct, Action<int, string> report)
        {
            var chunks = TableSelection.Chunk(names, LibraryNameChunkSize);
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[i];
                report(60 + 25 * i / Math.Max(1, chunks.Count), "Retrieving JavaScript libraries registered on these forms (" + Math.Min(names.Count, (i + 1) * LibraryNameChunkSize) + " of " + names.Count + ")...");

                Func<bool, QueryExpression> build = useState =>
                {
                    var q = new QueryExpression("webresource") { ColumnSet = new ColumnSet("webresourceid", "name", "displayname", "content") };
                    q.Criteria.AddCondition("webresourcetype", ConditionOperator.Equal, 3); // 3 = Script (JScript)
                    q.Criteria.AddCondition("name", ConditionOperator.In, chunk.Cast<object>().ToArray());
                    if (useState) q.Criteria.AddCondition("componentstate", ConditionOperator.Equal, 0);
                    return q;
                };

                List<Entity> rows;
                try { rows = RetrieveAll(svc, build(true), ct, 50).ToList(); }
                catch (FaultException<OrganizationServiceFault>) { rows = RetrieveAll(svc, build(false), ct, 50).ToList(); }

                int unexpected = 0;
                foreach (var e in rows)
                {
                    string rowName = e.GetAttributeValue<string>("name");
                    if (string.IsNullOrEmpty(rowName) || !chunk.Contains(rowName, StringComparer.OrdinalIgnoreCase)) { unexpected++; continue; } // never keep libraries that were not requested
                    var lib = DecodeLibrary(e);
                    cache.LibrariesByName[lib.Name] = lib;
                }
                if (unexpected > 0) Trace("Ignored " + unexpected + " web resource(s) returned although not requested.");
                foreach (var n in chunk) cache.LibraryNamesQueried.Add(n);
            }
        }

        private static WebResourceInfo DecodeLibrary(Entity e)
        {
            var lib = new WebResourceInfo { Id = e.Id, Name = e.GetAttributeValue<string>("name"), DisplayName = e.GetAttributeValue<string>("displayname") };
            string b64 = e.GetAttributeValue<string>("content");
            if (string.IsNullOrEmpty(b64)) lib.Problem = "content is empty";
            else
            {
                try
                {
                    using (var ms = new MemoryStream(Convert.FromBase64String(b64)))
                    using (var sr = new StreamReader(ms, new UTF8Encoding(false), true))
                        lib.Content = sr.ReadToEnd();
                    lib.Retrieved = true;
                }
                catch (Exception ex) { lib.Problem = "content could not be decoded: " + ex.Message; }
            }
            return lib;
        }

        private const int FormTableChunkSize = 25;

        /// <summary>Retrieves forms for a chunk of tables. If the chunk fails, each table is retried on its own to isolate the failing one.</summary>
        private void FetchForms(IOrganizationService svc, List<string> chunk, ScanCache cache, EnvironmentData data, ScanResult result, CancellationToken ct)
        {
            try
            {
                var tmp = new EnvironmentData();
                var rows = RetrieveForms(svc, tmp, (p, m) => { }, ct, chunk);
                data.Diagnostics.AddRange(tmp.Diagnostics);
                foreach (var t in chunk) cache.FormsByTable[t] = new List<Entity>();
                int ignored = 0;
                foreach (var e in rows)
                {
                    List<Entity> list;
                    string t = e.GetAttributeValue<string>("objecttypecode");
                    if (t != null && cache.FormsByTable.TryGetValue(t, out list)) list.Add(e);
                    else ignored++;
                }
                if (ignored > 0) data.Diagnostics.Add(ignored + " form(s) of tables that were not selected were returned by Dataverse and ignored.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (chunk.Count > 1)
                {
                    data.Diagnostics.Add("Retrieving forms for " + chunk.Count + " tables failed (" + ex.Message + "); retrying table by table.");
                    foreach (var t in chunk) FetchForms(svc, new List<string> { t }, cache, data, result, ct);
                }
                else
                {
                    result.FailedTables.Add(chunk[0]);
                    data.Diagnostics.Add("Forms could not be retrieved for table '" + chunk[0] + "': " + ex.Message);
                }
            }
        }

        private void RetrieveLibraries(IOrganizationService svc, EnvironmentData data, Action<int, string> report, CancellationToken ct)
        {
            Func<bool, QueryExpression> build = useState =>
            {
                var q = new QueryExpression("webresource") { ColumnSet = new ColumnSet("webresourceid", "name", "displayname", "content") };
                q.Criteria.AddCondition("webresourcetype", ConditionOperator.Equal, 3); // 3 = Script (JScript)
                if (useState) q.Criteria.AddCondition("componentstate", ConditionOperator.Equal, 0); // published only, avoids unpublished duplicates
                return q;
            };

            List<Entity> rows;
            try { rows = RetrieveAll(svc, build(true), ct, 50).ToList(); }
            catch (FaultException<OrganizationServiceFault>) { rows = RetrieveAll(svc, build(false), ct, 50).ToList(); }

            foreach (var e in rows)
            {
                var lib = new WebResourceInfo { Id = e.Id, Name = e.GetAttributeValue<string>("name"), DisplayName = e.GetAttributeValue<string>("displayname") };
                string b64 = e.GetAttributeValue<string>("content");
                if (string.IsNullOrEmpty(b64)) lib.Problem = "content is empty";
                else
                {
                    try
                    {
                        using (var ms = new MemoryStream(Convert.FromBase64String(b64)))
                        using (var sr = new StreamReader(ms, new UTF8Encoding(false), true))
                            lib.Content = sr.ReadToEnd();
                        lib.Retrieved = true;
                    }
                    catch (Exception ex) { lib.Problem = "content could not be decoded: " + ex.Message; }
                }
                if (!lib.Retrieved) data.Diagnostics.Add("Library '" + lib.Name + "': " + lib.Problem);
                data.Libraries.Add(lib);
            }
            report(35, "Retrieved " + data.Libraries.Count + " JavaScript libraries.");
        }

        private List<Entity> RetrieveForms(IOrganizationService svc, EnvironmentData data, Action<int, string> report, CancellationToken ct, IEnumerable<string> tables = null)
        {
            Func<bool, QueryExpression> build = useState =>
            {
                var q = new QueryExpression("systemform") { ColumnSet = new ColumnSet("formid", "name", "objecttypecode", "type", "formxml") };
                q.Criteria.AddCondition("type", ConditionOperator.In, new object[] { 2, 5, 6, 7, 11, 12 });
                if (tables != null) q.Criteria.AddCondition("objecttypecode", ConditionOperator.In, tables.Cast<object>().ToArray());
                if (useState) q.Criteria.AddCondition("componentstate", ConditionOperator.Equal, 0);
                return q;
            };
            List<Entity> rows;
            try { rows = RetrieveAll(svc, build(true), ct, 50).ToList(); }
            catch (FaultException<OrganizationServiceFault>)
            {
                data.Diagnostics.Add("Form query with componentstate filter failed; retried without it (unpublished duplicates are possible).");
                rows = RetrieveAll(svc, build(false), ct, 50).ToList();
            }
            report(65, "Retrieved " + rows.Count + " forms.");
            return rows;
        }

        private static IEnumerable<Entity> RetrieveAll(IOrganizationService svc, QueryExpression q, CancellationToken ct, int pageSize = PageSize)
        {
            q.PageInfo = new PagingInfo { Count = pageSize, PageNumber = 1, PagingCookie = null };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = Retry(() => svc.RetrieveMultiple(q), ct);
                foreach (var e in page.Entities) yield return e;
                if (!page.MoreRecords) yield break;
                q.PageInfo.PageNumber++;
                q.PageInfo.PagingCookie = page.PagingCookie;
            }
        }

        /// <summary>Retries on Dataverse service-protection throttling (honouring Retry-After) and on a single timeout.</summary>
        private static T Retry<T>(Func<T> action, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return action(); }
                catch (FaultException<OrganizationServiceFault> ex) when (attempt < 4 && ThrottleCodes.Contains(ex.Detail.ErrorCode))
                {
                    TimeSpan wait = TimeSpan.FromSeconds(5 * (attempt + 1));
                    object ra;
                    if (ex.Detail.ErrorDetails != null && ex.Detail.ErrorDetails.TryGetValue("Retry-After", out ra) && ra is TimeSpan) wait = (TimeSpan)ra;
                    if (ct.WaitHandle.WaitOne(wait)) ct.ThrowIfCancellationRequested();
                }
                catch (TimeoutException) when (attempt < 1) { }
            }
        }
    }
}
