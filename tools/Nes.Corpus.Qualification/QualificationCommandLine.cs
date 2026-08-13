using System.Globalization;

namespace Nes.Corpus.Qualification;

public sealed record QualificationOptions(
    string CorpusRoot,
    string ServerAssembly,
    QualificationBounds Bounds,
    ExpectedCohort Expected);

public sealed record CommandLineResult(
    bool IsSuccess,
    QualificationOptions? Options,
    string? ErrorCode)
{
    public static CommandLineResult Success(QualificationOptions options) => new(true, options, null);

    public static CommandLineResult Failure(string errorCode) => new(false, null, errorCode);
}

public static class QualificationCommandLine
{
    public static CommandLineResult Parse(IReadOnlyList<string> arguments)
    {
        string? corpusRoot = null;
        string? serverAssembly = null;
        var wallTimeoutSeconds = 30;
        var stagingTimeoutSeconds = 10;
        var maxImageBytes = 8 * 1024 * 1024;
        var maxFrames = 4;
        var maxInstructions = 10_000;
        var maxTraceEvents = 128;
        int? expectedTotal = null;
        var expectedMappers = new SortedDictionary<int, int>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (!TryTakeValue(arguments, ref index, out var value))
            {
                return CommandLineResult.Failure("invalid_arguments");
            }

            switch (option)
            {
                case "--root":
                    corpusRoot = value;
                    break;
                case "--server":
                    serverAssembly = value;
                    break;
                case "--wall-timeout-seconds":
                    if (!TryParsePositive(value, out wallTimeoutSeconds))
                    {
                        return CommandLineResult.Failure("invalid_wall_timeout");
                    }

                    break;
                case "--staging-timeout-seconds":
                    if (!TryParsePositive(value, out stagingTimeoutSeconds))
                    {
                        return CommandLineResult.Failure("invalid_staging_timeout");
                    }

                    break;
                case "--max-image-bytes":
                    if (!TryParsePositive(value, out maxImageBytes))
                    {
                        return CommandLineResult.Failure("invalid_image_bound");
                    }

                    break;
                case "--max-frames":
                    if (!TryParsePositive(value, out maxFrames))
                    {
                        return CommandLineResult.Failure("invalid_frame_bound");
                    }

                    break;
                case "--max-instructions":
                    if (!TryParsePositive(value, out maxInstructions))
                    {
                        return CommandLineResult.Failure("invalid_instruction_bound");
                    }

                    break;
                case "--max-trace-events":
                    if (!TryParsePositive(value, out maxTraceEvents))
                    {
                        return CommandLineResult.Failure("invalid_trace_bound");
                    }

                    break;
                case "--expected-total":
                    if (!TryParseNonNegative(value, out var total))
                    {
                        return CommandLineResult.Failure("invalid_expected_total");
                    }

                    expectedTotal = total;
                    break;
                case "--expect-mapper":
                    if (!TryParseMapperExpectation(value, out var mapper, out var count) ||
                        !expectedMappers.TryAdd(mapper, count))
                    {
                        return CommandLineResult.Failure("invalid_mapper_expectation");
                    }

                    break;
                default:
                    return CommandLineResult.Failure("unknown_option");
            }
        }

        if (string.IsNullOrWhiteSpace(corpusRoot) || string.IsNullOrWhiteSpace(serverAssembly))
        {
            return CommandLineResult.Failure("missing_required_option");
        }

        if (!expectedTotal.HasValue || expectedMappers.Count == 0 ||
            expectedMappers.Values.Sum(count => (long)count) != expectedTotal.Value)
        {
            return CommandLineResult.Failure("invalid_expected_cohort");
        }

        if (stagingTimeoutSeconds > wallTimeoutSeconds ||
            maxFrames > 600 ||
            maxInstructions > 10_000_000 ||
            maxTraceEvents > 10_000)
        {
            return CommandLineResult.Failure("invalid_bounds");
        }

        var bounds = new QualificationBounds(
            wallTimeoutSeconds,
            stagingTimeoutSeconds,
            maxImageBytes,
            maxFrames,
            maxInstructions,
            maxTraceEvents);
        return CommandLineResult.Success(new QualificationOptions(
            corpusRoot,
            serverAssembly,
            bounds,
            new ExpectedCohort(expectedTotal.Value, expectedMappers)));
    }

    private static bool TryTakeValue(IReadOnlyList<string> arguments, ref int index, out string value)
    {
        value = "";
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Count)
        {
            return false;
        }

        value = arguments[++index];
        return !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);
    }

    private static bool TryParseMapperExpectation(string value, out int mapper, out int count)
    {
        mapper = 0;
        count = 0;
        var separator = value.IndexOf('=');
        return separator > 0 && separator < value.Length - 1 &&
               TryParseNonNegative(value[..separator], out mapper) && mapper <= 0x0FFF &&
               TryParseNonNegative(value[(separator + 1)..], out count);
    }

    private static bool TryParsePositive(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryParseNonNegative(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
}
