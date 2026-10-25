# JavaScript Event Registration Auditor for XrmToolBox

![Icon](assets/icon-256.png)

Audit the **JavaScript event registrations on your model-driven app forms** in Microsoft Dataverse / Dynamics 365. See every handler registered on the tables you choose, check that each registered function really exists in the library it points to, and export the findings to Excel.

**Developer:** Gagan Deep Singh  
**LinkedIn:** [Gagan Deep Singh](https://www.linkedin.com/in/glowofsky/)

## What you can do

- Work in the **active XrmToolBox connection**; there is no separate sign-in.
- **Pick the tables to audit.** Search by logical or display name, tick individual tables or use **Select All** / **Clear Selection**, and see a live "n of m tables selected" count. Searching never changes your selection.
- **View a single table quickly.** Each table row has a **View** button that shows that table's registrations on the right, without changing your selection. After loading, the row shows a short summary (registrations and mismatches).
- **Load registrations for the selected tables only.** Only the forms of those tables, and the JavaScript libraries registered on those forms, are read.
- **Optionally include more** (both unticked by default, next to **Load Registrations**):
  - **Include subgrid events** – handlers registered on controls such as subgrids.
  - **Include PCF controls** – custom (PCF) controls on the forms, listed with the libraries from their manifest.
- **Review everything on screen.** The results panel shows the same 15 columns as the Excel report, sorted by Event Name (A–Z). Hover over a cell to read long text or the full function source.
- **Find library problems** – functions that are missing from, or registered against the wrong, library – with a recommended action for each row.
- **Export to Excel**, and export **JavaScript quality and dependency findings** to CSV.
- **Stay in control.** Cancel a running load at any time. Changing the table selection or either option clears the results and disables export, so you never export results that do not match what is selected.

## How to use

1. Connect to an environment in XrmToolBox.
2. Click **Load Environment Data**. The tables that can have form registrations are listed on the left.
3. Tick the tables you want to audit (or click **Select All**). Tick **Include subgrid events** and/or **Include PCF controls** if you need them.
4. Click **Load Registrations**, or click **View** next to a single table.
5. Review the results, then click **Export Excel**. The suggested file name is `<EnvironmentName>_JavaScriptEventsRegistrations.xlsx`.

Many-to-many (intersect) tables, internal tables and tables that cannot have forms are not listed.

## Function verification

For every handler, the tool looks for the function first in the library attached to the event, then in the other libraries of the same form.

| Result | Meaning | Library mismatch |
|---|---|---|
| Found in registered library | The function was found in the library the handler points to. | No |
| Found in another library on the same form — library mismatch | Not in the registered library, but found in another library of the form. | Yes (light orange) |
| Function not found in referenced library | Not found in the registered library or the other libraries of the form. | Yes (light red) |
| Registered library not retrieved | The web resource was not returned, or was empty or unreadable. | No |
| Ambiguous function match | More than one matching definition was found. | Only if not found in the registered library |
| Function extraction incomplete | The function start was found but not its end. | No |
| Function name missing | The handler has no function name. | No |
| PCF control (not verified) | A PCF control (when **Include PCF controls** is ticked). | No |

Disabled handlers are shown independently of the result. Verification works by scanning the library text, so treat a "not found" result as something to review rather than proof that the function is missing.

## Excel report

- **Table Index** (first sheet) lists every table you exported: Table Logical Name, Forms Retrieved, Event Registrations, Enabled Handlers, Disabled Handlers, Library Mismatches and a **View Events** link to the table's sheet. The *Library Mismatches* cell is light red if the table has a function that was not found, otherwise light orange if some functions were found in another library. A total row sits below the table.
- **One sheet per table that has registrations.** Tables with none stay in the index ("No registrations") but get no sheet.
- Every table sheet starts with a **← Back to Table Index** link. Headers are on row 2, both rows stay visible while you scroll, and the headers have filters.
- **Columns:** Table Logical Name, Form Name, Form ID, Form Type, Control Name, Event Name, Handler ID, Function Name, Enabled, Pass Execution Context, Function Code, Function Line Number, Registered Library, Function Verification Result, Recommended Action.
- Rows are sorted by Event Name (A–Z), then Form Name, Control Name and original handler order. Mismatch rows are light red or light orange.
- Very long function source (over about 32,000 characters) is marked as truncated in the cell, and the full source is saved in a `_FullFunctionSource` folder next to the workbook.
- **PCF rows** (when included) show `PCF Control` as the Event Name, the control name as the Function Name, and the code files and platform libraries from the control's manifest as the Registered Library. Pass Execution Context shows `N/A`. They count toward the registration totals but never as mismatches. If the control definition cannot be read, the library shows `(control definition not retrieved)`. Built-in Microsoft controls (`MscrmControls.*`) are not listed.

## Export Analysis (CSV)

Saves two files beside the name you choose: `…_Quality.csv` and `…_Dependencies.csv`.

- **Quality checks:** `debugger` statements, `==` / `!=` comparisons, `Xrm.Page`, `GetGlobalContext`, `getServerUrl`, and functions that could not be fully read.
- **Dependencies:** functions used by form registrations, function names defined in more than one library, functions not registered on the scanned forms (they may still be used by ribbon commands or other scripts), and possible calls to functions in other libraries.

## Permissions

Your Dataverse user needs read access to table metadata, forms and web resources, plus custom controls if you use **Include PCF controls**.

## Troubleshooting

| Problem | What to check |
|---|---|
| The table list or registrations do not load | The message shown, your connection and your read permissions. |
| "Registered library not retrieved" | The web resource exists, is published and has readable content. |
| "Function not found in referenced library" | The function name and namespace in the handler, and the note about scanning below. |
| PCF libraries show "(control definition not retrieved)" | Read access to custom controls, and that the control is installed. |
| Export fails | The destination is writable and the workbook is not already open in Excel. |

## Good to know

- Function and quality checks read the JavaScript text; unusual code styles or minified code can be missed.
- Libraries are searched only on the same form as the handler.
- Subgrid events and PCF controls are read from the form definition; PCF libraries come from the control's manifest only.
