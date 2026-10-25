using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using WinFormsLabel = System.Windows.Forms.Label;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor
{
    /// <summary>
    /// Main plugin UI: a single screen with a split view (about 25% table list on the left, 75% results on the right).
    /// Built in code (no designer file) to keep the repository easy to diff and review.
    /// </summary>
    public class MainControl : PluginControlBase
    {
        private static readonly Color TextColor = Color.FromArgb(32, 32, 32);
        private static readonly Color MutedColor = Color.FromArgb(110, 110, 110);
        private static readonly Color ErrorColor = Color.Firebrick;

        /// <summary>What the registrations currently held in 'data' belong to.</summary>
        private enum ResultSource { None, Selection, SingleTable }

        private EnvironmentData data;
        private ResultSource resultSource = ResultSource.None;
        private CancellationTokenSource cts;
        private bool busy;
        private readonly QualityOptions qualityOptions = new QualityOptions();
        private List<TableRow> allRows = new List<TableRow>();

        // Step 1 result (table metadata only) and the per-session cache used by registration scans.
        private List<TableSummary> availableTables = new List<TableSummary>();
        private ScanCache cache = new ScanCache();
        // Tables whose registrations are loaded in 'data' (successful part of the latest scan).
        private readonly HashSet<string> scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Tables of the latest scan that could not be loaded.
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string loadedEnvironment;

        private double splitRatio = 0.25;
        private bool adjustingSplit;

        private SplitContainer split;
        private CheckBox chkSubgrid, chkPcf;
        private Button btnLoad, btnScan, btnCancel, btnExport, btnAnalysisCsv, btnSelectAll, btnClear;

        // Explicitly use the Windows Forms Label type to avoid ambiguity with Microsoft.Xrm.Sdk.Label.
        private WinFormsLabel lblEnv, lblSelected, lblLeftMsg, lblRightMsg;

        private TextBox txtSearch;
        private ProgressBar bar;
        private DataGridView gridTables, gridRegs;
        private DataGridViewButtonColumn colView;
        private DataGridViewTextBoxColumn colTable;

        public MainControl()
        {
            BuildUi();
            UpdateEnvLabel();
            RefreshIdleMessages();
            UpdateSelectionLabel();
            UpdateButtons();
        }

        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            Dock = DockStyle.Fill;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            ForeColor = TextColor;

            split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical, // vertical splitter bar: left | right
                FixedPanel = FixedPanel.None,
                SplitterWidth = 5,
                BackColor = Color.FromArgb(218, 218, 218)
            };

            split.Panel1.BackColor = Color.White;
            split.Panel2.BackColor = Color.White;
            split.SizeChanged += (s, e) => ApplySplitRatio();
            split.SplitterMoved += (s, e) =>
            {
                if (!adjustingSplit && split.Width > 0)
                    splitRatio = (double)split.SplitterDistance / split.Width;
            };

            BuildLeftPanel(split.Panel1);
            BuildRightPanel(split.Panel2);

            Controls.Add(split);
        }

        /// <summary>Keeps the left:right ratio (default 25:75) while the window is resized, within sensible minimum widths.</summary>
        private void ApplySplitRatio()
        {
            const int leftMin = 240, rightMin = 420;
            int w = split.Width;

            if (w < leftMin + rightMin + split.SplitterWidth + 10)
                return; // too narrow to apply the minimum widths

            adjustingSplit = true;

            try
            {
                split.Panel1MinSize = leftMin;
                split.Panel2MinSize = rightMin;

                int d = (int)(w * splitRatio);
                d = Math.Max(leftMin, Math.Min(w - rightMin - split.SplitterWidth, d));

                split.SplitterDistance = d;
            }
            finally
            {
                adjustingSplit = false;
            }
        }

        private void BuildLeftPanel(Control host)
        {
            var layout = NewStack();

            lblEnv = new WinFormsLabel { ForeColor = MutedColor };
            WrapLabel(lblEnv, layout);

            btnLoad = Btn("Load Environment Data", true, (s, e) => LoadData());
            btnLoad.Dock = DockStyle.Fill;
            btnLoad.AutoSize = false;
            btnLoad.Height = 32;

            var searchLabel = new WinFormsLabel { Text = "Search tables:", AutoSize = true, Margin = new Padding(0, 8, 0, 2) };

            txtSearch = new TextBox { Dock = DockStyle.Fill };
            txtSearch.TextChanged += (s, e) => ApplyFilter();

            var selectButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            selectButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            selectButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            btnSelectAll = Btn("Select All", false, (s, e) => SetAll(true));
            btnClear = Btn("Clear Selection", false, (s, e) => SetAll(false));

            foreach (var b in new[] { btnSelectAll, btnClear })
            {
                b.Dock = DockStyle.Fill;
                b.AutoSize = false;
                b.Height = 28;
                b.Margin = new Padding(0, 0, 3, 0);
            }

            selectButtons.Controls.Add(btnSelectAll, 0, 0);
            selectButtons.Controls.Add(btnClear, 1, 0);

            var tip = new ToolTip();
            tip.SetToolTip(btnSelectAll, "Selects every table in the list, including tables hidden by the current search.");
            tip.SetToolTip(btnClear, "Clears the selection of every table, including tables hidden by the current search.");

            lblSelected = new WinFormsLabel { Margin = new Padding(0, 6, 0, 0) };
            WrapLabel(lblSelected, layout);

            lblLeftMsg = new WinFormsLabel { Margin = new Padding(0, 4, 0, 4) };
            WrapLabel(lblLeftMsg, layout);

            gridTables = BuildTableGrid();

            AddRow(layout, lblEnv);
            AddRow(layout, btnLoad);
            AddRow(layout, searchLabel);
            AddRow(layout, txtSearch);
            AddRow(layout, selectButtons);
            AddRow(layout, lblSelected);
            AddRow(layout, lblLeftMsg);
            AddRow(layout, gridTables, true);

            host.Controls.Add(layout);
        }

        private DataGridView BuildTableGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                RowHeadersVisible = false,
                ColumnHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Color.FromArgb(225, 225, 225),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = true
            };

            g.RowTemplate.Height = 44;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 240, 250);
            g.DefaultCellStyle.SelectionForeColor = TextColor;

            var colSelect = new DataGridViewCheckBoxColumn
            {
                Name = "colSelect",
                DataPropertyName = "Selected",
                Width = 32,
                Resizable = DataGridViewTriState.False
            };

            colTable = new DataGridViewTextBoxColumn
            {
                Name = "colTable",
                DataPropertyName = "LogicalName",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 80
            };

            colView = new DataGridViewButtonColumn
            {
                Name = "colView",
                Text = "View",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.System,
                Width = 58,
                Resizable = DataGridViewTriState.False
            };

            g.Columns.AddRange(colSelect, colTable, colView);

            g.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell)
                    g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            g.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == colSelect.Index)
                    OnSelectionChanged();
            };

            g.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != colView.Index)
                    return;

                var row = g.Rows[e.RowIndex].DataBoundItem as TableRow;

                if (row != null)
                    ViewTable(row.LogicalName);
            };

            g.CellPainting += GridTables_CellPainting;

            g.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0)
                    return;

                var row = g.Rows[e.RowIndex].DataBoundItem as TableRow;

                if (row == null)
                    return;

                if (e.ColumnIndex == colView.Index)
                    e.ToolTipText = "View the event registrations of " + row.LogicalName + " in the results panel.";
                else if (e.ColumnIndex == colTable.Index)
                    e.ToolTipText = TableTitle(row) + "\n" + RowMessage(row);
            };

            return g;
        }

        private void BuildRightPanel(Control host)
        {
            var layout = NewStack();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };

            btnScan = Btn("Load Registrations", true, (s, e) => LoadRegistrations());
            btnExport = Btn("Export Excel", false, (s, e) => ExportExcel());
            btnAnalysisCsv = Btn("Export Analysis (CSV)", false, (s, e) => ExportAnalysisCsv());
            btnCancel = Btn("Cancel", false, (s, e) =>
            {
                if (cts != null)
                    cts.Cancel();
            });

            // Optional content of a scan; both are off by default. Changing them invalidates loaded results.
            chkSubgrid = new CheckBox { Text = "Include subgrid events", AutoSize = true, Checked = false, Margin = new Padding(10, 7, 0, 0) };
            chkPcf = new CheckBox { Text = "Include PCF controls", AutoSize = true, Checked = false, Margin = new Padding(10, 7, 0, 0) };
            chkSubgrid.CheckedChanged += (s, e) => OnScanOptionsChanged();
            chkPcf.CheckedChanged += (s, e) => OnScanOptionsChanged();

            var optionTip = new ToolTip();
            optionTip.SetToolTip(chkSubgrid, "Also list event handlers registered on controls such as subgrids.");
            optionTip.SetToolTip(chkPcf, "Also list custom (PCF) controls on the forms, with the libraries from their manifest.");

            buttons.Controls.AddRange(new Control[] { btnScan, chkSubgrid, chkPcf, btnExport, btnAnalysisCsv, btnCancel });

            bar = new ProgressBar { Dock = DockStyle.Fill, Height = 8, Minimum = 0, Maximum = 100, Margin = new Padding(0, 6, 0, 6) };

            lblRightMsg = new WinFormsLabel { Margin = new Padding(0, 0, 0, 6) };
            WrapLabel(lblRightMsg, layout);

            gridRegs = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Color.FromArgb(225, 225, 225),
                EnableHeadersVisualStyles = true
            };

            gridRegs.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0)
                    return;

                var row = gridRegs.Rows[e.RowIndex].DataBoundItem as RegistrationRow;

                if (row == null)
                    return;

                if (gridRegs.Columns[e.ColumnIndex].HeaderText == "Function Code")
                {
                    string code = row.FullFunctionCode ?? string.Empty;
                    e.ToolTipText = code.Length > 3000 ? code.Substring(0, 3000) + "\n..." : code;
                }
                else
                {
                    string v = gridRegs[e.ColumnIndex, e.RowIndex].FormattedValue as string;

                    if (v != null && v.Length > 40)
                        e.ToolTipText = v;
                }
            };

            AddRow(layout, buttons);
            AddRow(layout, bar);
            AddRow(layout, lblRightMsg);
            AddRow(layout, gridRegs, true);

            host.Controls.Add(layout);
        }

        private static TableLayoutPanel NewStack()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(8),
                BackColor = Color.White
            };

            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            return t;
        }

        private static void AddRow(TableLayoutPanel t, Control c, bool fill = false)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
        }

        /// <summary>Makes a label wrap to the width of its panel so long messages never overlap other controls.</summary>
        private static void WrapLabel(WinFormsLabel label, TableLayoutPanel host)
        {
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;

            EventHandler fit = (s, e) =>
                label.MaximumSize = new Size(Math.Max(60, host.ClientSize.Width - host.Padding.Horizontal - 4), 0);

            host.SizeChanged += fit;
            fit(null, null);
        }

        private Button Btn(string text, bool primary, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(8, 2, 8, 2),
                FlatStyle = FlatStyle.System
            };

            if (primary)
                b.Font = new Font(b.Font, FontStyle.Bold);

            b.Click += click;

            return b;
        }

        // ---- per-row message ("Click View ...") ----

        private static string TableTitle(TableRow r)
        {
            return string.IsNullOrEmpty(r.DisplayName) ? r.LogicalName : r.DisplayName + " (" + r.LogicalName + ")";
        }

        private string RowMessage(TableRow r)
        {
            return TableSelection.RowMessage(r.Events, r.Mismatches, failed.Contains(r.LogicalName));
        }

        private void GridTables_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != colTable.Index)
                return;

            var row = gridTables.Rows[e.RowIndex].DataBoundItem as TableRow;

            if (row == null)
                return;

            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);

            var b = e.CellBounds;
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

            Color msgColor = failed.Contains(row.LogicalName) ? ErrorColor : (row.Events == null ? MutedColor : TextColor);

            using (var titleFont = new Font(gridTables.Font, FontStyle.Bold))
            using (var msgFont = new Font(gridTables.Font.FontFamily, 8F, FontStyle.Italic))
            {
                TextRenderer.DrawText(e.Graphics, TableTitle(row), titleFont, new Rectangle(b.X + 4, b.Y + 3, b.Width - 8, 19), TextColor, flags);
                TextRenderer.DrawText(e.Graphics, RowMessage(row), msgFont, new Rectangle(b.X + 4, b.Y + 22, b.Width - 8, 18), msgColor, flags);
            }

            e.Handled = true;
        }

        // ---- messages ----

        private void SetLeft(string message, bool error = false)
        {
            lblLeftMsg.Text = message;
            lblLeftMsg.ForeColor = error ? ErrorColor : MutedColor;
        }

        private void SetRight(string message, bool error = false)
        {
            lblRightMsg.Text = message;
            lblRightMsg.ForeColor = error ? ErrorColor : MutedColor;
        }

        /// <summary>Instruction messages that depend on the current state (nothing loaded / tables loaded / selection made).</summary>
        private void RefreshIdleMessages()
        {
            if (loadedEnvironment == null)
            {
                SetLeft("Connect to an environment with XrmToolBox, then click 'Load Environment Data' to list its tables.");
                SetRight("Load the tables first (left panel). Then select one or more tables and click 'Load Registrations', or click 'View' on a table row.");
                return;
            }

            if (resultSource != ResultSource.None)
                return; // results are shown; their message is set by the scan

            int n = allRows.Count(r => r.Selected);

            SetRight(n == 0
                ? "Select one or more tables and click 'Load Registrations', or click 'View' on a table row to see its registrations here."
                : "Click 'Load Registrations' to load the registrations of the " + n + " selected table(s).");
        }

        // ------------------------------------------------------------------ connection

        public override void UpdateConnection(
            IOrganizationService newService,
            ConnectionDetail detail,
            string actionName,
            object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);

            if (loadedEnvironment != null &&
                !string.Equals(
                    loadedEnvironment,
                    EnvironmentName(),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Everything belongs to the previous connection; never present it as coming from the new one.
                ClearResults();
                allRows = new List<TableRow>();
                availableTables = new List<TableSummary>();
                cache = new ScanCache();
                loadedEnvironment = null;

                gridTables.DataSource = null;

                UpdateSelectionLabel();
                RefreshIdleMessages();
                SetLeft("The connection changed. Click 'Load Environment Data' to list the tables of the new environment.");
            }

            UpdateEnvLabel();
            UpdateButtons();
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            if (cts != null)
                cts.Cancel();

            base.ClosingPlugin(info);
        }

        private string EnvironmentName()
        {
            var d = ConnectionDetail;

            if (d == null)
                return null;

            if (!string.IsNullOrWhiteSpace(d.OrganizationFriendlyName))
                return d.OrganizationFriendlyName;

            if (!string.IsNullOrWhiteSpace(d.Organization))
                return d.Organization;

            return d.ConnectionName;
        }

        private void UpdateEnvLabel()
        {
            string n = EnvironmentName();

            lblEnv.Text = Service == null || n == null
                ? "Not connected."
                : "Environment: " + n +
                  (string.IsNullOrEmpty(ConnectionDetail.WebApplicationUrl)
                      ? ""
                      : "\n" + ConnectionDetail.WebApplicationUrl);
        }

        // ------------------------------------------------------------------ load tables

        /// <summary>Loads the table list only (metadata). Does not retrieve forms, web resources or registrations.</summary>
        private async void LoadData()
        {
            if (busy || cts != null)
                return; // an operation is already running

            if (Service == null)
            {
                MessageBox.Show(
                    this,
                    "Connect to a Dataverse environment first (XrmToolBox > Connect).",
                    "Not connected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            cts = new CancellationTokenSource();

            var token = cts.Token;
            var svc = Service;
            string env = EnvironmentName();
            var timer = System.Diagnostics.Stopwatch.StartNew();

            SetBusy(true);
            SetLeft("Loading tables...");

            var progress = new Progress<ProgressInfo>(p =>
            {
                bar.Value = Math.Max(0, Math.Min(100, p.Percent));
                SetLeft(p.Message);
            });

            try
            {
                var tables = await Task.Run(
                    () => new DataverseRetriever().LoadTables(svc, progress, token),
                    token);

                // A new table list starts a new session: earlier results and caches no longer apply.
                ClearResults();
                availableTables = tables;
                cache = new ScanCache();
                loadedEnvironment = env;

                BindTables();

                SetLeft(availableTables.Count == 0
                    ? "No eligible tables were found in this environment."
                    : "Loaded " + availableTables.Count + " eligible tables in " +
                      (timer.ElapsedMilliseconds / 1000.0).ToString("0.0") +
                      "s. Tick the tables to audit, or click View on a row.");
            }
            catch (OperationCanceledException)
            {
                SetLeft("Cancelled. The previous table list and results were not changed.");
            }
            catch (Exception ex)
            {
                SetLeft("Loading tables failed: " + ex.Message, true);
                LogFailure("Loading tables failed: " + ex);

                MessageBox.Show(
                    this,
                    "Loading the table list failed:\n\n" + ex.Message +
                    "\n\nCheck your connection and that your user can read table metadata.",
                    "Load failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);

                cts.Dispose();
                cts = null;
            }
        }

        // ------------------------------------------------------------------ load registrations

        /// <summary>'Load Registrations' button: loads the registrations of the selected tables only.</summary>
        private void LoadRegistrations()
        {
            if (busy || cts != null)
                return;

            var sel = SelectedTables();

            if (sel.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Select at least one table before loading registrations.",
                    "No tables selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            RunScan(sel, ResultSource.Selection);
        }

        /// <summary>'View' button on a table row: loads and shows the registrations of that one table.</summary>
        private void ViewTable(string logicalName)
        {
            if (busy || cts != null)
                return;

            RunScan(new List<string> { logicalName }, ResultSource.SingleTable);
        }

        private async void RunScan(List<string> sel, ResultSource source)
        {
            if (busy || cts != null)
                return;

            if (Service == null || availableTables.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Click 'Load Environment Data' first to load the table list.",
                    "No tables loaded",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            // Results of an earlier operation must never stay visible (or exportable) while a new one runs.
            ClearResults();

            cts = new CancellationTokenSource();

            var token = cts.Token;
            var svc = Service;
            string env = EnvironmentName();
            var tables = availableTables;
            var sessionCache = cache;
            var options = new QualityOptions
            {
                DebuggerStatements = qualityOptions.DebuggerStatements,
                LooseEquality = qualityOptions.LooseEquality,
                DeprecatedApis = qualityOptions.DeprecatedApis
            };

            var scanOptions = new ScanOptions
            {
                IncludeSubgridEvents = chkSubgrid.Checked,
                IncludePcfControls = chkPcf.Checked
            };

            SetBusy(true);
            SetRight("Loading registrations for " + (sel.Count == 1 ? sel[0] : sel.Count + " tables") + "...");

            var progress = new Progress<ProgressInfo>(p =>
            {
                bar.Value = Math.Max(0, Math.Min(100, p.Percent));
                SetRight(p.Message);
            });

            try
            {
                var result = await Task.Run(
                    () => new DataverseRetriever().LoadRegistrations(
                        svc, env, tables, sel, sessionCache, options, progress, token, scanOptions),
                    token);

                data = result.Data;
                resultSource = source;

                scanned.Clear();
                failed.Clear();

                foreach (var t in result.ScannedTables)
                    scanned.Add(t);

                foreach (var t in result.FailedTables)
                    failed.Add(t);

                BindScan();

                if (data.Registrations.Count == 0)
                {
                    SetRight(
                        "No JavaScript event registrations were found for " +
                        (scanned.Count == 1 ? "table '" + scanned.First() + "'" : scanned.Count + " table(s)") +
                        " (" + result.TimingSummary + ").");
                }
                else
                {
                    SetRight(
                        "Showing " + data.Registrations.Count + " registrations from " + data.Forms.Count +
                        " form(s) in " + (scanned.Count == 1 ? "table '" + scanned.First() + "'" : scanned.Count + " tables") +
                        " (" + result.TimingSummary + ").");
                }

                if (result.FailedTables.Count > 0)
                    SetRight(lblRightMsg.Text + " " + result.FailedTables.Count + " table(s) could not be loaded.", true);

                if (result.FailedTables.Count > 0 || data.Diagnostics.Count > 0)
                    ShowScanProblems(result);
            }
            catch (OperationCanceledException)
            {
                SetRight("Cancelled. No registrations are loaded; click 'Load Registrations' to try again.");
            }
            catch (Exception ex)
            {
                SetRight("Loading registrations failed: " + ex.Message, true);
                LogFailure("Loading registrations failed: " + ex);

                MessageBox.Show(
                    this,
                    "Loading registrations failed:\n\n" + ex.Message +
                    "\n\nCheck that your user can read forms (systemform) and web resources.",
                    "Load failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);

                cts.Dispose();
                cts = null;
            }
        }

        private void ShowScanProblems(ScanResult result)
        {
            var lines = new List<string>();

            if (result.FailedTables.Count > 0)
                lines.Add("Tables NOT loaded (not included in preview/export): " + string.Join(", ", result.FailedTables));

            lines.AddRange(data.Diagnostics);

            const int max = 12;
            string text = string.Join("\n", lines.Take(max).Select(l => "- " + l));

            if (lines.Count > max)
                text += "\n... and " + (lines.Count - max) + " more.";

            MessageBox.Show(
                this,
                "Registrations were loaded, but some items had problems:\n\n" + text,
                "Completed with warnings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void BindTables()
        {
            allRows = availableTables.Select(t => new TableRow
            {
                LogicalName = t.LogicalName,
                DisplayName = t.DisplayName
            }).ToList();

            ApplyFilter();
            RefreshIdleMessages();
            UpdateButtons();
        }

        private void BindScan()
        {
            var summaries = data.Tables.ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);

            foreach (var r in allRows)
            {
                TableSummary t;

                if (!scanned.Contains(r.LogicalName))
                {
                    // Not part of the current results: counts are unknown, not zero.
                    r.Forms = r.Events = r.Enabled = r.Disabled = r.Mismatches = null;
                }
                else if (summaries.TryGetValue(r.LogicalName, out t))
                {
                    r.Forms = t.Forms;
                    r.Events = t.Events;
                    r.Enabled = t.Enabled;
                    r.Disabled = t.Disabled;
                    r.Mismatches = t.Mismatches;
                }
                else
                {
                    // Scanned successfully but the table has no forms.
                    r.Forms = r.Events = r.Enabled = r.Disabled = r.Mismatches = 0;
                }
            }

            gridTables.Invalidate();
            RefreshPreview();
            UpdateButtons();
        }

        /// <summary>Drops all loaded registrations (preview, export state, per-row counts). The table list and cache are kept.</summary>
        private void ClearResults()
        {
            data = null;
            resultSource = ResultSource.None;
            scanned.Clear();
            failed.Clear();

            foreach (var r in allRows)
                r.Forms = r.Events = r.Enabled = r.Disabled = r.Mismatches = null;

            if (gridRegs != null)
                gridRegs.DataSource = null;

            if (gridTables != null)
                gridTables.Invalidate();

            if (btnExport != null)
                UpdateButtons();
        }

        // ------------------------------------------------------------------ selection

        private void ApplyFilter()
        {
            // Filtering only changes which rows are visible; each row keeps its own Selected flag.
            var view = TableSelection.Filter(allRows, txtSearch.Text);

            gridTables.DataSource = new BindingList<TableRow>(view);

            UpdateSelectionLabel();
            UpdateButtons();
        }

        /// <summary>Select All / Clear Selection act on every table in the list, including rows hidden by the search filter.</summary>
        private void SetAll(bool value)
        {
            if (busy)
                return;

            foreach (var r in allRows)
                r.Selected = value;

            ApplyFilter();
            OnSelectionChanged();
        }

        /// <summary>The scan options decide what is loaded, so results loaded with other options are cleared.</summary>
        private void OnScanOptionsChanged()
        {
            if (resultSource == ResultSource.None)
                return;

            ClearResults();
            SetRight("The options changed, so the previous results were cleared. Click 'Load Registrations' (or 'View') to load them again.");
            UpdateButtons();
        }

        /// <summary>Called when the user changes the selection: results of a selection-based scan no longer apply and are cleared.</summary>
        private void OnSelectionChanged()
        {
            UpdateSelectionLabel();

            if (resultSource == ResultSource.Selection)
            {
                ClearResults();
                SetRight("The selection changed, so the previous results were cleared. Click 'Load Registrations' to load registrations for the new selection.");
            }
            else
            {
                RefreshIdleMessages();
            }

            UpdateButtons();
        }

        private void UpdateSelectionLabel()
        {
            lblSelected.Text = allRows.Count(r => r.Selected) + " of " + allRows.Count + " tables selected";
        }

        private List<string> SelectedTables()
        {
            return allRows
                .Where(r => r.Selected)
                .Select(r => r.LogicalName)
                .ToList();
        }

        private void RefreshPreview()
        {
            if (data == null)
            {
                gridRegs.DataSource = null;
                return;
            }

            // 'data' only ever contains the tables of the latest successful scan.
            var rows = AuditEngine.SortForReport(data.Registrations)
                .Select(r => new RegistrationRow
                {
                    TableLogicalName = r.TableLogicalName,
                    FormName = r.FormName,
                    FormId = r.FormId.ToString(),
                    FormType = r.FormType,
                    ControlName = r.ControlName,
                    EventName = r.EventName,
                    HandlerId = r.HandlerId,
                    FunctionName = r.FunctionName,
                    Enabled = r.Enabled ? "Yes" : "No",
                    PassExecutionContext = r.IsPcf ? "N/A" : (r.PassExecutionContext ? "Yes" : "No"),
                    FunctionCode = OneLine(r.FunctionCode),
                    FunctionLineNumber = r.FunctionLine,
                    RegisteredLibrary = r.LibraryName,
                    VerificationResult = r.VerificationResult,
                    RecommendedAction = r.RecommendedAction,
                    FullFunctionCode = r.FunctionCode
                }).ToList();

            gridRegs.DataSource = new BindingList<RegistrationRow>(rows);
            gridRegs.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);

            // Keep very long columns (function code, recommended action) readable; the full text is in the cell tooltip.
            foreach (DataGridViewColumn c in gridRegs.Columns)
                if (c.Width > 420)
                    c.Width = 420;
        }

        private static string OneLine(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }

        // ------------------------------------------------------------------ export

        /// <summary>Exports the tables of the latest successful scan (exactly what the preview shows).</summary>
        private async void ExportExcel()
        {
            if (busy || data == null || scanned.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Load registrations first ('Load Registrations' or 'View').",
                    "Nothing to export",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            var exportTables = scanned.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

            using (var dlg = new SaveFileDialog
            {
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = ExcelExporter.DefaultFileName(data.EnvironmentName),
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    SetRight("Export cancelled.");
                    return;
                }

                string path = dlg.FileName;

                SetBusy(true, false);
                SetRight("Exporting " + exportTables.Count + " table(s)...");

                try
                {
                    var snapshot = data;

                    var res = await Task.Run(
                        () => new ExcelExporter().Export(snapshot, exportTables, path));

                    string msg = "Exported " + res.Rows +
                                 " registrations from " + res.Tables + " table(s).";

                    if (res.TruncatedSourceFiles.Count > 0)
                    {
                        msg += "\n\n" + res.TruncatedSourceFiles.Count +
                               " function(s) exceeded Excel's cell limit and are marked TRUNCATED in the sheet; full source is in:\n" +
                               res.SidecarFolder;
                    }

                    SetRight(msg.Split('\n')[0]);

                    MessageBox.Show(
                        this,
                        msg,
                        "Export complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    LogFailure("Export failed: " + ex);
                    SetRight("Export failed: " + ex.Message, true);

                    MessageBox.Show(
                        this,
                        "Export failed:\n\n" + ex.Message +
                        "\n\n(If the file is open in Excel, close it and retry.)",
                        "Export failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }

        private void ExportAnalysisCsv()
        {
            if (busy || data == null)
                return;

            using (var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = ExcelExporter.SanitizeFileName(data.EnvironmentName) +
                           "_JavaScriptAnalysis.csv"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    string b = Path.Combine(
                        Path.GetDirectoryName(dlg.FileName),
                        Path.GetFileNameWithoutExtension(dlg.FileName));

                    CsvWriter.Write(
                        b + "_Quality.csv",
                        new[]
                        {
                            "Check", "Library", "Function", "Line",
                            "Occurrences", "Explanation", "Recommended Action", "Confidence"
                        },
                        data.Quality.Select(q => (IList<string>)new[]
                        {
                            q.Check, q.Library, q.Function, q.Line.ToString(),
                            q.Occurrences.ToString(), q.Explanation, q.Action, q.Confidence
                        }));

                    CsvWriter.Write(
                        b + "_Dependencies.csv",
                        new[] { "Function", "Library", "Relationship", "Detail", "Basis" },
                        data.Dependencies.Select(x => (IList<string>)new[]
                        {
                            x.Function, x.Library, x.Relationship, x.Detail, x.Basis
                        }));

                    SetRight("Wrote " + b + "_Quality.csv and " + b + "_Dependencies.csv");
                }
                catch (Exception ex)
                {
                    LogFailure("Analysis export failed: " + ex);
                    SetRight("Analysis export failed: " + ex.Message, true);

                    MessageBox.Show(
                        this,
                        "Analysis export failed:\n\n" + ex.Message,
                        "Export failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void LogFailure(string m)
        {
            // Full exception details go to the trace output; the user sees the message in the panel and a dialog.
            System.Diagnostics.Trace.TraceError(DateTime.Now.ToString("s") + "  " + m);
        }

        private void SetBusy(bool isBusy, bool allowCancel = true)
        {
            busy = isBusy;

            btnCancel.Enabled = isBusy && allowCancel;

            // The list (selection, search, View buttons) is locked while an operation runs.
            gridTables.Enabled = !isBusy;
            txtSearch.Enabled = !isBusy;
            chkSubgrid.Enabled = !isBusy;
            chkPcf.Enabled = !isBusy;

            if (!isBusy)
                bar.Value = 0;

            UpdateButtons();
            UseWaitCursor = false;
        }

        private void UpdateButtons()
        {
            if (btnLoad == null || btnCancel == null)
                return;

            bool tablesLoaded = availableTables.Count > 0;
            bool anySelected = allRows.Any(r => r.Selected);
            bool hasResults = data != null && scanned.Count > 0;

            btnLoad.Enabled = !busy;
            btnScan.Enabled = !busy && tablesLoaded && anySelected;
            btnSelectAll.Enabled = !busy && tablesLoaded;
            btnClear.Enabled = !busy && tablesLoaded;
            btnExport.Enabled = !busy && hasResults;
            btnAnalysisCsv.Enabled = !busy && hasResults;

            if (!busy)
                btnCancel.Enabled = false;
        }
    }
}
