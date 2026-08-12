# Task 1 report — anonymous direct-loopback Outlook operator

## Changed files

- `Directory.Packages.props`
- `src/FluxKnowledge.Web/FluxKnowledge.Web.csproj`
- `src/FluxKnowledge.Web/packages.lock.json`
- `tests/FluxKnowledge.Web.Tests/packages.lock.json`
- `src/FluxKnowledge.Web/Program.cs`
- `src/FluxKnowledge.Web/OutlookOperatorAuthentication.cs` (removed)
- `src/FluxKnowledge.Web/Components/Routes.razor`
- `src/FluxKnowledge.Web/Components/Pages/Outlook.razor`
- `src/FluxKnowledge.Web/Components/Outlook/OutlookPageState.cs`
- `tests/FluxKnowledge.Web.Tests/Components/OutlookPageStateTests.cs`
- `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`
- `tests/FluxKnowledge.Web.Tests/Browser/NativeOutlookConfigurationBrowserTests.cs`
- `tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs`
- `scripts/deploy/update-native-windows.ps1`
- `tests/native/native-deployment-plan.ps1`

## RED evidence

- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "FullyQualifiedName~OutlookPageStateTests" --logger "console;verbosity=normal"`
  - Failed as expected with the pre-amendment Windows identity condition: anonymous direct loopback, IPv6 loopback and loopback with a forwarded non-loopback value were rejected. 12 passed, 3 failed.
- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "FullyQualifiedName~Actual_program_entrypoint_has_no_Windows_authentication_dependency_or_service" --logger "console;verbosity=normal"`
  - Failed as expected while a temporary authentication registration was present: `IAuthenticationService` resolved from the host. The final composition test was narrowed to the application assembly's package boundary to avoid an unrelated parallel `WebApplicationFactory` disposal interaction.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests\\native\\native-deployment-plan.ps1 -SourceRoot (Get-Location).Path`
  - Failed as expected while a temporary `set config ... /section:windowsAuthentication` command was present.

An initial focused invocation before restore did not execute the rebuilt test host and produced no test-run output. After `dotnet restore tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --force-evaluate`, the normal focused runner produced the RED and GREEN evidence above. The restore updated only the relevant Web and Web-test lock files; incidental lock-file graph changes were excluded.

## GREEN evidence

- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "FullyQualifiedName~OutlookPageStateTests|FullyQualifiedName~Actual_program_entrypoint_has_no_Windows_authentication_dependency_or_service" --logger "console;verbosity=normal"`
  - Passed: 16/16. Covers anonymous direct loopback mutation, remote rejection, both forwarded-header cases, IPv6 loopback, and no Negotiate/authentication service/package.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests\\native\\native-deployment-plan.ps1 -SourceRoot (Get-Location).Path`
  - Passed: `Native deployment plan contract passed.`
- `dotnet build src\\FluxKnowledge.Web\\FluxKnowledge.Web.csproj -c Release --no-restore -warnaserror`
  - Passed: 0 warnings, 0 errors.
- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "Category!=Browser" --logger "console;verbosity=minimal"`
  - Passed: 91 passed, 10 existing disposable-SQL skips, 0 failed.

Browser tests were not run: the guarded fixture requires both `FLUXKNOWLEDGE_BROWSER_TESTS=1` and `FLUXKNOWLEDGE_TEST_SQL_CONNECTION`; neither was supplied. No live Outlook call was made.

## Safety confirmation

No COM or Outlook process was launched; no mailbox was accessed; no capture profile was created or enabled; no private spool contents were modified; no deployment, push or Gmail action occurred.

## Round 1 remediation — route/circuit gate and composition proof

### Changed files

- `src/FluxKnowledge.Web/OutlookOperatorLoopbackGate.cs` (added)
- `src/FluxKnowledge.Web/Program.cs`
- `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`

### RED evidence

- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "FullyQualifiedName~Actual_program_entrypoint_registers_no_authentication_surface_and_rejects_remote_Outlook_requests" --logger "console;verbosity=normal"`
  - Failed as expected before the gate was added: a remote peer with `Forwarded: for=127.0.0.1` reached `/outlook`, attempted SQL rendering and returned 500 instead of the required 403.

### GREEN evidence

- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "FullyQualifiedName~Actual_program_entrypoint_registers_no_authentication_surface_and_rejects_remote_Outlook_requests" --logger "console;verbosity=normal"`
  - Passed: 1/1. TestServer sets `RemoteIpAddress` directly. Remote `/outlook` and `/_blazor` requests with forwarded loopback headers returned 403 before rendering/circuit work. A loopback `/_blazor` request with a forwarded non-loopback header reached the hub endpoint and returned 400, proving the gate did not reject it. It also proves no `IAuthenticationService`, no `IAuthenticationSchemeProvider`, and no authentication middleware marker in the actual `Program` entrypoint composition.
- `dotnet test tests\\FluxKnowledge.Web.Tests\\FluxKnowledge.Web.Tests.csproj --no-restore --filter "Category!=Browser" --logger "console;verbosity=minimal"`
  - Passed: 91 passed, 10 existing disposable-SQL skips, 0 failed.
- `dotnet build src\\FluxKnowledge.Web\\FluxKnowledge.Web.csproj -c Release --no-restore -warnaserror`
  - Passed: 0 warnings, 0 errors.

No COM or Outlook process was launched; no mailbox was accessed; no capture profile was created or enabled; no private spool contents were modified; no deployment or Gmail action occurred.
