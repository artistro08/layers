using System.Globalization;
using System.Numerics;

namespace Layers.Core.Logic;

/// <summary>
/// One segment of a path figure.
/// </summary>
/// <remarks>
/// A straight line to <see cref="End"/>, or a cubic Bezier through two control points when <see cref="IsCubic"/> is
/// set. The control points are zero for a line.
/// </remarks>
/// <param name="Control1">First control point (cubic only).</param>
/// <param name="Control2">Second control point (cubic only).</param>
/// <param name="End">End point.</param>
/// <param name="IsCubic">Whether this is a cubic Bezier.</param>
public readonly record struct PathSegment(Vector2 Control1, Vector2 Control2, Vector2 End, bool IsCubic)
{
    /// <summary>Creates a line segment.</summary>
    /// <remarks>Used for <c>L</c>, <c>V</c>, <c>H</c>, and extra pairs after <c>M</c>.</remarks>
    /// <param name="end">End point.</param>
    /// <returns>The segment.</returns>
    public static PathSegment Line(Vector2 end) => new(default, default, end, false);

    /// <summary>Creates a cubic Bezier segment.</summary>
    /// <remarks>Used for <c>C</c>.</remarks>
    /// <param name="control1">First control point.</param>
    /// <param name="control2">Second control point.</param>
    /// <param name="end">End point.</param>
    /// <returns>The segment.</returns>
    public static PathSegment Cubic(Vector2 control1, Vector2 control2, Vector2 end) => new(control1, control2, end, true);
}

/// <summary>
/// One closed, filled figure of a path.
/// </summary>
/// <remarks>
/// Starts at <see cref="Start"/> and runs through <see cref="Segments"/>. Drawing closes it back to the start.
/// </remarks>
/// <param name="Start">Start point.</param>
/// <param name="Segments">Segments in order.</param>
public sealed record PathFigure(Vector2 Start, IReadOnlyList<PathSegment> Segments);

/// <summary>
/// The vendored layers glyph and a minimal SVG path parser for it.
/// </summary>
/// <remarks>
/// Segoe Fluent Icons has no filled stack-of-layers glyph, so the tray icon, menu, and HUD use Fluent System Icons'
/// <c>ic_fluent_layer_24_filled</c> (MIT, see assets/NOTICE-fluentui.txt), as Layers 1.0.3 did. The parser handles
/// exactly the subset that path uses (absolute <c>M</c>, <c>L</c>, <c>C</c>, <c>V</c>, <c>H</c>, <c>Z</c>) and
/// rejects anything else, since a loud error beats a silently misdrawn icon.
/// </remarks>
public static class PathData
{
    /// <summary>Fluent System Icons <c>ic_fluent_layer_24_filled</c>, on a 24x24 grid.</summary>
    public const string LayersGlyph = "M13.3867 3.42476L19.7519 7.66821C20.2115 7.97456 20.3356 8.59543 20.0293 9.05496C19.956 9.16481 19.8618 9.25907 19.7519 9.33231L13.3867 13.5758C12.547 14.1356 11.453 14.1356 10.6132 13.5758L4.24807 9.33231C3.78854 9.02595 3.66437 8.40509 3.97072 7.94556C4.04396 7.8357 4.13822 7.74144 4.24807 7.66821L10.6132 3.42476C11.453 2.86492 12.547 2.86492 13.3867 3.42476ZM20.0256 12.1922C19.8772 12.4296 19.6806 12.6332 19.4486 12.7899L13.3987 16.8736C12.5535 17.4441 11.4465 17.4441 10.6013 16.8736L4.55142 12.7899C3.79043 12.2762 3.49533 11.3306 3.77229 10.5003L10.6132 15.0598C11.4005 15.5847 12.4112 15.6175 13.2264 15.1582L13.3867 15.0598L20.2271 10.4998C20.4088 11.0459 20.3545 11.666 20.0256 12.1922ZM20.0256 15.4422C19.8772 15.6796 19.6806 15.8832 19.4486 16.0399L13.3987 20.1236C12.5535 20.6941 11.4465 20.6941 10.6013 20.1236L4.55142 16.0399C3.79043 15.5262 3.49533 14.5806 3.77229 13.7503L10.6132 18.3098C11.4005 18.8347 12.4112 18.8675 13.2264 18.4082L13.3867 18.3098L20.2271 13.7498C20.4088 14.2959 20.3545 14.916 20.0256 15.4422Z";

