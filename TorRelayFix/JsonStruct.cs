
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TorRelayFix
{
    public class RelaysInfo
    {
        public string[] bridges { get; set; }
        public string bridges_published { get; set; }
        public string build_revision { get; set; }
        public Relay[] relays { get; set; }
        public string relays_published { get; set; }
        public string version { get; set; }
    }

    public class Relay
    {
        public string fingerprint { get; set; }
        public string[] or_addresses { get; set; }
    }
}
