# v3.6 — Operations & Safety Console

- Administrator-only operations summary API with SQL connectivity and order/account counts.
- Dark operations panel with SQL safety state, pause/resume controls and bilingual EN/FA labels.
- Conservative behavior on database failure: show offline; do not expose credentials.
- No changes to the simulated matching algorithm or database schema.

## Important limits
The shared legacy trading engine is separate from SQL trading. The SQL safety flag is not an atomic barrier for in-flight orders. No ASP.NET Core Identity migration was performed.
