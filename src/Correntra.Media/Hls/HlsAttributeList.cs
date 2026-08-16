namespace Correntra.Media.Hls;

internal static class HlsAttributeList
{
    public static IReadOnlyDictionary<string, string> Parse(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        while (position < value.Length)
        {
            SkipWhitespaceAndCommas(value, ref position);
            if (position >= value.Length)
            {
                break;
            }

            int equals = value.IndexOf('=', position);
            if (equals < 0)
            {
                break;
            }

            string key = value[position..equals].Trim();
            position = equals + 1;

            string parsed;
            if (position < value.Length && value[position] == '"')
            {
                position++;
                int start = position;
                while (position < value.Length && value[position] != '"')
                {
                    position++;
                }

                parsed = value[start..Math.Min(position, value.Length)];
                if (position < value.Length)
                {
                    position++;
                }
            }
            else
            {
                int comma = value.IndexOf(',', position);
                int end = comma < 0 ? value.Length : comma;
                parsed = value[position..end].Trim();
                position = end;
            }

            if (key.Length > 0)
            {
                result[key] = parsed;
            }
        }

        return result;
    }

    private static void SkipWhitespaceAndCommas(string value, ref int position)
    {
        while (position < value.Length && (char.IsWhiteSpace(value[position]) || value[position] == ','))
        {
            position++;
        }
    }
}

