using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>Compiles and expands literal text, fields and invariant case formatting.</summary>
public static class FontNamingTemplate
{
    /// <summary>Validates template syntax and literal path boundaries.</summary>
    /// <param name="template">The exact administrator input.</param>
    /// <param name="kind">The filename or folder context.</param>
    /// <returns>A reusable program or positioned errors.</returns>
    public static FontNamingCompilation Compile(string template, FontTemplateKind kind)
    {
        string field = kind == FontTemplateKind.FileName ? "FontFileNameTemplate" : "FontFolderTemplate";
        try
        {
            if (template is null || template.Length > 16384)
            {
                throw new NamingSyntaxException("template-too-long", 0);
            }

            int start = 0;
            int end = template.Length;
            while (start < end && char.IsWhiteSpace(template[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(template[end - 1]))
            {
                end--;
            }

            if (start == end)
            {
                return kind == FontTemplateKind.Folder
                    ? new(new FontNamingProgram(kind, []), [])
                    : new(null, [new(field, "empty-template", start)]);
            }

            int count = 0;
            var components = new List<IReadOnlyList<FontNamingPart>>();
            var parts = new List<FontNamingPart>();
            void Add(FontNamingPart part)
            {
                if (++count > 512)
                {
                    throw new NamingSyntaxException("too-many-template-parts", part.Offset);
                }

                parts.Add(part);
            }

            int position = start;
            while (position < end)
            {
                if (template[position] is '/' or '\\')
                {
                    if (kind == FontTemplateKind.FileName || parts.Count == 0)
                    {
                        throw new NamingSyntaxException("invalid-path-separator", position);
                    }

                    ValidateLiteralComponent(parts);
                    components.Add(Array.AsReadOnly(parts.ToArray()));
                    parts.Clear();
                    position++;
                }
                else if (template[position] == '{')
                {
                    int closing = position + 1;
                    while (closing < end && template[closing] != '}')
                    {
                        if (template[closing] == '{')
                        {
                            throw new NamingSyntaxException("nested-template-brace", closing);
                        }

                        closing++;
                    }

                    if (closing == end)
                    {
                        throw new NamingSyntaxException("unclosed-template-expression", end);
                    }

                    Add(ParseField(template, position + 1, closing));
                    position = closing + 1;
                }
                else
                {
                    int literalStart = position;
                    while (position < end && template[position] is not '{' and not '/' and not '\\')
                    {
                        if (template[position] is '}' or ':' || char.IsControl(template[position]))
                        {
                            throw new NamingSyntaxException("invalid-path-literal", position);
                        }

                        position++;
                    }

                    Add(new(FontNamingPartKind.Literal, template[literalStart..position], literalStart));
                }
            }

            if (parts.Count == 0)
            {
                throw new NamingSyntaxException("empty-path-component", end);
            }

            ValidateLiteralComponent(parts);
            components.Add(Array.AsReadOnly(parts.ToArray()));
            return new(new FontNamingProgram(kind, Array.AsReadOnly(components.ToArray())), []);
        }
        catch (NamingSyntaxException exception)
        {
            return new(null, [new(field, exception.Message, exception.Offset)]);
        }
    }

    /// <summary>Validates current rules for settings and new execution.</summary>
    /// <param name="rules">The immutable administrator rules.</param>
    /// <returns>All rule errors.</returns>
    public static IReadOnlyList<FontNamingError> ValidateRules(FontNamingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.FormatVersion != FontNamingRules.CurrentFormatVersion)
        {
            return [new("FormatVersion", "unsupported-rule-version", 0)];
        }

        return Compile(rules.FileNameTemplate, FontTemplateKind.FileName).Errors.Concat(Compile(rules.FolderTemplate, FontTemplateKind.Folder).Errors).ToArray();
    }

    internal static FontTemplateResult Expand(FontNamingProgram program, FontNamingFields fields)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(fields);
        var evidence = new List<FontNamingFieldEvidence>();
        var components = new List<string>();
        var selections = new List<FontNamingSelectionEvidence>();
        string suffix = string.Empty;
        string? failure = null;
        int total = 0;
        try
        {
            foreach (var parts in program.Components)
            {
                var text = new StringBuilder();
                var tail = new StringBuilder();
                bool optionalOnly = true;
                foreach (var part in parts)
                {
                    string value;
                    if (part.Kind == FontNamingPartKind.Literal)
                    {
                        value = part.Text;
                        optionalOnly = false;
                    }
                    else
                    {
                        var resolved = fields.Get(part.Text);
                        evidence.AddRange(resolved.Evidence);
                        if (part.Text is "family" or "style" or "styleSuffix" or "collectionSuffix")
                        {
                            selections.AddRange(fields.Selections);
                        }

                        if (resolved.BlocksWhenReferenced)
                        {
                            throw new NamingEvaluationException(part.Text + ":" + resolved.MissingReason);
                        }

                        optionalOnly &= FontNamingFieldCatalog.Fields.Single(item => item.Name == part.Text).MissingBehavior != "skip";
                        value = part.Kind switch
                        {
                            FontNamingPartKind.Upper => resolved.Text.ToUpperInvariant(),
                            FontNamingPartKind.Lower => resolved.Text.ToLowerInvariant(),
                            _ => resolved.Text
                        };
                    }

                    if (value.Length > 65536 - text.Length || value.Length > 1048576 - total)
                    {
                        throw new NamingEvaluationException("evaluation-limit-exceeded");
                    }

                    total += value.Length;
                    text.Append(value);
                    if (part.Kind != FontNamingPartKind.Literal && part.Text is "styleSuffix" or "collectionSuffix" or "versionSuffix")
                    {
                        tail.Append(value);
                    }
                    else
                    {
                        tail.Clear();
                    }
                }

                if (text.Length == 0 && optionalOnly && program.Kind == FontTemplateKind.Folder)
                {
                    continue;
                }

                if (text.ToString().Trim() is "." or "..")
                {
                    throw new NamingEvaluationException("path-traversal");
                }

                string safe = FontNamingComponent.Normalize(text.ToString());
                if (!FontNamingComponent.IsSafe(safe))
                {
                    throw new NamingEvaluationException("empty-or-unsafe-component");
                }

                components.Add(safe);
                if (program.Kind == FontTemplateKind.FileName)
                {
                    string actualTail = tail.ToString();
                    suffix = safe.EndsWith(actualTail, StringComparison.Ordinal) ? actualTail : string.Empty;
                }
            }
        }
        catch (NamingEvaluationException exception)
        {
            failure = exception.Message;
            components.Clear();
            suffix = string.Empty;
        }

        return new(Array.AsReadOnly(components.ToArray()), new(Array.AsReadOnly(evidence.Distinct().ToArray()), Array.AsReadOnly(selections.Distinct().ToArray())), failure)
        {
            ProtectedSuffix = suffix,
            PreserveSourceDirectory = program.Kind == FontTemplateKind.Folder && program.Components.Count == 0
        };
    }

