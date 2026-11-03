namespace Poker.Application
{
    /// <summary>Structural validation only. Valid text is kept exactly as entered, without normalization.</summary>
    public static class HoldemPlayerText
    {
        public static bool IsValidSingleLine(string text)
        {
            if (text == null) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // Unicode line/paragraph separators are not Char.IsControl characters.
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029') return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(c)) return false;
            }
            return true;
        }
    }
}
