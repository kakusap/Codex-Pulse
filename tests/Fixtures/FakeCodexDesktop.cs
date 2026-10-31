using System;
using System.IO;
using System.Threading;

// Isolated restart fixture; never connects to Codex or reads user settings.
class FakeCodexDesktop {
    static void Main() {
        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"starts.txt"),"started\n");
        Thread.Sleep(120000);
    }
}
