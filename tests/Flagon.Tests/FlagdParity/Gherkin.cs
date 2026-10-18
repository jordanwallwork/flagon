namespace Flagon.Tests.FlagdParity;

/// <summary>One concrete scenario run: a plain scenario, or one Examples row of a scenario outline with its values substituted.</summary>
/// <param name="Id">Stable identity: <c>&lt;feature path&gt;:&lt;line&gt;</c> of the scenario (plain) or of the Examples row (outline).</param>
/// <param name="Scenario">The scenario name.</param>
/// <param name="Row">The Examples row values (empty for a plain scenario).</param>
/// <param name="Tags">Feature, scenario and Examples tags, e.g. <c>@fractional-v2</c>.</param>
/// <param name="Steps">Background then scenario steps, keyword stripped, placeholders substituted.</param>
internal sealed record GherkinCase(string Id, string Scenario, IReadOnlyList<string> Row, IReadOnlySet<string> Tags, IReadOnlyList<string> Steps);

/// <summary>
/// A deliberately small Gherkin reader, enough for the flagd-testbed feature files: tags, Feature, Background, Scenario,
/// Scenario Outline + Examples tables, steps and comments. Step data tables are kept as an unsupported marker step.
/// </summary>
internal static class Gherkin
{
    private static readonly string[] StepKeywords = ["Given ", "When ", "Then ", "And ", "But ", "* "];

    public static IReadOnlyList<GherkinCase> Parse(string path, string text)
    {
        var cases = new List<GherkinCase>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var pendingTags = new HashSet<string>(StringComparer.Ordinal);
        var featureTags = new HashSet<string>(StringComparer.Ordinal);
        var background = new List<string>();
        List<string>? currentSteps = null;

        string? scenario = null;
        int scenarioLine = 0;
        HashSet<string> scenarioTags = [];
        var outline = false;
        var scenarioSteps = new List<string>();

        HashSet<string>? examplesTags = null;
        string[]? header = null;

        void FlushPlainScenario()
        {
            if (scenario is not null && !outline)
            {
                cases.Add(new GherkinCase($"{path}:{scenarioLine}", scenario, [], Union(featureTags, scenarioTags), [.. background, .. scenarioSteps]));
            }

            scenario = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('@'))
            {
                foreach (var tag in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(t => t.StartsWith('@')))
                {
                    pendingTags.Add(tag);
                }

                continue;
            }

            if (TryKeyword(line, "Feature:", out _))
            {
                featureTags = [.. pendingTags];
                pendingTags.Clear();
                continue;
            }

            if (TryKeyword(line, "Background:", out _))
            {
                FlushPlainScenario();
                currentSteps = background;
                continue;
            }

            if (TryKeyword(line, "Scenario Outline:", out var name) || TryKeyword(line, "Scenario Template:", out name) ||
                TryKeyword(line, "Scenario:", out name) || TryKeyword(line, "Example:", out name))
            {
                FlushPlainScenario();
                scenario = name;
                scenarioLine = lineNumber;
                scenarioTags = [.. pendingTags];
                pendingTags.Clear();
                outline = line.StartsWith("Scenario Outline:", StringComparison.Ordinal) || line.StartsWith("Scenario Template:", StringComparison.Ordinal);
                scenarioSteps = [];
                currentSteps = scenarioSteps;
                examplesTags = null;
                header = null;
                continue;
            }

            if (TryKeyword(line, "Examples:", out _) || TryKeyword(line, "Scenarios:", out _))
            {
                examplesTags = [.. pendingTags];
                pendingTags.Clear();
                header = null;
                currentSteps = null;
                continue;
            }

            if (line.StartsWith('|'))
            {
                var cells = Cells(line);
                if (examplesTags is not null && scenario is not null)
                {
                    if (header is null)
                    {
                        header = cells;
                        continue;
                    }

                    var values = header.Select((h, c) => (h, v: c < cells.Length ? cells[c] : string.Empty)).ToList();
                    var steps = background.Concat(scenarioSteps).Select(s => values.Aggregate(s, (acc, kv) => acc.Replace($"<{kv.h}>", kv.v, StringComparison.Ordinal))).ToList();
                    cases.Add(new GherkinCase($"{path}:{lineNumber}", scenario, cells, Union(featureTags, scenarioTags, examplesTags), steps));
                }
                else
                {
                    // A step data table (e.g. the metadata assertions). Not interpreted: mark the case unsupported.
                    currentSteps?.Add("<data table>");
                }

                continue;
            }

            var keyword = StepKeywords.FirstOrDefault(k => line.StartsWith(k, StringComparison.Ordinal));
            if (keyword is not null)
            {
                currentSteps?.Add(line[keyword.Length..].Trim());
                continue;
            }

            throw new FormatException($"{path}:{lineNumber}: unrecognised Gherkin line '{line}'.");
        }

        FlushPlainScenario();
        return cases;
    }

    private static bool TryKeyword(string line, string keyword, out string rest)
    {
        if (line.StartsWith(keyword, StringComparison.Ordinal))
        {
            rest = line[keyword.Length..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }

    private static HashSet<string> Union(params HashSet<string>[] sets) =>
        sets.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);

    // Splits "| a | b\|c |" into cells, applying Gherkin's table escapes (\| \\ \n).
    private static string[] Cells(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\\' && i + 1 < line.Length && line[i + 1] is '|' or '\\' or 'n')
            {
                cell.Append(line[i + 1] == 'n' ? '\n' : line[i + 1]);
                i++;
            }
            else if (ch == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(ch);
            }
        }

        return [.. cells];
    }
}
