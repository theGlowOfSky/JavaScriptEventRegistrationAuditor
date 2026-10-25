# Changelog

## 1.2026.10.1
- Single-screen XrmToolBox interface: table list (left, ~25%) and results (right, ~75%).
- Load Environment Data lists eligible tables only; Load Registrations (or the per-table View button) loads the forms of the selected tables and only the JavaScript libraries registered on those forms.
- Optional, off-by-default inclusion of subgrid events and PCF controls.
- Function verification (registered library first, then the other libraries of the same form), library mismatch detection and recommended actions.
- Preview with all 15 report columns, sorted by Event Name (A-Z).
- Excel export: Table Index plus one sheet per table with registrations, Back to Table Index links, red/orange mismatch colouring.
- Export Analysis (CSV): quality checks and dependency findings.
- Session caching, cancellation, throttling-aware retries and partial-failure reporting.
