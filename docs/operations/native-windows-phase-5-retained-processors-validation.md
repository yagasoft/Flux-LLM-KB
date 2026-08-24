# Native Windows Phase 5 retained-processors validation

- Validated at (UTC): 2026-08-24T19:27:12.6001759Z
- Required migrations: 20260813103233_AddRetainedZipProcessorBranches; 20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys; 20260814144818_AddSourceProcessorForceRequests; 20260814161559_AddOperatorActionCapabilityFoundation; 20260814162746_EnforceOperatorActionCapabilityInvariants; 20260814170852_EnforceOperatorActionRequestPolicies; 20260820062157_AddRetainedCsharpCodeFacts; 20260820070404_HardenRetainedCsharpLifecycle; 20260820101021_CloseRetainedCsharpMixedOutcomes
- Schema contract: required tables and fencing triggers present
- Direct loopback GET probes: all returned 200
- Forwarded/proxy GET probes: all returned 403
- Forwarded/proxy status codes: {"/operator-actions":{"Forwarded":403,"Forwarded-For":403,"X-Forwarded-For":403,"X-Original-URL":403,"Proxy-Connection":403,"X-ProxyUser-IP":403,"X-Real-IP":403,"Via":403,"True-Client-IP":403,"CF-Connecting-IP":403},"/api/operator-actions":{"Forwarded":403,"Forwarded-For":403,"X-Forwarded-For":403,"X-Original-URL":403,"Proxy-Connection":403,"X-ProxyUser-IP":403,"X-Real-IP":403,"Via":403,"True-Client-IP":403,"CF-Connecting-IP":403},"/search/csharp-code":{"Forwarded":403,"Forwarded-For":403,"X-Forwarded-For":403,"X-Original-URL":403,"Proxy-Connection":403,"X-ProxyUser-IP":403,"X-Real-IP":403,"Via":403,"True-Client-IP":403,"CF-Connecting-IP":403},"/api/local/retained-csharp-code?query={no-match-token}":{"Forwarded":403,"Forwarded-For":403,"X-Forwarded-For":403,"X-Original-URL":403,"Proxy-Connection":403,"X-ProxyUser-IP":403,"X-Real-IP":403,"Via":403,"True-Client-IP":403,"CF-Connecting-IP":403}}
- Outlook host activation: false
- Validation operations: SQL metadata SELECT and HTTP GET only
