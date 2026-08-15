using System.IO;
using System.Runtime.Serialization.Json;

namespace XomNghien.Bootstrap;

internal static class Json
{
    public static T Read<T>(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream)!;
    }

    public static T ReadFile<T>(string path) => Read<T>(File.ReadAllBytes(path));

    public static byte[] Write<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return stream.ToArray();
    }

    public static void WriteFile<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(temporary, Write(value));
        AtomicFile.Replace(temporary, path);
    }
}
