# FluxKnowledge

FluxKnowledge is a self-hosted knowledge base for Windows. It makes local documents, Git workspaces, captured Outlook mail and saved notes searchable, with citations back to retained source revisions. It is designed for a single trusted local user.

## Use FluxKnowledge

- Add a local folder or Git workspace under **Sources**. FluxKnowledge tracks changes and keeps indexing and recovery progress visible in **Overview**.
- Search the corpus or a specific workspace. Results show cited passages and available page, image or shape locations. Retained C# also supports structured code queries.
- Connect an assistant over MCP, REST or the CLI. The Codex integration can supply cited workspace context and capture useful knowledge between sessions.

A provisioned instance serves the [dashboard](http://127.0.0.1:5137/) on loopback. REST uses `/api/v1` and MCP uses `/mcp`. [Integration guide](docs/integrations.md)

## Install or develop

The deployed app uses ASP.NET Core and Blazor on IIS. SQL Server with Full-Text holds the authoritative data, and USearch provides a rebuildable vector index. Optional model runtimes load from local files under `J:\Models`. Outlook capture and Visio extraction use a configured Windows desktop companion.

Use the [setup guide](docs/setup.md) for installation, updates and development. It covers the SDK pinned in [global.json](global.json), disposable SQL tests and [isolated staging](docs/operations/2026-10-14-isolated-staging.md).

## Documentation

- [Dashboard guide](docs/user-guide/dashboard-user-manual.md) for source management, indexing and recovery.
- [File-type coverage](docs/file-type-coverage.md) for supported formats and extraction limits.
- [Architecture](docs/architecture.md) and [data boundaries](docs/safety.md) for storage, processing and local access.
- [Roadmap](docs/roadmap.md) for current capabilities and remaining work.

[MIT licence](LICENSE)
