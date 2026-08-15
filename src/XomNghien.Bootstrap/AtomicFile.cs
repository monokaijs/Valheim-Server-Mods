using System.IO;

namespace XomNghien.Bootstrap;

internal static class AtomicFile
{
    public static void Replace(string temporary, string target)
    {
        if (File.Exists(target))
        {
            var backup = target + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Replace(temporary, target, backup);
            File.Delete(backup);
        }
        else
        {
            File.Move(temporary, target);
        }
    }
}
