namespace FluxKnowledge.Web;

internal static class StagingIndexWriteGate
{
    internal static bool Allowed(Guid controller,string? supplied,string method,string path,bool normalRuntime=false)
    {
        if(controller==Guid.Empty || !Guid.TryParse(supplied,out var actual) || actual!=controller)return false;
        if(normalRuntime) return method switch
        {
            "GET" => path is "/staging" or "/staging/gpu-trial/receipts" or "/staging/search-phases",
            "POST" => path is "/api/v1/corpus/search" or "/api/v1/corpus/read" or "/mcp" or "/staging/query-embedding" or
                "/staging/indexing/prepare" or "/staging/indexing/prepare-publication" or "/staging/indexing/start" or
                "/staging/indexing/publish" or "/staging/indexing/stop" or "/staging/gpu-trial/stop",
            _ => false
        };
        return method switch
        {
            "GET"=>path is "/staging" or "/staging/source-diagnostics" or "/staging/gpu-trial/receipts",
            "POST"=>path is "/staging/query-embedding" or "/staging/expanded-gpu" or "/api/v1/corpus/read" or
                "/staging/indexing/prepare" or "/staging/indexing/prepare-publication" or "/staging/indexing/start" or
                "/staging/indexing/publish" or "/staging/indexing/stop" or "/staging/gpu-trial/stop",
            _=>false
        };
    }
}
