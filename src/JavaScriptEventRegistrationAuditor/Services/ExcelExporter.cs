using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    public class ExportResult
    {
        public int Tables { get; set; }
        public int Rows { get; set; }
        public List<string> TruncatedSourceFiles { get; } = new List<string>();
        public string SidecarFolder { get; set; }
    }

    /// <summary>Writes the workbook: "Table Index" followed by one worksheet per selected table. No other sheets are created.</summary>
    public class ExcelExporter
    {
        public const int ExcelCellLimit = 32767;
        private const int CodeLimit = 32000; // leaves room for the truncation notice
        private static readonly XLColor Header = XLColor.FromHtml("#0F6CBD");
        // Row highlights for library mismatches: function not found (light red) / found in another library of the form (light orange).
        private static readonly XLColor NotFoundFill = XLColor.FromHtml("#FFC7CE");
        private static readonly XLColor OtherLibraryFill = XLColor.FromHtml("#FFE0B3");
        private static readonly string[] SheetHeaders =
        {
            "Table Logical Name","Form Name","Form ID","Form Type","Control Name","Event Name","Handler ID","Function Name","Enabled",
            "Pass Execution Context","Function Code","Function Line Number","Registered Library","Function Verification Result","Recommended Action"
        };

        public static string DefaultFileName(string environmentName)
        {
            string n = SanitizeFileName(string.IsNullOrWhiteSpace(environmentName) ? "Environment" : environmentName);
            return n + "_JavaScriptEventsRegistrations.xlsx";
        }

        public static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "Environment" : name;
        }

        public static string SanitizeSheetName(string name)
        {
            string s = Regex.Replace(name ?? "Sheet", @"[\[\]:*?/\\']", "_").Trim();
            if (s.Length > 31) s = s.Substring(0, 31);
            return s.Length == 0 ? "Sheet" : s;
        }

        /// <summary>Builds a unique, Excel-safe sheet name per table (case-insensitive, max 31 chars, never "Table Index").</summary>
        public static Dictionary<string, string> BuildSheetNames(IEnumerable<string> tables)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Table Index" };
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                string baseName = SanitizeSheetName(t), name = baseName;
                for (int i = 2; used.Contains(name); i++)
                {
                    string suffix = "~" + i;
                    name = (baseName.Length + suffix.Length > 31 ? baseName.Substring(0, 31 - suffix.Length) : baseName) + suffix;
                }
                used.Add(name);
                map[t] = name;
            }
            return map;
        }

        public ExportResult Export(EnvironmentData data, IEnumerable<string> selectedTables, string path)
        {
            var tables = selectedTables.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            // Tables without any registration get no worksheet (they stay in the Table Index, without a link).
            var withRegistrations = tables.Where(t => data.Registrations.Any(r => string.Equals(r.TableLogicalName, t, StringComparison.OrdinalIgnoreCase))).ToList();
            var sheetNames = BuildSheetNames(withRegistrations);
            var result = new ExportResult { Tables = withRegistrations.Count };
            string sidecar = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + "_FullFunctionSource");

            using (var wb = new XLWorkbook())
            {
                var index = wb.Worksheets.Add("Table Index");
                var indexHeaders = new[] { "Table Logical Name", "Forms Retrieved", "Event Registrations", "Enabled Handlers", "Disabled Handlers", "Library Mismatches", "View Events" };
                StyleHeader(index, indexHeaders);

                int row = 2;
                int totForms = 0, totEv = 0, totEn = 0, totDis = 0, totMis = 0;
                foreach (var t in tables)
                {
                    var regs = AuditEngine.SortForReport(data.Registrations.Where(r => string.Equals(r.TableLogicalName, t, StringComparison.OrdinalIgnoreCase)));
                    int forms = data.Forms.Count(f => string.Equals(f.TableLogicalName, t, StringComparison.OrdinalIgnoreCase));
                    int en = regs.Count(r => r.Enabled), dis = regs.Count - en, mis = regs.Count(r => r.IsLibraryMismatch);

                    index.Cell(row, 1).SetValue(t);
                    index.Cell(row, 2).SetValue(forms);
                    index.Cell(row, 3).SetValue(regs.Count);
                    index.Cell(row, 4).SetValue(en);
                    index.Cell(row, 5).SetValue(dis);
                    index.Cell(row, 6).SetValue(mis);

                    // Same colours as the table sheets: red if any function was not found in its referenced library, otherwise orange if
                    // some were found only in another library of the form. Only this cell is coloured.
                    int notFound = regs.Count(r => r.IsLibraryMismatch && r.VerificationResult == VerificationResults.NotFound);
                    if (notFound > 0) index.Cell(row, 6).Style.Fill.BackgroundColor = NotFoundFill;
                    else if (mis > 0) index.Cell(row, 6).Style.Fill.BackgroundColor = OtherLibraryFill;

                    var link = index.Cell(row, 7);
                    if (regs.Count > 0)
                    {
                        link.SetValue("View Events");
                        link.SetHyperlink(new XLHyperlink("'" + sheetNames[t] + "'!A1"));
                        link.Style.Font.FontColor = Header;
                        link.Style.Font.Underline = XLFontUnderlineValues.Single;
                    }
                    else
                    {
                        link.SetValue("No registrations");
                        link.Style.Font.FontColor = XLColor.FromHtml("#6E6E6E");
                        link.Style.Font.Italic = true;
                    }

                    totForms += forms; totEv += regs.Count; totEn += en; totDis += dis; totMis += mis;
                    if (regs.Count > 0) WriteTableSheet(wb, sheetNames[t], t, regs, sidecar, result);
                    row++;
                }

                int lastData = row - 1;
                index.Range(2, 2, Math.Max(2, lastData), 6).Style.NumberFormat.Format = "#,##0";
                index.Range(2, 2, Math.Max(2, lastData), 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                if (lastData >= 1) index.Range(1, 1, Math.Max(1, lastData), 7).SetAutoFilter();

                // Totals sit below a blank row, outside the filter range, so sorting/filtering the index stays clean.
                int tot = lastData + 2;
                index.Cell(tot, 1).SetValue("Total");
                index.Cell(tot, 2).SetValue(totForms); index.Cell(tot, 3).SetValue(totEv); index.Cell(tot, 4).SetValue(totEn);
                index.Cell(tot, 5).SetValue(totDis); index.Cell(tot, 6).SetValue(totMis);
                index.Range(tot, 1, tot, 7).Style.Font.Bold = true;
                index.Range(tot, 2, tot, 6).Style.NumberFormat.Format = "#,##0";
                index.Range(tot, 1, tot, 7).Style.Border.TopBorder = XLBorderStyleValues.Thin;

                index.Column(1).Width = 36; index.Columns(2, 6).Width = 20; index.Column(7).Width = 18;
                index.SheetView.FreezeRows(1);
                index.Position = 1;
                wb.SaveAs(path);
            }
            if (result.TruncatedSourceFiles.Count > 0) result.SidecarFolder = sidecar;
            return result;
        }

        private void WriteTableSheet(XLWorkbook wb, string sheetName, string table, List<HandlerRegistration> sortedRegs, string sidecar, ExportResult result)
        {
            var ws = wb.Worksheets.Add(sheetName);

            // Row 1: link back to the Table Index. It sits above the header row and is frozen with it, so it is always visible.
            var back = ws.Cell(1, 1);
            back.SetValue("\u2190 Back to Table Index");
            back.SetHyperlink(new XLHyperlink("'Table Index'!A1"));
            back.Style.Font.FontColor = Header;
            back.Style.Font.Underline = XLFontUnderlineValues.Single;

            StyleHeader(ws, SheetHeaders, 2);
            int row = 3;
            foreach (var r in sortedRegs)
            {
                ws.Cell(row, 1).SetValue(Clean(r.TableLogicalName));
                ws.Cell(row, 2).SetValue(Clean(r.FormName));
                ws.Cell(row, 3).SetValue(r.FormId.ToString());
                ws.Cell(row, 4).SetValue(Clean(r.FormType));
                ws.Cell(row, 5).SetValue(Clean(r.ControlName));
                ws.Cell(row, 6).SetValue(Clean(r.EventName));
                ws.Cell(row, 7).SetValue(Clean(r.HandlerId));
                ws.Cell(row, 8).SetValue(Clean(r.FunctionName));
                ws.Cell(row, 9).SetValue(r.Enabled ? "Yes" : "No");
                ws.Cell(row, 10).SetValue(r.IsPcf ? "N/A" : (r.PassExecutionContext ? "Yes" : "No"));

                string code = Clean(r.FunctionCode);
                if (code.Length > CodeLimit)
                {
                    Directory.CreateDirectory(sidecar);
                    string file = SanitizeFileName(sheetName + "_" + r.OriginalOrder) + ".txt";
                    File.WriteAllText(Path.Combine(sidecar, file), r.FunctionCode, new UTF8Encoding(true));
                    result.TruncatedSourceFiles.Add(file);
                    code = code.Substring(0, CodeLimit) + Environment.NewLine + "[TRUNCATED: exceeds Excel's 32,767 character cell limit. Full source: " + Path.GetFileName(sidecar) + "\\" + file + "]";
                }
                ws.Cell(row, 11).SetValue(code);
                if (r.FunctionLine.HasValue) ws.Cell(row, 12).SetValue(r.FunctionLine.Value);
                ws.Cell(row, 13).SetValue(Clean(r.LibraryName));
                ws.Cell(row, 14).SetValue(Clean(r.VerificationResult));
                ws.Cell(row, 15).SetValue(Clean(r.RecommendedAction));
                if (r.IsLibraryMismatch)
                    ws.Range(row, 1, row, SheetHeaders.Length).Style.Fill.BackgroundColor = r.VerificationResult == VerificationResults.NotFound ? NotFoundFill : OtherLibraryFill;
                row++;
            }
            result.Rows += sortedRegs.Count;

            int last = Math.Max(2, row - 1);
            ws.Range(2, 1, last, SheetHeaders.Length).SetAutoFilter();
            var body = ws.Range(2, 1, last, SheetHeaders.Length);
            body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Column(11).Style.Alignment.WrapText = true;
            ws.Column(15).Style.Alignment.WrapText = true;
            ws.Column(14).Style.Alignment.WrapText = true;
            ws.Column(12).Style.NumberFormat.Format = "0";
            int[] widths = { 28, 28, 38, 14, 22, 14, 38, 30, 10, 14, 80, 12, 36, 34, 60 };
            for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
            ws.Row(2).Style.Alignment.WrapText = true;
            ws.SheetView.FreezeRows(2);
        }

        private static void StyleHeader(IXLWorksheet ws, string[] headers, int headerRow = 1)
        {
            for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).SetValue(headers[i]);
            var h = ws.Range(headerRow, 1, headerRow, headers.Length);
            h.Style.Fill.BackgroundColor = Header;
            h.Style.Font.FontColor = XLColor.White;
            h.Style.Font.Bold = true;
            h.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>Removes characters that are illegal in XML/Excel (control chars); everything else is preserved as-is.</summary>
        private static string Clean(string s)
        {
            return s == null ? string.Empty : Regex.Replace(s, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", "?");
        }
    }
}
