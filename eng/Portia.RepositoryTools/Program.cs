using System;

namespace Cntryl.Portia.RepositoryTools;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "verify-stable-dependencies" => DependencyVerifier.Verify(args),
                "fix-application-structure" => ApplicationStructureCommand.RunAsync(args).GetAwaiter().GetResult(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown repository-tool command: {command}");
        PrintUsage();
        return 2;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Portia repository tools:");
        Console.WriteLine("  verify-stable-dependencies [--root <repository-root>]");
        Console.WriteLine("  fix-application-structure --solution <path.sln|path.slnx>");
    }
}
