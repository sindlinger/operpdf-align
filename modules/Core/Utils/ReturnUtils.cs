using System;

namespace Obj.Utils
{
    public static class ReturnUtils
    {
        public static bool Enabled { get; private set; }

        public static void Enable()
        {
            Enabled = true;
        }

        public static bool IsEnabled()
        {
            return Enabled;
        }
    }
}
