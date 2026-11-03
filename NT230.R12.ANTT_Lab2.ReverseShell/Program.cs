using System;
using System.ServiceProcess;

namespace NT230.R12.ANTT_Lab2.ReverseShell
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            args = args ?? new string[0];

            if (args.Length == 1 &&
                string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunConsoleMode("SELF-TEST", () => SelfTestRunner.RunAsync());
            }

            if (args.Length == 4 &&
                string.Equals(args[0], "--listener", StringComparison.OrdinalIgnoreCase))
            {
                return RunConsoleMode(
                    "LISTENER",
                    () => ListenerConsole.RunAsync(args[1], args[2], args[3]));
            }

            if (args.Length == 1 &&
                (string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "/?", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return 0;
            }

            if (args.Length != 0)
            {
                Console.Error.WriteLine("Invalid arguments.");
                PrintUsage();
                return 2;
            }

            ServiceBase.Run(new ServiceBase[]
            {
                new ReverseShellService()
            });

            return 0;
        }

        private static int RunConsoleMode(string mode, Func<System.Threading.Tasks.Task<int>> action)
        {
            try
            {
                return action().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                string detail = string.Equals(
                    mode,
                    "SELF-TEST",
                    StringComparison.Ordinal)
                    ? exception.ToString()
                    : exception.Message;
                Console.Error.WriteLine(mode + " FAIL: " + detail);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("NT230.R12.ANTT_Lab2.ReverseShell - Lab 2.2");
            Console.WriteLine("Usage:");
            Console.WriteLine("  NT230.R12.ANTT_Lab2.ReverseShell.exe --self-test");
            Console.WriteLine(
                "  NT230.R12.ANTT_Lab2.ReverseShell.exe --listener <bindIp> <port> <sharedToken>");
            Console.WriteLine("  NT230.R12.ANTT_Lab2.ReverseShell.exe --help");
            Console.WriteLine("  NT230.R12.ANTT_Lab2.ReverseShell.exe              Run as Windows Service");
            Console.WriteLine();
            Console.WriteLine(
                "Listener bindIp must be localhost or a literal loopback/RFC1918/private IP.");
            Console.WriteLine(
                "Interactive commands: help, hostname, whoami, pwd, dir, echo <text>, time, exit.");
            Console.WriteLine(
                "The listener and service mutually authenticate with nonce-HMAC-SHA256.");
        }
    }
}
