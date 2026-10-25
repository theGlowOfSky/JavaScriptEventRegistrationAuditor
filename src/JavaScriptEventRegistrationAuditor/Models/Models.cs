using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace JavaScriptEventRegistrationAuditor.Models
{
    /// <summary>Allowed values of the "Function Verification Result" column.</summary>
    public static class VerificationResults
    {
        public const string FoundInRegistered = "Found in registered library";
        public const string FoundInOther = "Found in another library on the same form — library mismatch";
        public const string NotFound = "Function not found in referenced library";
        public const string LibraryNotRetrieved = "Registered library not retrieved";
        public const string Ambiguous = "Ambiguous function match";
        public const string Incomplete = "Function extraction incomplete";
        public const string NameMissing = "Function name missing";
        public const string PcfControl = "PCF control (not verified)";
    }

    /// <summary>A JavaScript web resource (type 3) retrieved from Dataverse.</summary>
    public class WebResourceInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Content { get; set; }
        /// <summary>True when content was retrieved and decoded successfully.</summary>
        public bool Retrieved { get; set; }
        public string Problem { get; set; }
    }

    public class FormInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string TableLogicalName { get; set; }
        public int TypeCode { get; set; }
        public string TypeName { get; set; }
        public bool XmlParsed { get; set; }
        public string Problem { get; set; }
        /// <summary>Libraries attached to the form (formLibraries) in the form XML.</summary>
        public List<string> LibraryNames { get; set; } = new List<string>();
        /// <summary>Custom (PCF) controls found on the form.</summary>
        public List<PcfControlRef> PcfControls { get; set; } = new List<PcfControlRef>();

        public static string TypeLabel(int code)
        {
            switch (code)
            {
                case 2: return "Main";
                case 5: return "Mobile - Express";
                case 6: return "Quick View";
                case 7: return "Quick Create";
                case 11: return "Card";
                case 12: return "Main - Interactive experience";
                default: return "Type " + code;
            }
        }
    }

    /// <summary>A custom (PCF) control found in a form's controlDescriptions.</summary>
    public class PcfControlRef
    {
        /// <summary>The form control the PCF is bound to (data field name or control id).</summary>
        public string ControlName { get; set; }
        /// <summary>Name of the custom control, e.g. "contoso_Contoso.MyControl".</summary>
        public string CustomControlName { get; set; }
    }

    /// <summary>A function definition located by the regex-based scanner (best effort).</summary>
    public class FunctionDef
    {
        public string LibraryName { get; set; }
        /// <summary>Name as written, e.g. "Contoso.Account.onLoad" or "onLoad".</summary>
        public string FullName { get; set; }
        public string ShortName { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Code { get; set; }
        /// <summary>False when the closing brace could not be located; Code then holds a partial snippet.</summary>
        public bool Complete { get; set; }
        public List<string> Parameters { get; set; } = new List<string>();
        public string Kind { get; set; }
    }

    /// <summary>One handler registration. Every registration stays a separate record.</summary>
    public class HandlerRegistration
    {
        public string TableLogicalName { get; set; }
        public string FormName { get; set; }
        public Guid FormId { get; set; }
        public string FormType { get; set; }
        public string ControlName { get; set; }
        public string EventName { get; set; }
        public string HandlerId { get; set; }
        public string FunctionName { get; set; }
        public bool Enabled { get; set; }
        public bool PassExecutionContext { get; set; }
        public string LibraryName { get; set; }
        public string Parameters { get; set; }
        /// <summary>Position in the parsed form XML (execution order within an event). Used as sort tie-breaker.</summary>
        public int OriginalOrder { get; set; }

        // ---- audit results ----
        public string FunctionCode { get; set; }
        public int? FunctionLine { get; set; }
        public string VerificationResult { get; set; }
        /// <summary>Library in which the function was actually detected (may differ from LibraryName).</summary>
        public string FoundInLibrary { get; set; }
        /// <summary>True when the function is not in the registered library: not found at all, or found only in another library on the same form.</summary>
        public bool IsLibraryMismatch { get; set; }
        /// <summary>True for a PCF (custom control) found on the form. FunctionName = control name, LibraryName = libraries from its manifest.</summary>
        public bool IsPcf { get; set; }
        /// <summary>True when the event is registered on a control (e.g. a subgrid) instead of the form or a field.</summary>
        public bool IsControlEvent { get; set; }
        public FunctionDef Definition { get; set; }
        public List<string> ActionParts { get; } = new List<string>();
        public List<string> QualityParts { get; } = new List<string>();

        public string RecommendedAction
        {
            get { return string.Join(" ", ActionParts.Concat(QualityParts)); }
        }
    }

    public class TableSummary
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public int Forms { get; set; }
        public int Events { get; set; }
        public int Enabled { get; set; }
        public int Disabled { get; set; }
        public int Mismatches { get; set; }
    }

    public class QualityFinding
    {
        public string Check { get; set; }
        public string Library { get; set; }
        public string Function { get; set; }
        public int? Line { get; set; }
        public int Occurrences { get; set; }
        public string Explanation { get; set; }
        public string Action { get; set; }
        /// <summary>"Pattern match" or "Needs Review" — never a confirmed defect.</summary>
        public string Confidence { get; set; }
    }

    public class DependencyRow
    {
        public string Function { get; set; }
        public string Library { get; set; }
        public string Relationship { get; set; }
        public string Detail { get; set; }
        /// <summary>"Detected" or "Inferred".</summary>
        public string Basis { get; set; }
    }

    /// <summary>What a registration scan includes in addition to the form and field events. Both are off by default.</summary>
    public class ScanOptions
    {
        /// <summary>Events registered on a control such as a subgrid (the event's control attribute).</summary>
        public bool IncludeSubgridEvents { get; set; }
        /// <summary>Custom (PCF) controls found on the forms, listed with the libraries from their manifest.</summary>
        public bool IncludePcfControls { get; set; }
    }

    public class QualityOptions
    {
        public bool DebuggerStatements { get; set; } = true;
        public bool LooseEquality { get; set; } = true;
        public bool DeprecatedApis { get; set; } = true;
    }

    public class ProgressInfo
    {
        public int Percent { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Everything retrieved and derived from one connected environment.</summary>
    public class EnvironmentData
    {
        public string EnvironmentName { get; set; }
        public DateTime CapturedUtc { get; set; }
        public List<TableSummary> Tables { get; set; } = new List<TableSummary>();
        public List<FormInfo> Forms { get; set; } = new List<FormInfo>();
        public List<HandlerRegistration> Registrations { get; set; } = new List<HandlerRegistration>();
        public List<WebResourceInfo> Libraries { get; set; } = new List<WebResourceInfo>();
        public List<FunctionDef> Definitions { get; set; } = new List<FunctionDef>();
        public List<QualityFinding> Quality { get; set; } = new List<QualityFinding>();
        public List<DependencyRow> Dependencies { get; set; } = new List<DependencyRow>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    // ---- grid row view-models ----
    public class TableRow
    {
        // Counts are null until the table's registrations have been loaded (blank in the grid, not a misleading 0).
        [DisplayName("Select")] public bool Selected { get; set; }
        [DisplayName("Table Logical Name")] public string LogicalName { get; set; }
        [DisplayName("Display Name")] public string DisplayName { get; set; }
        [DisplayName("Forms")] public int? Forms { get; set; }
        [DisplayName("Event Registrations")] public int? Events { get; set; }
        [DisplayName("Enabled")] public int? Enabled { get; set; }
        [DisplayName("Disabled")] public int? Disabled { get; set; }
        [DisplayName("Library Mismatches")] public int? Mismatches { get; set; }
    }

    /// <summary>One row of the preview grid: the same 15 columns, in the same order, as an exported table worksheet.</summary>
    public class RegistrationRow
    {
        [DisplayName("Table Logical Name")] public string TableLogicalName { get; set; }
        [DisplayName("Form Name")] public string FormName { get; set; }
        [DisplayName("Form ID")] public string FormId { get; set; }
        [DisplayName("Form Type")] public string FormType { get; set; }
        [DisplayName("Control Name")] public string ControlName { get; set; }
        [DisplayName("Event Name")] public string EventName { get; set; }
        [DisplayName("Handler ID")] public string HandlerId { get; set; }
        [DisplayName("Function Name")] public string FunctionName { get; set; }
        [DisplayName("Enabled")] public string Enabled { get; set; }
        [DisplayName("Pass Execution Context")] public string PassExecutionContext { get; set; }
        /// <summary>Source shown on one line; the full text (with line breaks) is in <see cref="FullFunctionCode"/> and the cell tooltip.</summary>
        [DisplayName("Function Code")] public string FunctionCode { get; set; }
        [DisplayName("Function Line Number")] public int? FunctionLineNumber { get; set; }
        [DisplayName("Registered Library")] public string RegisteredLibrary { get; set; }
        [DisplayName("Function Verification Result")] public string VerificationResult { get; set; }
        [DisplayName("Recommended Action")] public string RecommendedAction { get; set; }
        [Browsable(false)] public string FullFunctionCode { get; set; }
    }

    public class DiffRow
    {
        [DisplayName("Change")] public string Change { get; set; }
        [DisplayName("Table")] public string Table { get; set; }
        [DisplayName("Form")] public string Form { get; set; }
        [DisplayName("Event")] public string Event { get; set; }
        [DisplayName("Control")] public string Control { get; set; }
        [DisplayName("Function")] public string Function { get; set; }
        [DisplayName("Detail")] public string Detail { get; set; }
    }
}
