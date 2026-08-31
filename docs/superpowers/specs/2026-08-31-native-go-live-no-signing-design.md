# Native go-live without certificate signing

## Decision

The private native go-live flow no longer uses SQL module signing, a
certificate-mapped login, certificate evidence, or least-privilege authority
proof. The confirmed local bootstrap Windows identity and the fixed IIS
application-pool identity are treated as trusted local administrators for the
go-live lifecycle.

## Runtime model

The one-shot closeout still runs only through `scripts/dev/complete-feature.ps1
-GoLive`, receives the four explicit confirmations, and publishes an immutable
merged-main payload. The bootstrap SQL creates the fixed app-pool Windows login
when needed, grants it membership of `sysadmin`, and creates the existing four
fixed procedures without certificates or signatures. The procedures keep their
fixed catalogue, path and identity checks, but database ownership and live
authority observations are bound to the app-pool login rather than a
certificate login.

The native bridge continues to use the caller's supplied local Windows
bootstrap connection. Its post-bootstrap observation validates only functional
facts: the fixed app-pool login/SID, app-pool sysadmin membership, target
catalogue ownership by that login, the four fixed procedures, and the generated
bootstrap manifest hashes. It does not model a signing certificate, certificate
login, thumbprint, or signature.

## Removed

- `FluxKnowledgeNativeGoLiveCertificate` and its certificate login.
- Certificate creation, grants, signature creation and certificate SID checks.
- Certificate fields from the generated manifest, bootstrap evidence and
  validation contracts.
- Reset cleanup for certificate objects and certificate-specific test mutants.

## Retained

- Fixed `I:\FluxKnowledge` hierarchy, explicit confirmations and fail-closed
  clean-slate admission.
- The existing fixed procedure names and fixed target database paths.
- Disabled Phase 6 and Outlook capabilities, retained-only data handling and
  the secret-disclosure boundary for actual credentials and private keys.
- No journal, adoption, recovery, resume, repair or manual closeout path.

## Acceptance

Synthetic bootstrap contracts prove that the canonical SQL contains no
certificate/signature machinery, grants the fixed app-pool login `sysadmin`,
and retains exactly the four fixed procedures. Disposable SQL integration
proves the bridge accepts the direct-admin authority shape and rejects missing
app-pool identity, missing sysadmin membership, wrong database ownership and
wrong procedure manifest. The focused native contracts, locked restore,
zero-warning Release build, full native Release suite and EF no-pending-model
check must pass before the next official go-live invocation.
