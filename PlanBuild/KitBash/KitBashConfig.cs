﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlanBuild.KitBash
{
    class KitBashConfig
    {
        public List<string> boxColliderPaths = new List<string>();
        public List<KitBashSourceConfig> KitBashSources = new List<KitBashSourceConfig>();

        public bool FixReferences { get; internal set; }
    }
}
