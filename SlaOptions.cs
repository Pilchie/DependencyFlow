﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DependencyFlow
{
    public class SlaOptions
    {
        public IDictionary<string, Sla> Repositories { get; } =
            new Dictionary<string, Sla>
            {
                { "[Default]", new Sla { FailUnconsumedBuildAge = 7, WarningUnconsumedBuildAge = 5 } },
            };

        public Sla GetForRepo(string repoShortName)
        {
            if (!Repositories.TryGetValue("dotnet/" + repoShortName, out var value))
            {
                value = Repositories["[Default]"];
            }

            return value;
        }
    }

    [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
    public class Sla
    {
        public int WarningUnconsumedBuildAge { get; set; }
        public int FailUnconsumedBuildAge { get; set; }

        private string GetDebuggerDisplay()
             => $"{nameof(Sla)}(Warn: {WarningUnconsumedBuildAge}, Fail: {FailUnconsumedBuildAge})";
    }
}
