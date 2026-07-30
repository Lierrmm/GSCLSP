namespace GSCLSP.Core.Models;

public static class GscCompiledScriptDetector
{
    private const int SampleLength = 512;
    private const double BinaryRatioThreshold = 0.05;
    private const char CompiledMagicChar = (char)0x80;
    private const char ReplacementChar = (char)0xFFFD;

    public static bool IsCompiledText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (HasMagic(text))
            return true;

        int examined = Math.Min(text.Length, SampleLength);
        int binary = 0;

        for (int i = 0; i < examined; i++)
        {
            if (IsBinaryIndicator(text[i]))
                binary++;
        }

        return binary > examined * BinaryRatioThreshold;
    }

    public static bool IsCompiledFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            Span<byte> buffer = stackalloc byte[SampleLength];
            int read = stream.ReadAtLeast(buffer, SampleLength, throwOnEndOfStream: false);
            if (read <= 0)
                return false;

            var sample = buffer[..read];

            if (sample.Length >= 4 && sample[0] == 0x80 && sample[1] == (byte)'G' && sample[2] == (byte)'S' && sample[3] == (byte)'C')
                return true;

            return sample.IndexOf((byte)0) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasMagic(string text)
    {
        return text.Length >= 4
            && (text[0] == CompiledMagicChar || text[0] == ReplacementChar)
            && text[1] == 'G'
            && text[2] == 'S'
            && text[3] == 'C';
    }

    private static bool IsBinaryIndicator(char c)
    {
        if (c == '\0' || c == ReplacementChar)
            return true;

        return char.IsControl(c) && c != '\t' && c != '\r' && c != '\n';
    }
}
