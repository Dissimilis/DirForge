namespace DirForge.Services;

public static class DirectoryReadinessHelper
{
    public static bool IsDirectoryReadable(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