    /// <summary>The layers glyph's view box size.</summary>
    public const float LayersGlyphViewBox = 24;

    /// <summary>
    /// Parses path data into figures.
    /// </summary>
    /// <remarks>
    /// Numbers may butt against letters and against each other through a leading sign. A bare coordinate run repeats
    /// the previous command, and extra pairs after <c>M</c> are lines, per the SVG grammar.
    /// </remarks>
    /// <param name="data">Path data.</param>
    /// <returns>The figures, at least one.</returns>
    /// <exception cref="FormatException">The data is malformed or uses an unsupported command.</exception>
    public static IReadOnlyList<PathFigure> Parse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var figures  = new List<PathFigure>();
        var segments = (List<PathSegment>?)null;
        var start    = Vector2.Zero;
        var current  = Vector2.Zero;

        void EndFigure()
        {
            if (segments is not null)
            {
                figures.Add(new PathFigure(start, segments));
                segments = null;
            }
        }

        List<PathSegment> Open(char command) => segments ?? throw new FormatException($"{command} before M");

        var command = '\0';
        foreach (var (letter, numbers) in Tokenize(data))
        {
            command = letter ?? command;
            switch (command)
            {
                // Move, Then Implicit Lines
                case 'M':
                    RequireGroups(numbers, 2, command);
                    EndFigure();
                    start    = current = new Vector2(numbers[0], numbers[1]);
                    segments = [];
                    for (var i = 2; i < numbers.Count; i += 2)
                    {
                        current = new Vector2(numbers[i], numbers[i + 1]);
                        segments.Add(PathSegment.Line(current));
                    }
                    command = 'L';
                    break;

                // Lines
                case 'L':
                    RequireGroups(numbers, 2, command);
                    for (var i = 0; i < numbers.Count; i += 2)
                    {
                        current = new Vector2(numbers[i], numbers[i + 1]);
                        Open(command).Add(PathSegment.Line(current));
                    }
                    break;

                // Cubic Beziers
                case 'C':
                    RequireGroups(numbers, 6, command);
                    for (var i = 0; i < numbers.Count; i += 6)
                    {
                        current = new Vector2(numbers[i + 4], numbers[i + 5]);
                        Open(command).Add(PathSegment.Cubic(new Vector2(numbers[i], numbers[i + 1]), new Vector2(numbers[i + 2], numbers[i + 3]), current));
                    }
                    break;

                // Vertical And Horizontal Lines
                case 'V' or 'H':
                    RequireGroups(numbers, 1, command);
                    foreach (var value in numbers)
                    {
                        current = command == 'V' ? current with { Y = value } : current with { X = value };
                        Open(command).Add(PathSegment.Line(current));
                    }
                    break;

                // Close
                case 'Z':
                    if (numbers.Count != 0)
                    {
                        throw new FormatException("Z takes no coordinates");
                    }
                    EndFigure();
                    current = start;
                    break;

                case '\0':
                    throw new FormatException("path does not begin with a command");

                default:
                    throw new FormatException($"unsupported path command '{command}'");
            }
        }

        EndFigure();
        return figures.Count > 0 ? figures : throw new FormatException("path produced no figures");
    }

    private static void RequireGroups(List<float> numbers, int size, char command)
    {
        if (numbers.Count == 0 || numbers.Count % size != 0)
        {
            throw new FormatException($"{command} needs groups of {size} coordinates");
        }
    }

    private static List<(char? Letter, List<float> Numbers)> Tokenize(string data)
    {
        var tokens  = new List<(char?, List<float>)>();
        var letter  = (char?)null;
        var numbers = new List<float>();

        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];

            // Command Letter Closes The Previous Run
            if (char.IsAsciiLetter(c))
            {
                if (letter is not null || numbers.Count > 0)
                {
                    tokens.Add((letter, numbers));
                    numbers = [];
                }
                letter = c;
                i++;
                continue;
            }

            // Separators
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            // Number With Optional Sign
            var begin = i;
            if (c is '-' or '+')
            {
                i++;
            }
            while (i < data.Length && (char.IsAsciiDigit(data[i]) || data[i] == '.'))
            {
                i++;
            }

            if (!float.TryParse(data.AsSpan(begin, i - begin), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                throw new FormatException($"unexpected character '{c}' at {begin}");
            }
            numbers.Add(number);
        }

        if (letter is not null || numbers.Count > 0)
        {
            tokens.Add((letter, numbers));
        }

        return tokens;
    }
}
