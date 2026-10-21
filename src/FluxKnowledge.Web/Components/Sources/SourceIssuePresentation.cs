using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Web.Components.Sources;

internal static class SourceIssuePresentation
{
    internal static IReadOnlyList<SourceActivityReasonProjection> GroupCurrentFiles(IReadOnlyList<SourceFileProjection> files) =>
        files.Where(file => file.Status is "Failed" or "Blocked" or "Deferred" or "Unconfirmed")
            .GroupBy(file => (file.Status, Reason: string.IsNullOrWhiteSpace(file.Reason) ? "No reason recorded." : file.Reason))
            .Select(group =>
            {
                var paths = group.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                return new SourceActivityReasonProjection(group.Key.Status, group.Key.Reason, paths.Length) { AffectedPaths = paths };
            })
            .OrderByDescending(group => group.Count).ThenBy(group => group.State, StringComparer.Ordinal).ThenBy(group => group.Reason, StringComparer.Ordinal).ToArray();

    internal static string Guidance(string? reason) => reason switch
    {
        "File extension is unknown to local source ingestion." => "An earlier extension allowlist refused this item. Complete, valid UTF-8 text can use exact retained-policy recovery; recognised binary formats still require a supported parser. No source edit or full scan is needed.",
        "index-storage-capacity-deferred" => "Publication is waiting for free index storage and will retry automatically. Free app-owned storage through reviewed retention; the source file remains eligible and its previous publication is preserved.",
        "SourceAccessDenied" => "Windows denied access to the indexing identity. Ask an administrator to approve scoped automatic source-access recovery or grant read access to this item. Source trust does not change Windows permissions.",
        "SourceAccessRepairNotApproved" => "This physical source root has no approved automatic access policy. An administrator must approve the root and indexing identity before the helper can grant read access.",
        "SourceAccessRepairUnavailable" => "The source-access helper is unavailable. Restore its configured Windows task and policy. IIS has no permission-changing privileges; normal reconciliation will retry.",
        "SourceAccessExplicitDeny" => "An applicable Windows deny rule prevents read access. Automatic recovery preserves that rule. An administrator must review the affected item and the indexing identity's group restrictions.",
        "SourceAccessPermissionsUnsupported" => "These Windows permissions cannot be safely amended automatically. An administrator must review the item's ACL; recovery will not reset inheritance or remove unrelated rules.",
        "SourceAccessOutsideApprovedRoot" => "A required path component lies outside the approved repair root, or the path is unsafe. Review the exact path and parent traversal permissions; automatic recovery will not extend its scope.",
        "SourceAccessIdentityChanged" => "The approved physical source identity changed. Confirm the current folder and renew its access policy before retrying; a reused path is not sufficient authority.",
        "SourceAccessContentUnavailable" => "The cloud provider could not supply a guarded local read. No download was requested. Make the item available in the sync client before retrying.",
        "SourceAccessRepairUnconfirmed" => "Read access could not be confirmed after the repair request. Review the helper's unresolved diagnostic and the current file/provider state before targeted recovery.",
        "SourceAccessRepairLimit" => "This read reached the bounded component-repair limit. Normal reconciliation can continue with the remaining components; review repeatedly denied parents if progress stops.",
        "SourcePathDenied" or "SourceDirectoryDenied" or "SourceRootAccessDenied" => "This older scan did not distinguish permissions from path-security refusals. A scan with the updated diagnostics is needed before changing permissions.",
        "SourceReparseTypeNotSupported" or "SourcePathSafetyRefused" => "An unsupported reparse point or unsafe redirection was refused. Use the real authorised local folder; symbolic links and junctions remain excluded.",
        "SourcePhysicalPathMismatch" or "SourcePathOutsideRoot" or "SourceRootIdentityMismatch" or "SourceRootIdentityMissing" or "SourceRootIdentityMalformed" => "The path or physical identity no longer matches the admitted source. Review the real folder and re-admit it; do not bypass the identity check.",
        "SourceContentNotAvailableLocally" => "The provider could not supply guarded local content. Make only the required item available locally in the sync client, then rescan. No download was requested by FluxKnowledge.",
        "CloudDirectoryNamespaceIncomplete" => "Only the locally materialised cloud namespace was scanned. Make the required folder available in the sync client before rescanning. Unseen files are preserved, not treated as deleted.",
        "SourceInUse" => "Another process prevented a stable read. Let its write finish or close the item, then rescan.",
        "SourceChangedDuringDiscovery" or "SourcePathNoLongerExists" => "The item changed during the scan. Let changes settle, then rescan.",
        "SourceIoFailure" => "Windows or the filesystem provider refused guarded I/O. Review the recorded native error and provider status before rescanning.",
        "document-ocr-runtime-failed" => "The OCR child exited without a specific failure result. A bounded retry with failure-stage diagnostics is required; this is not proof that the source is corrupt.",
        { } code when code.StartsWith("document-ocr-runtime-exit-", StringComparison.Ordinal) => "The OCR child exited before returning a valid failure result. The final eight hexadecimal digits record its exit status. Review interpreter access and runtime startup under the application account before retrying; this is not a content-corruption diagnosis.",
        "document-ocr-dependency-unavailable" => "An installed OCR dependency is missing or incompatible. Repair the verified offline runtime before retrying this item; model downloads are not automatic.",
        "document-ocr-model-initialization-failed" => "OCR failed while initialising the verified local runtime or models. Diagnose that stage in isolation before retrying source items.",
        "document-ocr-orientation-failed" or "document-ocr-recognition-failed" => "OCR failed at the indicated inference stage. Validate one retained item in isolation before a bounded retry; do not replay the whole source.",
        "document-ocr-offline-operation-refused" => "OCR attempted an operation outside the offline contract. Review the local runtime configuration; automatic acquisition and network fallback remain blocked.",
        "document-ocr-provider-result-invalid" => "OCR did not return a valid bounded result. Review the runtime using one isolated item before retrying.",
        "image-document-dimensions-exceeded" => "An earlier image processor refused these dimensions. The current processor uses a decoded memory budget. Retry this retained item only after its previous execution is confirmed settled.",
        "image-document-decoded-memory-budget-exceeded" => "Decoding and orientation require more than the 256 MiB bitmap budget. This item needs bounded tiled processing or a reviewed capacity change.",
        "image-ocr-raster-page-output-too-large" => "The decoded image is valid, but its PNG for OCR exceeds 16 MiB. Process this image as bounded tiles before retrying.",
        "pdf-document-encrypted" => "An earlier processor refused the PDF encryption flag. The current reader accepts password-free readable PDFs. Retry this retained item after settlement checks; password-protected content requires an authorised unlocked copy.",
        "pdf-document-password-required" or "office-document-encrypted" => "Supply an authorised unlocked copy. The processor cannot open this content without a password or supported rights-management access.",
        "office-document-container-invalid" => "The processor could not validate this Office package. Inspect the retained package and parser reason before choosing targeted recovery; this code alone does not establish corruption. Package-integrity checks remain enabled.",
        "office-document-part-unsupported" => "An Office part, relationship structure or derived-output receipt was refused. Older OOXML processors also used this code for per-part size limits. Inspect the retained package and processor evidence before choosing targeted recovery; validation guards remain enabled.",
        "office-document-expanded-xml-limit" => "The Office package exceeds its XML expansion budget. OOXML v3/v4 permit 128 MiB per part, 512 MiB across XML parts and a 169:1 XML compression ratio. Visio and OOXML v2 use 64 MiB parts; older processors have version-specific lower budgets. The current runtime has no budget override; larger content needs capacity-aware processing.",
        "office-document-element-limit" => "An earlier OOXML processor refused its element count. OOXML v4 removes that ceiling and supports exact retained-item recovery. Visio keeps its separately versioned parser limits.",
        "archive-entry-path-invalid" => "An archive member has an unsafe or unsupported path. Repack authorised members using relative forward-slash paths without traversal, drive names or links.",
        "archive-entry-count-limit" or "archive-member-size-limit" or "nested-archive-depth-limit" => "The archive exceeds the indicated safe processing limit. Split or flatten an authorised copy rather than disabling archive safeguards.",
        "retained-artifact-checksum-invalid" => "Retained bytes failed their checksum. Preserve the evidence and verify the original and retained copy before any re-retention; do not bypass the checksum.",
        "repository-gpu-outcome-cleanup-or-binding-unconfirmed" or "source-revision-binding-unconfirmed" => "Execution ownership or prior native cleanup is unconfirmed. Review the exact binding and cleanup evidence before recovery; do not blindly retry or clear it.",
        "compound-binary-parser-unavailable" or "legacy-office-binary-parser-unavailable" or "binary-format-parser-unavailable" or "No installed parser can process this retained binary format." => "No installed parser supports these bytes. Export an authorised copy to a supported format; allowing the policy does not add a parser.",
        "archive-zip-expansion-pending" => "Archive expansion has not completed. Review the current retained branch and enabled capability before scheduling a bounded replay.",
        "File bytes contain binary control values and are not accepted as text." => "These bytes cannot safely be treated as text. Use a supported binary parser or an authorised text export.",
        _ => "Review the recorded cause and affected item before retrying. Current security and content-safety checks remain in force."
    };
}
