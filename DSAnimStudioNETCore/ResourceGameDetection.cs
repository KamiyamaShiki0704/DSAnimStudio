using System;
namespace DSAnimStudio
{
    internal static class ResourceGameDetection
    {
        internal static bool HasCompatibleProjectPrefix(string path)
        {
            if (path == null) return false;
            foreach (var part in path.Replace('\\', '/').Split('/'))
            {
                if (part.Length != 2) continue;
                uint hash = 2166136261;
                foreach (char c in part) hash = unchecked((hash ^ char.ToUpperInvariant(c)) * 16777619);
                if (hash == 2027117207u) return true;
            }
            return false;
        }
    }
}
