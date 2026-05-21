---
applyTo: "**"
description: "Workspace scope — reference-only projects must never be modified"
---

## Reference-only projects

This workspace contains three folders. Only `smart-orders-dotnet` is the active implementation target.

- `smart-orders-adk` — **READ-ONLY reference**. Do not modify, suggest changes to, or include in analysis.
- `BobPOc` — **READ-ONLY reference**. Do not modify, suggest changes to, or include in analysis.

All code generation, edits, and analysis apply exclusively to `smart-orders-dotnet`.
