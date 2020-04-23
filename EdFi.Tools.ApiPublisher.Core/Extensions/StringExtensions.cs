namespace EdFi.Tools.ApiPublisher.Core.Extensions
{
    public static class StringExtensions
    {
        public static string EnsureSuffixApplied(this string text, string suffix)
        {
            if (string.IsNullOrEmpty(text))
            {
                return suffix;
            }
            
            if (text.EndsWith(suffix))
            {
                return text;
            }

            return text + suffix;
        }
        
        public static bool TryTrimSuffix(this string text, string suffix, out string trimmedText)
        {
            trimmedText = null;

            if (text == null)
            {
                return false;
            }

            int pos = text.LastIndexOf(suffix);

            if (pos < 0)
            {
                return false;
            }

            if (text.Length - pos == suffix.Length)
            {
                trimmedText = text.Substring(0, pos);
                return true;
            }

            return false;
        }

        public static string TrimSuffix(this string text, string suffix)
        {
            string trimmedText;

            if (TryTrimSuffix(text, suffix, out trimmedText))
            {
                return trimmedText;
            }

            return text;
        }
    }
}