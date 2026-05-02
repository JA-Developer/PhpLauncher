using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhpLauncher.ServerLoader
{
    public class ServerOptions
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string FrankenPhpPath { get; set; } = string.Empty;
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8443;
    }
}
