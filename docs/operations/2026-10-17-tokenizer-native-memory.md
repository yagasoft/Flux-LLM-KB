# Native tokenizer memory lifetime

## Reproduced problem

The production IIS worker reached approximately 169 GiB of private committed
memory. Managed GC commitment was approximately 1.45 GiB. A bounded read-only
memory-region inventory found 4,923 private 32 MiB allocations, accounting for
153.84 GiB; all but one were aligned to 32 MiB. This pattern directed isolation
towards the mimalloc-backed tokenizer runtime. Module presence and allocation
sizes alone do not prove attribution.

An isolated process used the exact verified, cached Tokenizers.DotNet 1.4.0
runtime and BGE-M3 tokenizer. It performed no ONNX inference, SQL operation,
network request or model acquisition. With the existing process-lifetime DLL pin
held in both cases, 60 create/encode/dispose cycles on one persistent OS thread
stayed near 180 MB private memory. Running complete lifecycles on newly created
threads retained 4.38 GB after 25 cycles, despite successful disposal, departed
threads and full managed GC. The resource guard stopped that experiment.

The actual application adapter reproduced 1,260,576,768 bytes of additional
private commitment after eight departed caller lifecycles. Changing only encode
callers did not reproduce that growth; the regression therefore exercises native
initialisation and cleanup as well. These results establish a reproducible
integration lifetime defect. They do not prove a particular upstream allocator
implementation bug or explain every historical native IIS exception.

## Correction and verification contract

One lazy process-lifetime background OS thread owns tokenizer native library
loading, initialisation, encoding, cleanup and failed-construction cleanup.
The existing verified runtime pin and collectible managed contexts remain.
Each tokenizer still disposes its own native instance and protected file leases.
The synchronous public contract, token IDs, fixed model identities and offline
acquisition boundary are unchanged. ONNX execution, scheduler ownership and SQL
contracts are unaffected.

The handoff permits 16 pending operations, then blocks admission until space is
available. It never drops cleanup or creates another native owner. The owner
starts without a caller execution context, executes reentrant calls inline and
propagates individual failures without abandoning later work. A caller retains
ownership until accepted native work completes, even if its surrounding request
is cancelled.

Focused checks cover 32 departed caller lifecycles with unchanged token IDs and
a 128 MiB post-warmup retention ceiling; repeated disposal and malformed native
construction; concurrent encode/dispose; stable single-owner execution; queue
saturation, backpressure and cleanup following failure; context isolation and
reentry; existing runtime pin and CPU native cleanup protections. Real tokenizer
checks are explicit opt-in probes using verified local files; ordinary tests
remain model-free.

## Operational boundary

A controlled same-release incremental replacement first relieved the machine's
memory pressure. That replacement was temporary containment, not the correction.
The corrected release follows ordinary incremental deployment with independent
technical review, explicit user authority, GPU admission fencing and drain, exact
old-worker exit, paired compatible payload rollback and retained configuration.
There is no database migration, model replacement or ownership reset.

Live acceptance must observe real indexing progress, healthy probes, the same
worker incarnation and stable private memory/native 32 MiB allocation counts
after warmup. A bounded observation does not establish indefinite stability or
prevent unrelated native faults. Exact deployment, process and measurement
receipts remain in the machine's private operational evidence store.
