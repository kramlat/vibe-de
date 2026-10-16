using Vde.Terminal;

namespace VTerminal;

// vterminal [-p PROFILE] [--working-directory DIR] [--title TEXT] [--hold] [-e | --] PROGRAM [ARGS...]
public sealed record Options(string? Profile, string? WorkingDirectory, string? Title, bool Hold, IReadOnlyList<string> Command, string? Error = null)
{
    public const string Usage = """
        usage: vterminal [options] [-e PROGRAM [ARGS...]]

          -p, --profile NAME       start this shell profile (PowerShell, bash, ...); see the Terminal settings
          -e, --, -x PROGRAM ...   run this program instead of a shell (the rest of the line is its arguments)
          --working-directory DIR  start in this folder
          --title TEXT             the window's title until the program sets its own
          --hold                   keep the tab open when the program ends
          -h, --help

        """;

    public static Options Parse(IReadOnlyList<string> args)
    {
        string? profile = null, dir = null, title = null;
        var hold = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-e" or "--" or "-x" or "--command":
                    return new Options(profile, dir, title, hold, args.Skip(i + 1).ToList(), i + 1 >= args.Count ? $"{args[i]} needs a program" : null);
                case "-p" or "--profile" when i + 1 < args.Count: profile = args[++i]; break;
                case "--working-directory" when i + 1 < args.Count: dir = args[++i]; break;
                case "--title" or "-T" when i + 1 < args.Count: title = args[++i]; break;
                case "--hold": hold = true; break;
                case "-h" or "--help": return new Options(null, null, null, false, [], "help");
                default: return new Options(profile, dir, title, hold, [], $"unknown option {args[i]}");
            }
        }
        return new Options(profile, dir, title, hold, []);
    }

    public static string Name(IReadOnlyList<string> command) => Path.GetFileName(command[0]);
}
