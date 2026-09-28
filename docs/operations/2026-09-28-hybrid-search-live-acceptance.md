# Hybrid search live acceptance, 28 September 2026

The corrected forward patch completed the 35-input coherent-passage rebuild:
3,596 passages, no failed embedding or publication jobs, and a validated active
generation. After the same-release recovery restarted the web host, live and ready
probes returned HTTP 200, index health reported the new generation healthy, and
the deployment-validation hold was released. The recovery reused the committed
operation and checkpoints; it did not migrate or reset the projection again.

Activation is not search acceptance. A nine-call public-root check across REST,
CLI and MCP found no query for which all three interfaces returned the same
healthy hybrid result. Some calls returned explicit timeout status at the
ten-second request deadline with no hits; later calls returned either lexical
fallback or trained-reranker hybrid results. Citation reads matched the exact
passage when a hit was returned. The frozen 96-question English quality run was
held back because inconsistent execution would confound its relevance score.

An opt-in EventPipe trace of a successful 50-passage search measured 9.954 seconds
end to end. Embedding load, inference and unload took 3.379, 0.457 and 0.191
seconds; reranker load, inference and unload took 3.271, 2.096 and 0.191 seconds.
Hashing one already cached 2.27 GB model data file independently took 2.224
seconds. A separate 24-sample live GPU observation rose from about 2.4 to 4.8
GiB during a search and returned to baseline. These are individual observations,
not p95 estimates or a proof that residency would meet the two-second target.

Next, retain only the exact hash-verified model-file handles for the web process,
so repeated requests do not hash the same protected bytes. Keep each ONNX GPU
session inside its existing scheduler-owned request and dispose it before capacity
release. Recheck cross-surface parity, full-search latency, the frozen English
quality set and OCR waiting after deployment of that bounded change. Model
residency remains conditional on a separate warm full-search and memory/OCR
experiment and an independently reviewed ownership/release/recovery design.
