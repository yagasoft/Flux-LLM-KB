# Teams speaker attribution implementation plan

> Use superpowers:executing-plans to do the work in sequence, with independent Astra review at the consequential gates.

**Goal** Add evidence-based speaker labels to local transcription and Flux sidecars and refresh the two approved recordings.

**Architecture** Keep the detector and word-to-speaker alignment in `Media/speaker_attribution.py` and distribute that exact file to the shared tools. Existing Whisper settings, GPU admission, source capture and create-only Flux publication remain in force. The approved 20 November design is the basis for this plan.

## 1. Detect visible nameplates

- [x] Add red synthetic tests for the rail, gallery and presenter badge, lookalike colours in shared content, absent highlights, two badges and layout changes.
- [x] Use local PyAV input with secondary opens denied and bounded sampling. Detect the purple label shape inside corroborated Teams regions. Cache OCR for recurring crops, use an explicit local Tesseract executable and English assets under J:, and bound its time and calls. Keep frame data in memory.
- [x] Confirm names and activity across nearby samples. Retain concurrent alternatives, account for short no-highlight gaps conservatively, and report missing or unsupported evidence separately from analysed video.
- [x] Compare sampled timestamps with independent visual review of both recordings and record false identities, coverage, ambiguous turns and elapsed time.

## 2. Align and publish

- [x] Add red tests for intra-segment changes, concurrent names, unsupported layouts, unknown speakers, invalid word timing and preservation of recognised wording.
- [x] Share the alignment and formatting across the shared runner and Flux. Keep unlabelled outputs compatible, provide speaker candidates in structured output, and keep original JSONL recognition evidence. Package the module beside the supervised worker for both cold and warm execution.
- [x] Keep new Flux output inside existing limits. Treat attribution errors as a diagnosed fallback to speech-only processing. Preserve ownership checks and conflicting sidecars.
- [x] Verify native Python tests, shared-runner tests, isolated packaged imports, and Flux publication and recovery tests.

## 3. Stage and release

- [x] Use isolated SQL, endpoints, source admission and runtime storage. Run the local scripts on isolated output and validate the actual Flux discovery, GPU queue and sidecar publisher on a staging copy. Validate indexed text where the staging host supports it.
- [x] Run one ASR workload at a time, retain both originals, and verify the user-selected fresh wording and staged replacement bytes.
- [ ] Replace only the two explicitly selected sidecars after checking their current bytes and recording identity.
- [ ] Obtain technical review and present the exact production target, deployment plan, bounded live validation and rollback for user approval. Issue the normal incremental deployment only after approval.
- [ ] After live verification, squash-merge, verify main, and remove task-owned branch and worktree. Skip full closeout.
