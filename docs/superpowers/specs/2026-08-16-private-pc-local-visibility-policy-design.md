# Private-PC local visibility policy design

## Decision

Flux-LLM-KB is a permanently private, single-user application on the owner's PC.  Its trusted local interfaces may show useful retained-content-derived information rather than treating every private value as unsuitable for presentation.  This amendment supersedes any contrary local-output restriction in every phase and design.  It does not weaken secret handling, public-repository hygiene, external publication controls, source-original access rules, or operational approval gates.

## Visibility boundary

There are two output classes:

1. **Trusted local output** is the local Blazor UI, direct-loopback REST and SignalR, user-invoked CLI, the legacy local-process MCP server, and the native HTTP MCP endpoint at `/mcp` (including its SSE/event stream), local diagnostics, local audit views, local search/result detail, and retained-processor output.  It may expose useful raw retained-content-derived text/code, symbol names, signatures, relationships, local paths, hashes, parser diagnostics, identifiers, process/runtime evidence, and provenance.
2. **External/public output** is a Git commit, repository documentation or fixture, export/share/sync payload, remote/proxied endpoint, external log sink, generated report intended for publication, and any future multi-user surface.  It remains sanitised and contains no private retained content, private paths, raw diagnostics, live runtime state, credentials, or secret material.

Trusted local REST and SignalR remain direct-loopback-only and reject forwarded/proxy headers.  The legacy MCP server remains a local-process integration and must not introduce a remote listener or forward retained content.  Native MCP is instead an anonymous HTTP transport on the approved private PC: every `/mcp` request, SSE negotiation and event-stream reconnect must enforce direct-loopback authority and forwarded/proxy-header denial before tool dispatch.  It must not require Windows, Negotiate, cookie or any other user authentication; the approved authority is the direct loopback connection itself.  A direct non-loopback request and a loopback request carrying forwarding/proxy headers are both denied.  This is a disclosure policy, not a mutation policy: existing antiforgery, loopback, lease/fencing, idempotency, cancellation and explicit operational-approval rules remain in force.

## Secret boundary

The policy never permits a password, token, OAuth client secret, cookie/session value, connection string, private key, key material, credential header or credential-bearing diagnostic to be persisted in a new projection, returned by a local contract, copied to audit/status evidence, or committed to Git.  A local raw-content route must apply the existing secret-detection/redaction policy before returning a retained text/code excerpt and return a fixed `secret-content-withheld` indication when it detects a value.  Raw retained bytes remain behind the immutable retained-artifact reader; no interface may reopen a source original to populate a detail view.

Every persisted retained-derived fact uses a bounded secret scan before its SQL or audit write.  The outcome is per fact, never a later presentation-only scrub:

| Fact | Scan fields | Clean result | Secret detected | Scan cannot complete within its bound |
| --- | --- | --- | --- | --- |
| Symbol | name, qualified name, signature and modifiers | Persist the complete fact | Withhold that symbol; persist only a bounded count/reason `secret-content-withheld` | Block the completion with `csharp-code-secret-scan-failed`; commit no document or facts |
| Reference | target display text and relationship display fields | Persist the complete fact | Withhold that reference; persist only a bounded count/reason `secret-content-withheld` | Block the completion with `csharp-code-secret-scan-failed`; commit no document or facts |
| Parser diagnostic | rendered message/detail and any source text fragment | Persist only the bounded, scanned diagnostic | Withhold the diagnostic text; retain its stable diagnostic code/count and `secret-content-withheld` | Block the completion with `csharp-code-secret-scan-failed`; commit no document or facts |
| Local excerpt or audit/status evidence | rendered retained-derived text | Return/persist the bounded scanned value | Withhold the value and return/record `secret-content-withheld` | Block that disclosure/write; do not substitute an unscanned value |

Tests must seed synthetic secret sentinels independently in a symbol, signature, reference display text and parser diagnostic, and prove the clean fact persists, the detected fact is absent from durable rows/local detail and only its bounded withholding evidence remains, and scanner failure leaves no partial document/fact set.

Audit records may contain useful local fields and parser diagnostics but must store bounded data and must not duplicate arbitrary raw files.  The retained artifact remains the authoritative byte store.  Public/export audit projections are a separately sanitised projection, never a flag on the local record.

## Whole-application contract change

Every existing local surface is widened deliberately, not by removing filters from a shared public/export reader:

| Contract family | Local capability | Required boundary |
| --- | --- | --- |
| Sources, Corpus, Events and retained branches | Original local path/provenance, content hash, retained binding facts, child/member relations and bounded detail | Verified retained binding for content; no synthetic locator or credential disclosure through external readers |
| Search and code search | Exact paths, code excerpts, symbols, signatures, references, parser fallback/diagnostics and retained provenance | Explicit local-only scope for raw hydration; secret scan for excerpts |
| REST, CLI and MCP | The same local read fields and reason/diagnostic detail for a contract family | Direct-loopback and forwarded/proxy denial for REST and native HTTP `/mcp`/SSE; local-process-only legacy CLI/MCP; existing mutation authorities remain unchanged |
| UI, dashboard, audit, diagnostics and status | IDs, local paths, hashes, process/runtime evidence and bounded raw retained-derived evidence when useful | Local-only renderers/status feeds; external/export views use sanitised DTOs |
| Outlook and mail | Retained export identifiers, paths, content and bounded diagnostic evidence already stored locally | No Outlook activation, profile enablement, mailbox/source-original reread, credential/header exposure or external forwarding |

