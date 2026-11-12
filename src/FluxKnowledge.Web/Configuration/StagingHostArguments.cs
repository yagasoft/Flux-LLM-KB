namespace FluxKnowledge.Web.Configuration;

internal enum StagingHostMode { Run, Migrate, Cli, CandidateQuality, ExpandedGpu, PayloadReadiness, ExpandedGpuQualification, ExpandedGpuIndexing, IndexFixtureCleanup, NormalRuntime, CheckNormalRuntime }

internal static class StagingHostArguments
{
    internal static StagingHostMode ParseMode(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--settings-file" && !string.IsNullOrWhiteSpace(args[1]))
        {
            if (args.Length == 2) return StagingHostMode.Run;
            if (args.Length >= 5 && args[2] == "--cli") return StagingHostMode.Cli;
            if ((args.Length == 5 || args.Length == 7 && args[5] == "--continuation" && !string.IsNullOrWhiteSpace(args[6])) &&
                args[2] is "--normal-runtime" or "--check-normal-runtime" && args[3] == "--ledger-reset" && !string.IsNullOrWhiteSpace(args[4]))
                return args[2]=="--normal-runtime"?StagingHostMode.NormalRuntime:StagingHostMode.CheckNormalRuntime;
            if ((args.Length==7 || args.Length==9 && args[7]=="--legacy-plan" && !string.IsNullOrWhiteSpace(args[8])) && args[2]=="--index-fixture-cleanup" && args[3]=="--fixture-output" &&
                !string.IsNullOrWhiteSpace(args[4]) && args[5]=="--prior-window" && !string.IsNullOrWhiteSpace(args[6]))
                return StagingHostMode.IndexFixtureCleanup;
            if (args.Length == 3)
            {
                if (args[2] == "--migrate") return StagingHostMode.Migrate;
                if (args[2] == "--candidate-quality") return StagingHostMode.CandidateQuality;
                if (args[2] == "--expanded-gpu") return StagingHostMode.ExpandedGpu;
                if (args[2] == "--expanded-gpu-qualification") return StagingHostMode.ExpandedGpuQualification;
                if (args[2] == "--expanded-gpu-indexing") return StagingHostMode.ExpandedGpuIndexing;
                if (args[2] == "--payload-readiness") return StagingHostMode.PayloadReadiness;
            }
        }
        throw new ArgumentException("Use --settings-file <owned Config\\staging.json> [--migrate | --candidate-quality | --expanded-gpu | --expanded-gpu-qualification | --payload-readiness | --cli <native command>].");
    }
}
