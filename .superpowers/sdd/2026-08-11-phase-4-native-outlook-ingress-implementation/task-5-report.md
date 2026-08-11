# Task 5 report — local Outlook configuration UI

## Outcome

Implemented the local `/outlook` configuration experience. The UI creates, edits,
pauses and removes Outlook profiles through the existing SQL control plane; it
records browse and manual catch-up work for a later native host claim. It does not
start a process or invoke COM.

The projection exposes only display names, sanitised spool health/capacity,
timestamps, state and aggregate export counts. It excludes spool paths, Outlook
store and entry identifiers, message content, credentials and raw diagnostics.

## Scope review

- No Gmail integration was added.
- No COM reference, Outlook-process invocation or browser/live execution was added
  to the Web host.
- No REST endpoint, MCP tool or CLI mutation surface was added.
- The only writes are the required local UI calls to the existing durable SQL
  control-plane store. Enabling is explicitly deferred to a later host claim.

## Verification

Command:

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~OutlookPageStateTests|FullyQualifiedName~OutlookProjectionReaderIntegrationTests|FullyQualifiedName~NativeOutlookConfigurationBrowserTests|FullyQualifiedName~WebHostCompositionTests"
```

Initial result: passed 19, failed 0, skipped 3.

## Independent-review correction

The initial implementation was committed as `f210ff3` (`feat: configure Outlook
capture in the local UI`). Independent review then identified three closeout
defects, all corrected in the follow-up commit containing this report:

- Windows Negotiate authentication is registered and explicitly challenged for
  `/outlook`, then preserved on the Blazor circuit path. The scoped policy captures the authenticated Windows identity
  and loopback connection boundary while the circuit has an HTTP context, so
  mutations do not depend on a later ambient `HttpContext`. Existing public
  read-only APIs remain anonymous.
- Existing-profile saves now require an expected configuration revision, and
  enabled saves also require the completed browse correlation. SQL checks both
  in the same mutation transaction and rejects stale or unrelated requests.
- The interactive page no longer accepts or renders a private spool path. It
  sends an opaque configured-spool key over SignalR; the server resolves and
  validates the private allowlisted path. All outward mutation errors are
  mapped to bounded messages and never echo exception text.

The browser specification now covers anonymous rejection, authenticated loopback
profile creation, antiforgery, and absence of the private spool path. The SQL specification covers
stale revision and unrelated browse rejection. These cases remain explicitly
environment-gated because the disposable SQL/browser variables are unavailable.

Fresh correction verification:

- `dotnet restore FluxKnowledge.slnx --locked-mode`: passed.
- Release solution build with `-warnaserror`: passed with 0 warnings/errors.
- focused Task 5 Web tests: 21 passed, 6 environment-gated skips.
- full Web suite: 82 passed, 16 environment-gated skips.
- Outlook domain tests: 10 passed.
- Outlook host tests: 30 passed.
- Outlook integration filter: 9 passed, 27 disposable-SQL skips.
- CLI `outlook` probe: usage only, exit code 2; no mutation command exists.

No SQL service, browser, COM adapter, Outlook process or live mailbox was
started. No Gmail-owned file changed.

## Task 7 fresh evidence

With the disposable SQL connection enabled, the focused Task 5 Web matrix passed
28 tests and explicitly skipped the three browser tests because
`FLUXKNOWLEDGE_BROWSER_TESTS` was not enabled. The skip preserves the approval
boundary: no browser, COM adapter, Outlook process or mailbox was started.
