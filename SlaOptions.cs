using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DependencyFlow
{
    public class SlaOptions
    {
        public Dictionary<string, Sla> Repositories { get; set;  }

        public Sla GetForRepo(string repoShortName)
        {
            if (!Repositories.TryGetValue("dotnet/" + repoShortName, out var value))
            {
                value = Repositories["[Default]"];
            }

            return value;
        }
    }

    public class Sla
    {
        public int WarningUnconsumedCommitAge { get; set; }
        public int FailUnconsumedCommitAge { get; set; }
    }
}
