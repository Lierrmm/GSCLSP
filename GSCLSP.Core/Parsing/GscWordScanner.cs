namespace GSCLSP.Core.Parsing;

public static class GscWordScanner
{
    // Characters that can be part of a GSC function call or path
    private static readonly char[] GscIdentifierChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_\\:".ToCharArray();

    public static string GetFullIdentifierAt(string line, int characterIndex)
    {
        if (string.IsNullOrEmpty(line)) return "";

        // Adjust index if cursor is at the end of the line
        int index = characterIndex;
        if (index >= line.Length && index > 0) index--;

        // If the current character isn't a valid part of an identifier, 
        // try checking the character immediately to the left.
        if (!GscIdentifierChars.Contains(line[index]))
        {
            if (index > 0 && GscIdentifierChars.Contains(line[index - 1]))
                index--;
            else
                return "";
        }

        int start = index;
        int end = index;

        // Expand Left
        while (start > 0 && GscIdentifierChars.Contains(line[start - 1]))
        {
            start--;
        }

        // Expand Right
        while (end < line.Length && GscIdentifierChars.Contains(line[end]))
        {
            end++;
        }

        return line.Substring(start, end - start).Trim();
    }
}