using System.Text.RegularExpressions;

namespace AnchorTemplateExtractor;

public sealed class AnchorExtractionEngine
{
    public List<ExtractedField> Extract(ExtractionPlan plan, string realText)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var normalizedText = TextNormalizer.Normalize(realText);
        var results = new List<ExtractedField>(plan.Rules.Count);
        var offset = 0;

        foreach (var rule in plan.Rules)
        {
            var match = rule.Regex.Match(normalizedText, offset);
            if (!match.Success || !match.Groups[rule.GroupName].Success)
            {
                var anchorFallback = TryAnchorSpanFallback(rule, normalizedText, offset);
                if (anchorFallback is not null)
                {
                    results.Add(anchorFallback);
                    offset = Math.Max(anchorFallback.EndIndex, offset);
                    continue;
                }

                match = TryFallback(rule, normalizedText, offset);
            }

            if (match is null || !match.Groups[rule.GroupName].Success)
            {
                results.Add(new ExtractedField
                {
                    FieldId = rule.Field.Id,
                    FieldKey = rule.Field.Key,
                    OccurrenceIndex = rule.Field.OccurrenceIndex,
                    Type = rule.Field.Type,
                    Value = null,
                    StartIndex = -1,
                    EndIndex = -1,
                    Confidence = 0,
                    Missing = true,
                    Notes = "Missing"
                });

                continue;
            }

            var group = match.Groups[rule.GroupName];
            var cleaned = CleanValue(group.Value);
            var isValid = FieldValidators.IsValid(rule.Field.Type, cleaned);

            results.Add(new ExtractedField
            {
                FieldId = rule.Field.Id,
                FieldKey = rule.Field.Key,
                OccurrenceIndex = rule.Field.OccurrenceIndex,
                Type = rule.Field.Type,
                Value = cleaned,
                StartIndex = group.Index,
                EndIndex = group.Index + group.Length,
                Confidence = isValid ? 0.9 : 0.4,
                Missing = false,
                Notes = isValid ? null : "LowConfidence"
            });

            offset = Math.Max(group.Index + group.Length, offset);
        }

        return results;
    }

    private static ExtractedField? TryAnchorSpanFallback(ExtractionRule rule, string text, int offset)
    {
        var anchorText = NormalizeForAnchorSearch(text);
        var leftAnchor = NormalizeForAnchorSearch(rule.LeftAnchorText);
        var rightAnchor = NormalizeForAnchorSearch(rule.RightAnchorText);

        var start = offset;
        if (!string.IsNullOrWhiteSpace(leftAnchor))
        {
            var leftIndex = anchorText.IndexOf(leftAnchor, offset, StringComparison.Ordinal);
            if (leftIndex < 0)
            {
                return null;
            }

            start = leftIndex + leftAnchor.Length;
        }

        var end = text.Length;
        if (!string.IsNullOrWhiteSpace(rightAnchor))
        {
            var rightIndex = anchorText.IndexOf(rightAnchor, start, StringComparison.Ordinal);
            end = rightIndex >= 0 ? rightIndex : FindStopPosition(text, start);
        }
        else
        {
            end = FindStopPosition(text, start);
        }

        if (end <= start)
        {
            return null;
        }

        var span = text.Substring(start, end - start);
        var cleaned = CleanValue(span);
        var focused = TryExtractValueFromSpan(rule.Field.Type, cleaned) ?? cleaned;
        var isValid = FieldValidators.IsValid(rule.Field.Type, focused);
        var isEmpty = string.IsNullOrWhiteSpace(focused);
        var treatAsMissing = isEmpty || (!isValid && IsStrictType(rule.Field.Type));

        return new ExtractedField
        {
            FieldId = rule.Field.Id,
            FieldKey = rule.Field.Key,
            OccurrenceIndex = rule.Field.OccurrenceIndex,
            Type = rule.Field.Type,
            Value = treatAsMissing ? null : focused,
            StartIndex = start,
            EndIndex = end,
            Confidence = treatAsMissing ? 0 : (isValid ? 0.8 : 0.4),
            Missing = treatAsMissing,
            Notes = treatAsMissing ? "Missing" : (isValid ? null : "LowConfidence")
        };
    }

    private static Match? TryFallback(ExtractionRule rule, string text, int offset)
    {
        if (rule.AfterOnlyRegex is not null)
        {
            var afterMatch = rule.AfterOnlyRegex.Match(text, offset);
            if (afterMatch.Success && afterMatch.Groups[rule.GroupName].Success)
            {
                return afterMatch;
            }
        }

        var globalMatch = FindBestGlobalMatch(rule.Regex, text, offset, rule.GroupName);
        if (globalMatch is not null)
        {
            return globalMatch;
        }

        if (rule.AfterOnlyRegex is not null)
        {
            return FindBestGlobalMatch(rule.AfterOnlyRegex, text, offset, rule.GroupName);
        }

        return null;
    }

    private static Match? FindBestGlobalMatch(Regex regex, string text, int offset, string groupName)
    {
        Match? firstAfter = null;
        Match? lastBefore = null;

        foreach (Match match in regex.Matches(text))
        {
            if (!match.Groups[groupName].Success)
            {
                continue;
            }

            var index = match.Groups[groupName].Index;
            if (index >= offset)
            {
                firstAfter ??= match;
            }
            else
            {
                lastBefore = match;
            }
        }

        return firstAfter ?? lastBefore;
    }

    private static string CleanValue(string value)
    {
        var normalized = TextNormalizer.Normalize(value);
        return normalized.Trim().Trim(',', ';', '.', ':');
    }

    private static int FindStopPosition(string text, int start)
    {
        var stopChars = new[] { ',', '.', ';' };
        var idx = text.IndexOfAny(stopChars, start);
        return idx >= 0 ? idx : text.Length;
    }

    private static string? TryExtractValueFromSpan(FieldType type, string span)
    {
        var pattern = type switch
        {
            FieldType.CPF => FieldPatterns.CpfPattern,
            FieldType.CNPJ => FieldPatterns.CnpjPattern,
            FieldType.CNJ => FieldPatterns.CnjPattern,
            FieldType.Date => FieldPatterns.DatePattern,
            _ => null
        };

        if (pattern is null)
        {
            return null;
        }

        var match = Regex.Match(span, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private static string NormalizeForAnchorSearch(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = TextNormalizer.Normalize(input).ToLowerInvariant();
        var chars = normalized.ToCharArray();

        for (var i = 0; i < chars.Length - 1; i++)
        {
            if (chars[i] == 'n' && (chars[i + 1] == 'º' || chars[i + 1] == '°' || chars[i + 1] == 'o'))
            {
                chars[i + 1] = 'o';
            }
        }

        var marker = "em favor d";
        var idx = new string(chars).IndexOf(marker, StringComparison.Ordinal);
        while (idx >= 0 && idx + marker.Length < chars.Length)
        {
            var next = idx + marker.Length;
            if (chars[next] == 'o' || chars[next] == 'a' || chars[next] == 'e')
            {
                chars[next] = 'e';
            }

            idx = new string(chars).IndexOf(marker, idx + marker.Length, StringComparison.Ordinal);
        }

        return new string(chars);
    }

    private static bool IsStrictType(FieldType type)
    {
        return type is FieldType.CPF or FieldType.CNPJ or FieldType.CNJ or FieldType.Date;
    }
}
