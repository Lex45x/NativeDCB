using System.Globalization;

namespace NativeDCB.Cli.Arguments;

internal sealed class CliArguments
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.Ordinal)
    {
        "allow-incompatible",
        "command-stdin",
        "events-stdin",
        "help",
        "ndl-stdin",
        "plan-stdin",
        "query-stdin",
        "schema-stdin",
        "signature-stdin",
        "source-stdin"
    };

    private readonly Dictionary<string, List<string>> _options;

    private CliArguments(List<string> positionals, Dictionary<string, List<string>> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }

    public static CliArguments Parse(string[] arguments)
    {
        List<string> positionals = [];
        Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
        bool optionsEnded = false;

        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            string option = argument[2..];
            int equals = option.IndexOf(value: '=', StringComparison.Ordinal);
            string name = equals >= 0 ? option[..equals] : option;
            if (name.Length == 0)
            {
                throw new CliUsageException("Option name cannot be empty.");
            }

            string value;
            if (equals >= 0)
            {
                value = option[(equals + 1)..];
            }
            else if (BooleanOptions.Contains(name))
            {
                value = "true";
            }
            else
            {
                if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CliUsageException($"Option --{name} requires a value.");
                }

                value = arguments[index];
            }

            if (!options.TryGetValue(name, out List<string>? values))
            {
                values = [];
                options.Add(name, values);
            }

            values.Add(value);
        }

        return new CliArguments(positionals, options);
    }

    public bool Has(string name)
    {
        return _options.ContainsKey(name);
    }

    public bool Flag(string name)
    {
        string? value = Optional(name);
        if (value is null)
        {
            return false;
        }

        return bool.TryParse(value, out bool result)
            ? result
            : throw new CliUsageException($"Option --{name} must be true or false.");
    }

    public string Required(string name)
    {
        string? value = Optional(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CliUsageException($"Missing required option --{name}.");
    }

    public string? Optional(string name)
    {
        if (!_options.TryGetValue(name, out List<string>? values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new CliUsageException($"Option --{name} may be specified only once.");
        }

        return values[index: 0];
    }

    public IReadOnlyList<string> Many(string name)
    {
        return _options.TryGetValue(name, out List<string>? values) ? values : [];
    }

    public long Int64(string name, long defaultValue, bool nonNegative = false)
    {
        string? text = Optional(name);
        long value = text is null
            ? defaultValue
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : throw new CliUsageException($"Option --{name} must be a 64-bit integer.");
        if (nonNegative && value < 0)
        {
            throw new CliUsageException($"Option --{name} cannot be negative.");
        }

        return value;
    }

    public long? OptionalInt64(string name, bool nonNegative = false)
    {
        string? text = Optional(name);
        if (text is null)
        {
            return null;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            throw new CliUsageException($"Option --{name} must be a 64-bit integer.");
        }

        if (nonNegative && value < 0)
        {
            throw new CliUsageException($"Option --{name} cannot be negative.");
        }

        return value;
    }

    public uint RequiredUInt32(string name)
    {
        string text = Required(name);
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
            ? value
            : throw new CliUsageException($"Option --{name} must be an unsigned 32-bit integer.");
    }

    public uint? OptionalUInt32(string name)
    {
        string? text = Optional(name);
        if (text is null)
        {
            return null;
        }

        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value == 0)
        {
            throw new CliUsageException($"Option --{name} must be a positive 32-bit integer.");
        }

        return value;
    }

    public void EnsureAllowed(params string[] commandOptions)
    {
        HashSet<string> allowed = new(commandOptions, StringComparer.Ordinal)
        {
            "access-token-file", "api-key-file", "help", "server"
        };
        foreach (string option in _options.Keys)
        {
            if (!allowed.Contains(option))
            {
                throw new CliUsageException($"Unknown option --{option} for this command.");
            }
        }
    }
}