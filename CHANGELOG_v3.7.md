# v3.7 — SQL Risk Observability

- Admin-only, read-only `/api/admin/risk/overview` endpoint.
- Aggregated SQL account cash, pending orders and execution counts.
- Responsive bilingual EN/FA admin panel, with English as the default.
- Connection failures fail visibly rather than displaying misleading zero values.
- No changes to the execution engine, identity model, or safety controls.

**Limitations:** No daily loss enforcement or ASP.NET Core Identity in this release. Paper trading only.
