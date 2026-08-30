# Native FluxKnowledge setup

## Supported local prerequisites

FluxKnowledge is a private, single-user native Windows application. Its
supported integration endpoint is the direct loopback application at
`http://127.0.0.1:5137`; no Docker, Python service, remote endpoint or legacy
runtime is required or supported for this contract.

For development verification, install the .NET SDK version pinned by the
repository and restore the local tool manifest:

```powershell
dotnet tool restore
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
```

The native application is not started by these commands. Starting, deploying,
migrating, restarting or probing a live application needs separate operational
authority.

## Native v1 clients

The CLI reads a JSON request from standard input and prints the canonical JSON
envelope. It is a loopback-only client and refuses redirects or proxies.

```powershell
'{"view":"overview"}' | FluxKnowledge.Cli operations status
'{"query":"example","limit":10}' | FluxKnowledge.Cli knowledge search
'{"action":"create_note","title":"Example","body":"Safe retained note"}' |
  FluxKnowledge.Cli knowledge write --preview
```

For a mutation commit, resend precisely the previewed command with both values
returned or supplied by the caller:

```powershell
'{"action":"create_note","title":"Example","body":"Safe retained note"}' |
  FluxKnowledge.Cli knowledge write --commit `
    --confirmation-id <confirmation-id> `
    --idempotency-key <unique-idempotency-key>
```

The corresponding REST routes are under `/api/v1` and the native MCP endpoint
is `/mcp`. See [native v1 integrations](integrations.md) for all nine tools,
request routes and the confirmation protocol.

## Storage and recovery preparation

The production hierarchy is fixed beneath `I:\FluxKnowledge`:

```text
I:\FluxKnowledge\
  App\
  Config\
  Data\Sql\Data\FluxKnowledge.mdf
  Data\Sql\Log\FluxKnowledge_log.ldf
  Data\Index\
  Data\Retained\
  Runtime\Spool\
  Runtime\Temp\
  Runtime\Logs\
  CodexPlugin\
  Recovery\
```

The application rejects paths outside this hierarchy and reparse-point or
ambiguous resolution before production storage I/O. VSS recovery preparation is
plan-only: the intended OS-managed, unencrypted shadow-storage cap is 10% of
`I:` and there is no file-copy backup or automatic restore.

`FluxKnowledge.Cli fresh-start` emits the guarded layout and VSS plan with
`executionAvailable: false`. It does not erase files, detach a database,
configure VSS, install a plugin or start a host. Fresh-start is reserved for a
separately authorised go-live workflow.

## One-shot go-live boundary

The sole go-live entry point is `scripts/dev/complete-feature.ps1 -GoLive` with
the four explicit clean-slate, VSS, SQL-destruction and Codex-registration
acknowledgements. It admits only an absent `I:\FluxKnowledge` root and target
catalogue, or wipes both in that same confirmed invocation before continuing.
It does not inspect ownership state or use deployment journals, markers,
adoption, recovery, resume, repair or replay. A failure or interruption ends
the invocation; a later attempt requires a new explicit confirmation and clean
wipe. This documentation does not authorise that invocation.

## Codex plugin status

Application-owned plugin material belongs in
`I:\FluxKnowledge\CodexPlugin` and points only to the local `/mcp` endpoint.
Use the following command only to inspect its status:

```powershell
FluxKnowledge.Cli codex plugin status
```

The `codex plugin repair` command is intentionally unavailable to normal CLI
execution: it needs the typed authority of the separately approved go-live
workflow. Normal application startup never changes Codex registrations.

## Verification boundary

The verified non-live release gate is locked restore, a zero-warning Release
build, focused contract tests, the full Release suite and EF's
no-pending-model check. Browser validation is necessary only for a changed
interactive UI route. Neither that gate nor this document authorises live
deployment, migration, VSS configuration, a plugin lifecycle change or a
fresh-start operation.
