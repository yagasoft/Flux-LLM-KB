# FluxKnowledge

FluxKnowledge is a private, single-user knowledge application for Windows. It
retains local documents and useful knowledge, indexes their content and exposes
bounded search and operations through a Blazor interface, MCP, REST and a CLI.

The application runs on .NET 10 and ASP.NET Core under IIS. SQL Server holds
canonical data and durable work; SQL Full-Text and embedded USearch provide
search projections. The application endpoint is `http://127.0.0.1:5137`.

## Capabilities

- Local folder registration, watcher-assisted reconciliation, revision tracking,
  pause/resume and complete removal of application-owned source data.
- Retained UTF-8, ZIP/TAR, Office Open XML, PDF and C# processing. A document's
  extracted content keeps the identity of its original file.
- English document OCR through a provisioned offline PaddleOCR-VL adapter and
  interactive Visio processing through the logged-in Windows companion host.
- SQL-authoritative scheduling, leases, idempotent operations, publication
  fences and rebuildable search indexes.
- Nine native MCP tools with corresponding REST and CLI operations for knowledge,
  retained code, corpus management and operational evidence.
- A local operator interface for sources, corpus, pipeline records, events,
  search, Outlook capture and retained C# facts.

Supported formats and important limits are listed in
[file-type coverage](docs/file-type-coverage.md). Learned semantic retrieval is
[planned work](docs/roadmap.md); the current embedding baseline is deterministic.

## Build and verify

Use Windows, the SDK selected by [global.json](global.json), PowerShell 7 and
the documented [development prerequisites](docs/setup.md).

```powershell
dotnet tool restore
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --filter "Category!=Browser"
pwsh -NoProfile -File tests/native/repository-contract.ps1
```

SQL integration tests use generated disposable databases through the guarded
test fixture. These commands do not deploy the application. Model-backed
operation uses verified files under `J:\Models`; ordinary tests use synthetic
fixtures and must not download models.

For an existing installation, review the incremental update plan described in
[setup](docs/setup.md). Applying an update requires explicit operational approval.

## Documentation

- [Architecture](docs/architecture.md)
- [Setup and verification](docs/setup.md)
- [MCP, REST and CLI integrations](docs/integrations.md)
- [Operator guide](docs/user-guide/dashboard-user-manual.md)
- [File-type coverage](docs/file-type-coverage.md)
- [Safety and data boundaries](docs/safety.md)
- [Roadmap and remaining work](docs/roadmap.md)

Git contains source, migrations, synthetic tests and public documentation.
Private content, credentials, model payloads and runtime data belong outside it.
