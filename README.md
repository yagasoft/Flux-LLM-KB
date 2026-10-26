# FluxKnowledge

FluxKnowledge is a private, self-hosted knowledge application for Windows. It
brings local documents, source code, captured mail and saved knowledge into one
searchable store, with results traceable to their retained sources and revisions.

Manage sources through the web dashboard, or connect an AI assistant through
the Model Context Protocol (MCP), REST or the CLI to retrieve cited context and
preserve knowledge between sessions. It is designed for one trusted local user.

## What it does

- **Index workspaces.** Register local folders or Git repositories with
  include/exclude rules. Track revisions, pause or resume sources, and remove
  application-owned data while preserving originals.
  Git sources follow tracked files; opt-in NTFS journal scanning and paced
  integrity checks support ongoing synchronisation.
- **Extract documents and mail.** Process UTF-8 text, Markdown, structured text,
  DOCX, XLSX, PPTX, PDF and ZIP/TAR archives. Provisioned English OCR handles
  scanned PDF pages and JPEG/PNG images. A logged-in Windows companion provides
  configured Outlook capture and Visio extraction.
- **Search with evidence.** Combine lexical and semantic passage retrieval with
  reranking across the corpus, a source or a registered workspace. Read cited
  passages with source/revision identity and available page, image or shape
  locations. Search reports degraded or unavailable modes.
- **Explore code.** Query retained C# syntax, symbols, relationships and
  diagnostics without building or executing the project. Supported repository
  languages also have searchable source text.
- **Retain useful knowledge.** Store notes, claims and relationships alongside
  indexed sources. The Codex integration supplies cited workspace context at
  prompt time and captures final responses as notes through its Stop hook.
- **Connect local tools.** Eleven MCP tools and corresponding REST/CLI
  operations cover knowledge, corpus search and management, retained code,
  status and audit. A workspace-brief skill composes cited project summaries.
- **See what is happening.** Inspect sources, retained files, processing stages,
  saved passage progress, CPU/GPU activity, queues, blocked work and events in
  the dashboard.

## How it works

| Layer | Implementation |
| --- | --- |
| Application and interfaces | .NET 10, ASP.NET Core and Interactive Server Blazor under IIS; shared MCP, REST and CLI contracts. |
| Canonical storage | SQL Server stores knowledge, source revisions, durable work, ownership and audit receipts. |
| Retrieval | SQL Full-Text, local BGE embeddings and cross-encoder reranking, with embedded USearch for rebuildable vector indexes. |
| Native processing | Scheduled CPU/GPU work, offline model adapters and a desktop companion for Outlook/Visio COM operations. |

Sources pass through retained snapshots, extraction, normalisation, indexing
and publication. Processing uses checksum-verified retained bytes; SQL controls
current revisions and publication. Durable leases and resumable work protect
against duplicate or stale completion.

Models load from verified local files and are never downloaded implicitly.
Local interfaces expose bounded, secret-filtered results; mutations use
preview-bound confirmation and idempotency keys.

## Getting started

Installation requires Windows, IIS with ASP.NET Core hosting support, SQL Server
with Full-Text, and local NTFS source storage. Outlook/Visio require their desktop
applications and a logged-in session. The layout uses `I:\FluxKnowledge` for the
application and runtime data, and `J:\Models` for model artifacts.

Follow [setup](docs/setup.md) to provision the application and optional
integrations. Interfaces use direct loopback: the dashboard at
`http://127.0.0.1:5137`, REST at `/api/v1` and MCP at `/mcp`. Add a folder or
repository through **Sources and indexing**, preview it and start indexing.

For development, use PowerShell 7 and the SDK selected by
[global.json](global.json), with the documented disposable SQL prerequisites:

```powershell
dotnet tool restore
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -m:1 -c Release --no-build --filter "Category!=Browser"
pwsh -NoProfile -File tests/native/repository-contract.ps1
```

Tests use disposable databases and synthetic models. Installation and updates
follow separate procedures in the setup guide.

## Current scope

The application is in personal use. Hybrid retrieval has passed scoped English
acceptance; broader workload latency, concurrency and recovery remain active work.
OCR has repeated-text fidelity limits and does not support Arabic or scanned
regions on pages that already contain native text. Binary Office formats and
general audio/video understanding are not supported extraction routes.

Cloud-managed sources require locally available content and qualified access;
OneDrive support has environment constraints. Format recognition does not
guarantee extraction. The [roadmap](docs/roadmap.md) records delivered scope and
outstanding acceptance.

## Documentation

| Guide | Contents |
| --- | --- |
| [Architecture](docs/architecture.md) | Components, processing, storage and retrieval design. |
| [Setup and verification](docs/setup.md) | Prerequisites, installation, updates and development checks. |
| [Integrations](docs/integrations.md) | MCP tools, REST routes, CLI commands and Codex workflows. |
| [Operator guide](docs/user-guide/dashboard-user-manual.md) | Dashboard workflows and source management. |
| [File-type coverage](docs/file-type-coverage.md) | Extraction routes and format limitations. |
| [Safety and data boundaries](docs/safety.md) | Local access, retained data, models and operational controls. |
| [Roadmap](docs/roadmap.md) | Delivered capabilities, acceptance evidence and remaining work. |

Released under the [MIT licence](LICENSE). The repository contains source,
migrations, synthetic tests and public documentation; private content,
credentials, model payloads and runtime data remain outside Git.