    private static FontNamingPart ParseField(string template, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(template[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(template[end - 1]))
        {
            end--;
        }

        int position = start;
        string identifier = ReadIdentifier(template, ref position, end, "unsupported-syntax");
        SkipWhitespace(template, ref position, end);
        var kind = FontNamingPartKind.Field;
        int fieldOffset = start;
        if (position < end && template[position] == '(')
        {
            kind = identifier switch
            {
                "upper" => FontNamingPartKind.Upper,
                "lower" => FontNamingPartKind.Lower,
                _ => throw new NamingSyntaxException("unknown-function", start)
            };
            position++;
            SkipWhitespace(template, ref position, end);
            fieldOffset = position;
            identifier = ReadIdentifier(template, ref position, end, "invalid-format-argument");
            SkipWhitespace(template, ref position, end);
            if (position == end || template[position] != ')')
            {
                throw new NamingSyntaxException("invalid-format-argument", position);
            }

            position++;
            SkipWhitespace(template, ref position, end);
            if (position != end)
            {
                throw new NamingSyntaxException("invalid-format-argument", position);
            }
        }
        else if (position != end)
        {
            throw new NamingSyntaxException("unsupported-syntax", position);
        }

        if (!FontNamingFieldCatalog.Fields.Any(item => item.Name == identifier))
        {
            throw new NamingSyntaxException("unknown-field", fieldOffset);
        }

        return new(kind, identifier, fieldOffset);
    }

    private static string ReadIdentifier(string template, ref int position, int end, string error)
    {
        int start = position;
        if (position == end || !char.IsAsciiLetter(template[position]))
        {
            throw new NamingSyntaxException(error, position);
        }

        while (position < end && char.IsAsciiLetterOrDigit(template[position]))
        {
            position++;
        }

        return template[start..position];
    }

    private static void SkipWhitespace(string template, ref int position, int end)
    {
        while (position < end && char.IsWhiteSpace(template[position]))
        {
            position++;
        }
    }

    private static void ValidateLiteralComponent(List<FontNamingPart> parts)
    {
        if (parts.All(part => part.Kind == FontNamingPartKind.Literal) && string.Concat(parts.Select(part => part.Text)).Trim() is "." or "..")
        {
            throw new NamingSyntaxException("path-traversal", parts[0].Offset);
        }
    }
}