Existing sanitised DTOs remain valid compatibility shapes; a local detail/read model may add fields.  A change must not repurpose a shared corpus, public, or export projection such as `SqlCorpusProjectionReader` merely because it happens to be reachable locally.  It must introduce or extend a named local-only projection and test both its local detail behaviour and its external/public exclusion.

## Application-surface ledger

Every implementation task must account for each current surface; an unlisted transport is not implicitly covered by another reader.

| Application | Surface | Local-detail contract | Boundary/test obligation |
| --- | --- | --- | --- |
| Native .NET | Blazor UI and dashboard | Named local-only detail/audit/search projections | Render synthetic detail; prove public/export DTO exclusion |
| Native .NET | REST and SignalR | Local-only read DTOs and bounded status/detail events | Direct loopback allowed; remote and forwarded/proxy requests denied |
| Native .NET | HTTP MCP `/mcp` and SSE | The same named local-only read DTOs, never a shared export reader | Anonymous direct-loopback authority only; direct remote and forwarded/proxy request/SSE reconnect denied before dispatch; no Windows/Negotiate/cookie requirement |
| Native .NET | Outlook UI/host views | Existing retained export detail only; no profile activation or source-original reopen | Preserve Outlook/profile/COM denial and external projection exclusion |
| Native .NET | Operator Actions | Existing capability-bound action/status detail only; no C# action is added | Preserve fixed-origin, antiforgery, loopback, forwarded/proxy and hard-denial policy |
| Native .NET | `FluxKnowledge.Cli` | Explicit read-only local code/detail, diagnostic and retained-provenance projections | User-invoked local process only; no listener/export-serializer reuse and no mutation command, authority or capability widening |
| Legacy Python | React UI | Named local-only result/detail adapters | Synthetic detail and public/export exclusion tests |
| Legacy Python | REST | Named local-only result/detail adapters | Local authority, remote/forwarded denial where HTTP-hosted, and export exclusion |
| Legacy Python | CLI | Explicit local command projections | No listener, no export serializer reuse and no mutation widening |
| Legacy Python | MCP | Explicit local-process tool projections | No listener/forwarding, secret withholding and parity tests |
| Legacy Python | Diagnostics, audit and search | Named local-only diagnostics/audit/search adapters | Bounded facts, per-fact secret outcomes and sanitised external/export views |

## Safe validation and parser/runtime policy

Generated/disposable test databases and synthetic browser validation are standing-authorised.  Tests may create, migrate, use and dispose their generated SQL catalogues, but must never select a configured non-disposable, production, or externally shared database.  A missing disposable configuration is an implementation task: provision/configure the safe fixture and run the relevant matrix rather than using the absence as a reason to stop.  Browser validation uses synthetic data and disposable local infrastructure only.

Browser execution is an explicit local prerequisite, not a skip.  The disposable-browser helper must discover the `Microsoft.Playwright` Chromium executable from an explicitly supplied local executable path or the locally restored Playwright browser cache, canonicalise the path, require that it is an existing regular executable beneath that approved local cache/path, and prove it can report its local version before test launch.  It must pass that validated executable to the synthetic Playwright launch configuration.  It may provision only this disposable test configuration; it must never run `playwright install`, `playwright install chromium`, an installer, a package restore that fetches packages, or any other browser download/network operation.  The browser wrapper sets `FLUXKNOWLEDGE_BROWSER_TESTS=1` only in the child environment of the synthetic disposable test command, never for a shared shell, service or live site.  A missing, non-executable or non-launchable Chromium is a failed browser-infrastructure result, not a skipped matrix or fallback browser.

Maintained deterministic local parser packages and runtimes may be added when a design names their exact version, licence, startup preflight, offline/no-download behaviour and failure outcome.  Network/cloud parsers, model downloads or activation, GPU/model work, Office automation, Outlook activation, and source-original rereads remain separately approved operations.

## Rollout and verification

The rollout is contract-family based so that every widening is observable and reviewable:

1. Add a shared local-disclosure classification and direct-loopback/local-process authority tests, without changing mutation powers.
2. Widen Sources/Corpus/retained-branch local detail and audit reads, including external/public sentinel exclusion.
3. Widen code search, generic search, REST, CLI and MCP together so their fields remain consistent.
4. Widen dashboard, diagnostics, status and audit viewers, preserving bounded payloads and external/export DTO separation.
5. Deliver retained C# parsing under the companion C# processor design, then expose its facts through the local detail/search family.

Each vertical slice requires focused RED/GREEN tests, synthetic secret sentinels proving exclusion, disposable-SQL/migration evidence where persistence changes, browser tests where UI changes, and a fresh independent reviewer.  No slice authorises deployment, push, merge, a production migration, external publication, live validation, Outlook activation or source-original reread.

## Superseded wording

This design supersedes local-only readings of "public projection", "sanitised output", "privacy-safe diagnostics", and "no private path/raw content" in architecture, roadmap, Phase 5 branch and Operator Actions documents.  Those phrases now apply to external/public/export/shared output and to actual secrets.  Historic evidence remains historically accurate; it does not constrain a later trusted-local contract.
