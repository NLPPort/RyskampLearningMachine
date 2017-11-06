﻿using RLM.Models.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RLM.Models
{
    public class LoadRnetworkResult
    {
        public bool Loaded { get; set; } = false;
        public long CurrentNetworkId { get; set; }
        public string CurrentNetworkName { get; set; }
        public long CaseOrder { get; set; }
        public int SessionCount { get; set; }
        public IEnumerable<RlmIO> Inputs { get; set; }
        public IEnumerable<RlmIO> Outputs { get; set; }
        public IDictionary<long, RlmInputMomentum> InputMomentums { get; set; }
    }
}
