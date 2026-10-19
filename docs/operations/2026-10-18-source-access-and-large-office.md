# Source access and large Office follow-up

The 18 October production release `20261018T095852Z-a32a97fd2b1f`
was applied through the approved incremental updater. A bounded read-only Preview
accepted the authorised cloud-managed root; six sampled permission refusals
remained. It created no durable root or scan and requested no cloud hydration.

A subsequent approved zero-content-byte probe used the bound IIS application's
actual token. All three exact sampled files returned Windows error 5,
`SourceAccessDenied`, with unchanged file identity and ACL metadata. This proves
application-account denial for those samples. It does not locate the denying
parent component or explain every historical scan report. No permissions changed.
The requested architecture document was already indexed and readable through
the supported Corpus view; no retry was needed.

## Focused Office correction

A finite retained-only experiment found two Word XML parts above the old 64 MiB
limit. Raising only that limit to 128 MiB let one current document extract
1,235,471 UTF-8 bytes in about one second. The other document then hit the
unchanged two-million-element limit. A third input had a compound-binary
container; that signature alone proves neither corruption, pre-OOXML Word format
nor encryption. Private paths, hashes and receipts remain outside Git.

The prepared OOXML v3 descriptor uses a new immutable capability identity.
New DOCX/XLSX/PPTX work may stream XML parts up to 128 MiB. Total XML expansion
remains 512 MiB, with two million elements, depth 128, 2,048 entries, 8,192
relationships and a 169:1 XML compression ratio. Compressed input stays at
128 MiB and extracted text at 200 MiB. Visio's shared parser keeps its 64 MiB
part limit. Package paths, relationships, encryption, checksums and source
authorisation are unchanged.

Exact v1/v2 descriptor pairs remain eligible for historical registration,
publication and source-status projection. Old runnable registrations retire;
the deployment readiness fence also recognises v2 pending/running work and
unsettled force requests. Activation must drain or refuse that work.

An exact v2 predecessor refused with `office-document-part-unsupported` can be
submitted through the existing retained `policy_reprocess` preview/commit route.
The route still verifies the root configuration, revision, retained hash,
completed attempt, leases, row versions and binding uncertainty. It creates a
successor without resetting prior attempts or replacing earlier lineage.
Startup and ordinary promotion do not replay terminal branches.

Current issue projections also recognise exact v2 successors. A later successful
successor keeps its resolved predecessor out of current failures across the v3
upgrade; a different retained hash does not hide an unresolved failure.

## Verification and production boundary

The disposable cap experiment built without warnings and used no production
database, models or original-source bytes. Test-first checks reproduced the old
part refusal and absent v2 retirement/recovery fences. Focused verification then
passed 122 OOXML domain checks, 70 SQL integration checks and 19 Web projection
and presentation checks, with no skips or new warnings. A new fixture initially
reused an idempotency key across v1/v2 cases and correctly received a confirmation
refusal; distinct test keys resolved it without changing product guards or
assertions. Canonical closeout, independent final review and production
application remain separate gates.

Production deployment and any exact-item recovery require fresh user approval,
current independent operational review and the incremental updater's native
drain/validation-hold safeguards. No failed-item replay, ACL change, interpreter
adoption, new scan or model acquisition has been performed by this follow-up.
Paired rollback is valid only before any attempt to release the deployment hold.
After a release attempt, including a successful release before explicit item
admission, workers may have written durable state. Preserve the installed
payload, immutable metadata and recovery receipts and use reviewed forward
recovery rather than replaying predecessor rollback or relabelling v3 work to
fit an older payload.

The ordinary updater now records the release attempt before removing the hold.
Both recovery and final cleanup refuse predecessor restoration or a second
release after that boundary. Disposable fault injection covers failures before
and after deletion, a later probe failure, successful release and the existing
paired rollback before the boundary, in both PowerShell runtimes.

Source permissions and OCR startup under the application identity remain separate
unresolved causes. Oversized XML, unsupported binary formats, encrypted content,
image bounds, checksum failures and uncertain native ownership remain current
issues. The user's revised direction trusts all added sources and requires
continued work across format, encoding, count, nesting, image and size restrictions.
Unnecessary processor refusals should give way to suitable streaming, batching or
resumable work; the runtime must still report actual access, integrity and
ownership failures truthfully. This delivery does not claim those follow-ups are
already implemented.
