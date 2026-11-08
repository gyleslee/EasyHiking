namespace System;

public static class LegacySpanExtensions
{
    public static int IndexOfAnyExcept(this ReadOnlySpan<char> value,ReadOnlySpan<char> allowed)
    {
        for(var i=0;i<value.Length;i++)if(allowed.IndexOf(value[i])<0)return i;
        return -1;
    }
}
public static class LegacyCharacter
{
    public static bool IsAsciiLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
