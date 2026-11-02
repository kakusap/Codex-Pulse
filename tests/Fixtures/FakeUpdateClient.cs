using System;
using System.IO;
using System.Reflection;
using System.Threading;
[assembly: AssemblyVersion("0.4.4.0")]
internal static class FakeUpdateClient {
    static int Main(string[] args) {
        if(args.Length==3 && args[0]=="--wait") {
            File.WriteAllText(args[1],"ready");
            for(int i=0;i<600 && !File.Exists(args[2]);i++) Thread.Sleep(50);
            return File.Exists(args[2])?0:1;
        }
        if(args.Length==2 && args[0]=="--mark") {
#if UPDATED
            File.WriteAllText(args[1],"new");
#else
            File.WriteAllText(args[1],"old");
#endif
            return 0;
        }
        return 2;
    }
}
