# GPU model warming implementation plan

> Use the existing shared scheduler, Windows Job containment and offline model store. The approved GPU queue rules continue to apply throughout.

**Goal**. Retain one compatible GPU model for up to two minutes after its most recent completed job and admit interactive search at the next free slot.

**Architecture**. Give idle resident capacity its own durable identity. Commit per-job completion independently from native release. Bind each reuse to the exact model, settings, phase, resident generation and a fresh scheduler execution. Free capacity after verified eviction. An expired deadline or a missing heartbeat requests eviction and carries no release authority.

- [x] Add a bounded, contained request protocol to the OCR and transcription helpers. Each command and acknowledgement carries a fresh execution identity. Validate the protocol and native shutdown with synthetic children.
- [x] Persist the resident owner, exact model identity and expiration. Cover stale versions, refused physical release and the schema 83 and 84 deployment drains.
- [x] Select the winning eligible lane first. Exercise exact warm selection beyond a 12-job prefix, preferred interactive admission after eviction and refusal of a foreign process.
- [x] Bind both document runtimes to the resident helper. Cover detached settlement and source withdrawal in disposable SQL and run the contained native trials against cached models.
- [x] Separate the ONNX session lifetime from each job's borrow. Use the actual embedding or rank phase and active precision. Staging reused the same native allocation, evicted it for an incompatible model and confirmed release of the successor after 120.17 seconds idle.
- [x] Qualify the build with synthetic and separate, bounded native tests. The real runs covered compatible reuse, wall time, eviction and physical release. Disposable SQL verified lost acknowledgement recovery and cleanup-only settlement after watchdog expiry.
- [x] Obtain independent review and approval, deploy the reviewed build, and verify live health and GPU scheduling. Release `c8aaee20` applied schema 84. Both live searches returned results with durable interactive GPU completion and release receipts.
