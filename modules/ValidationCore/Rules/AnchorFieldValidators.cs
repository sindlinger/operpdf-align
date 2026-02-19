namespace AnchorTemplateExtractor;

internal static class FieldValidators
{
    public static bool IsValid(FieldType type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = TextNormalizer.Normalize(value);
        var digitsOnly = new string(normalized.Where(char.IsDigit).ToArray());

        return type switch
        {
            FieldType.CPF => FieldPatterns.CpfRegex.IsMatch(normalized) || digitsOnly.Length == 11,
            FieldType.CNPJ => FieldPatterns.CnpjRegex.IsMatch(normalized) || digitsOnly.Length == 14,
            FieldType.CNJ => FieldPatterns.CnjRegex.IsMatch(normalized) || digitsOnly.Length >= 20,
            FieldType.Money => FieldPatterns.MoneyRegex.IsMatch(normalized) || normalized.Contains(','),
            FieldType.Date => FieldPatterns.DateRegex.IsMatch(normalized) || normalized.Contains(" de ") || normalized.Contains('/'),
            _ => !string.IsNullOrWhiteSpace(normalized)
        };
    }
}
