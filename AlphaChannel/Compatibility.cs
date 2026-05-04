using Microsoft.Win32;
class Compatibility
{
    public static bool IsRunningUnderWine()
    {
        // Wine sets this registry key
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Wine");
            return key != null;
        }
        catch
        {
            return false;
        }
    }
}