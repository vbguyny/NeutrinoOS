// NeutrinoOS Phase 4 test app 2 - Interactive console
//
// Reads lines from the console (serial line discipline) and echoes them
// until the user types "exit". Verifies Console.ReadLine through the JIT,
// the CAL, and the UART line discipline (line editing and history are
// handled by the line discipline, exercised manually).
//
// Runner interaction: send a line, expect "[interactive] echo: <line>",
// then send "exit" and expect "[interactive] bye".

using System;

namespace Phase4.Interactive;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("[interactive] type lines; 'exit' quits");
        int count = 0;
        for (; ; )
        {
            string line = Console.ReadLine();
            if (line == null)
                break; // input closed
            if (line == "exit")
            {
                Console.WriteLine("[interactive] bye");
                return 0;
            }
            count++;
            Console.WriteLine("[interactive] echo: " + line);
        }
        Console.WriteLine("[interactive] bye (eof after " + count + " lines)");
        return 0;
    }
}
