using System.Globalization;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Cli;

public static class CliParser
{
    public static CliRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return HelpRequest();
        }

        if (args.Count == 1 && (args[0] is "--version" or "-v"))
        {
            return Request(CliCommands.Version);
        }

        if (args[0] is "--help" or "-h")
        {
            return HelpRequest();
        }

        var command = args[0].Trim().ToLowerInvariant();
        if (command == CliCommands.Help)
        {
            if (args.Count != 1)
            {
                throw ErrorFactory.InvalidArgument("The help command does not accept positional arguments.");
            }

            return HelpRequest();
        }

        if (command == "--version" || command == "-v")
        {
            throw ErrorFactory.InvalidArgument("The version option cannot be combined with other arguments.");
        }

        if (!CliCommands.All.Contains(command, StringComparer.Ordinal))
        {
            throw ErrorFactory.InvalidArgument($"Unknown command '{args[0]}'.");
        }

        string? root = null;
        string? store = null;
        string? kind = null;
        string? file = null;
        var assemblyRoots = new List<string>();
        var positionals = new List<string>();
        var force = false;
        var json = false;
        var compact = true;
        var human = false;
        var limit = IndexConstants.DefaultLimit;
        int? maxBytes = null;
        var depth = IndexConstants.DefaultAffectedDepth;
        var direction = "both";

        for (var index = 1; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var equalsIndex = token.IndexOf('=');
            var option = equalsIndex >= 0 ? token[..equalsIndex] : token;
            var inlineValue = equalsIndex >= 0 ? token[(equalsIndex + 1)..] : null;

            switch (option)
            {
                case "--json":
                    if (inlineValue is not null)
                    {
                        throw ErrorFactory.InvalidArgument("The --json option does not accept a value.");
                    }

                    json = true;
                    break;
                case "--compact":
                    if (inlineValue is not null)
                    {
                        throw ErrorFactory.InvalidArgument("The --compact option does not accept a value.");
                    }

                    compact = true;
                    break;
                case "--human":
                    if (inlineValue is not null)
                    {
                        throw ErrorFactory.InvalidArgument("The --human option does not accept a value.");
                    }

                    human = true;
                    break;
                case "--force":
                    if (inlineValue is not null)
                    {
                        throw ErrorFactory.InvalidArgument("The --force option does not accept a value.");
                    }

                    force = true;
                    break;
                case "--root":
                    root = ReadValue(args, ref index, option, inlineValue);
                    break;
                case "--store":
                    store = ReadValue(args, ref index, option, inlineValue);
                    break;
                case "--assembly-root":
                    assemblyRoots.Add(ReadValue(args, ref index, option, inlineValue));
                    break;
                case "--kind":
                    kind = ReadValue(args, ref index, option, inlineValue);
                    break;
                case "--file":
                    file = ReadValue(args, ref index, option, inlineValue);
                    break;
                case "--direction":
                    direction = ReadValue(args, ref index, option, inlineValue).ToLowerInvariant();
                    if (direction is not ("in" or "out" or "both"))
                    {
                        throw ErrorFactory.InvalidArgument("--direction must be one of: in, out, both.");
                    }

                    break;
                case "--limit":
                    limit = ParseLimit(ReadValue(args, ref index, option, inlineValue));
                    break;
                case "--max-bytes":
                    maxBytes = ParseMaxBytes(ReadValue(args, ref index, option, inlineValue));
                    break;
                case "--depth":
                    depth = ParseDepth(ReadValue(args, ref index, option, inlineValue));
                    break;
                case "--help":
                    throw ErrorFactory.InvalidArgument("Place --help before the command or use 'rimctx help'.");
                default:
                    throw ErrorFactory.InvalidArgument($"Unknown option '{option}'.");
            }
        }

        if (json && human)
        {
            throw ErrorFactory.InvalidArgument("--json and --human cannot be combined.");
        }

        ValidateCommandOptions(command, positionals, force, assemblyRoots, kind, direction, depth, file);
        var subject = command == CliCommands.Find
            ? string.Join(' ', positionals)
            : positionals.Count == 1 ? positionals[0] : null;

        return new CliRequest(
            command,
            subject,
            positionals.ToArray(),
            root,
            store,
            assemblyRoots,
            force,
            json,
            compact,
            human,
            limit,
            maxBytes,
            depth,
            direction,
            kind,
            file);
    }

    private static CliRequest HelpRequest() => Request(CliCommands.Help);

    private static CliRequest Request(string command) =>
        new(command, null, Array.Empty<string>(), null, null, Array.Empty<string>(), false, false, true, false, IndexConstants.DefaultLimit, null, IndexConstants.DefaultAffectedDepth, "both", null, null);

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option, string? inlineValue)
    {
        var value = inlineValue;
        if (value is null)
        {
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw ErrorFactory.InvalidArgument($"Option '{option}' requires a value.");
            }

            value = args[++index];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw ErrorFactory.InvalidArgument($"Option '{option}' requires a non-empty value.");
        }

        return value;
    }

    private static int ParseLimit(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
        {
            throw ErrorFactory.InvalidArgument("--limit must be a positive integer.");
        }

        if (limit > IndexConstants.MaximumLimit)
        {
            throw ErrorFactory.LimitExceeded($"--limit cannot exceed {IndexConstants.MaximumLimit}.");
        }

        return limit;
    }

    private static int ParseDepth(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth) || depth < 1 || depth > 8)
        {
            throw ErrorFactory.InvalidArgument("--depth must be an integer from 1 through 8.");
        }

        return depth;
    }

    private static int ParseMaxBytes(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes) || maxBytes <= 0)
        {
            throw ErrorFactory.InvalidArgument("--max-bytes must be a positive integer.");
        }

        if (maxBytes > IndexConstants.MaximumOutputBytes)
        {
            throw ErrorFactory.LimitExceeded($"--max-bytes cannot exceed {IndexConstants.MaximumOutputBytes}.");
        }

        return maxBytes;
    }

    private static void ValidateCommandOptions(
        string command,
        IReadOnlyList<string> positionals,
        bool force,
        IReadOnlyList<string> assemblyRoots,
        string? kind,
        string direction,
        int depth,
        string? file)
    {
        if (command == CliCommands.Index && positionals.Count > 0)
        {
            throw ErrorFactory.InvalidArgument("The index command does not accept positional arguments.");
        }

        if (CliCommands.IsQuery(command) &&
            positionals.Count == 0 &&
            !(command == CliCommands.Harmony && file is not null))
        {
            throw ErrorFactory.InvalidArgument($"The {command} command requires a selector.");
        }

        if (CliCommands.IsQuery(command) &&
            command != CliCommands.Affected &&
            command != CliCommands.Find &&
            !(command == CliCommands.Harmony && file is not null && positionals.Count == 0) &&
            positionals.Count != 1)
        {
            throw ErrorFactory.InvalidArgument($"The {command} command accepts exactly one selector.");
        }

        if (command is CliCommands.Version or CliCommands.Summary && positionals.Count > 0)
        {
            throw ErrorFactory.InvalidArgument($"The {command} command does not accept positional arguments.");
        }

        if (command != CliCommands.Index && (force || assemblyRoots.Count > 0))
        {
            throw ErrorFactory.InvalidArgument("--force and --assembly-root are only valid for index.");
        }

        if (command != CliCommands.Find && kind is not null)
        {
            throw ErrorFactory.InvalidArgument("--kind is only valid for find.");
        }

        if (command != CliCommands.Refs && direction != "both")
        {
            throw ErrorFactory.InvalidArgument("--direction is only valid for refs.");
        }

        if (command != CliCommands.Affected && depth != IndexConstants.DefaultAffectedDepth)
        {
            throw ErrorFactory.InvalidArgument("--depth is only valid for affected.");
        }

        if (file is not null && command != CliCommands.Harmony)
        {
            throw ErrorFactory.InvalidArgument("--file is only valid for harmony.");
        }
    }
}
