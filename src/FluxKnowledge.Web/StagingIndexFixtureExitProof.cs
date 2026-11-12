using System.Diagnostics;
using System.Text.Json;

namespace FluxKnowledge.Web;

internal static class StagingIndexFixtureExitProof
{
    internal static void Verify(string window,JsonElement fixture)
    {
        var path=Path.GetFullPath(window);
        const string prefix=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\window-";
        if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("exact-prior-window-required");
        foreach(var file in new[]{Path.Combine(path,"owned-host-started.json"),Path.Combine(path,"owned-host-settlement.json")})
            for(var part=Path.GetFullPath(file);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
                if((File.Exists(part)||Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidOperationException("linked-prior-window");
        using var start=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"owned-host-started.json")));
        using var end=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"owned-host-settlement.json")));
        Validate(start.RootElement,end.RootElement,fixture,(id,birth)=>
        {
            try{using var process=Process.GetProcessById(id);return process.HasExited || process.StartTime.ToUniversalTime()!=birth.UtcDateTime;}
            catch(ArgumentException){return true;}
        });
    }
    internal static void Validate(JsonElement start,JsonElement end,JsonElement fixture,Func<int,DateTimeOffset,bool> exited)
    {
        var id=start.GetProperty("id").GetInt32();var birth=start.GetProperty("startedUtc").GetDateTimeOffset();
        if(!start.GetProperty("assignedOwnedJob").GetBoolean() || !start.GetProperty("noBreakaway").GetBoolean() ||
            !start.GetProperty("killOnLastJobHandleClose").GetBoolean() || start.GetProperty("activeProcessLimit").GetInt32()!=1 ||
            !end.GetProperty("actualExitObserved").GetBoolean() || !end.GetProperty("ownedJobEmpty").GetBoolean() ||
            end.GetProperty("id").GetInt32()!=id || end.GetProperty("startedUtc").GetDateTimeOffset()!=birth ||
            fixture.GetProperty("hostProcessId").GetInt32()!=id || fixture.GetProperty("hostStartedUtc").GetDateTimeOffset()!=birth ||
            fixture.GetProperty("operation").GetGuid()!=start.GetProperty("operationId").GetGuid() || !exited(id,birth))
            throw new InvalidOperationException("exact-fixture-host-job-exit-unproved");
    }
}
