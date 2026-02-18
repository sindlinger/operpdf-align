using System;

namespace Obj.Utils
{
    public static class ShowUtils
    {
        public static bool IsShowEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("OBJPDF_SHOW");
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("y", StringComparison.OrdinalIgnoreCase);
        }
    }
}
