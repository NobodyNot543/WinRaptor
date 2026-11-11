using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WinPulse;

/// <summary>Nombre y foto de la cuenta de Windows con la que se ha iniciado sesión.</summary>
public static class UserInfo
{
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
    const int NameDisplay = 3;

    public static string DisplayName()
    {
        try
        {
            var sb = new StringBuilder(256);
            uint n = 256;
            if (GetUserNameEx(NameDisplay, sb, ref n) && sb.Length > 0) return sb.ToString().Trim();
        }
        catch { }
        return Environment.UserName;
    }

    public static string FirstName(string display) =>
        display.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? display;

    public static string Initials(string display)
    {
        var parts = display.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                           .Where(p => char.IsLetter(p[0])).ToList();
        if (parts.Count == 0) return "?";
        return (parts.Count == 1 ? parts[0][..1] : $"{parts[0][0]}{parts[^1][0]}").ToUpperInvariant();
    }

    /// <summary>Ruta de la foto de la cuenta (la que se ve en el menú Inicio), o null si no tiene.</summary>
    public static string? PicturePath()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return null;
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            if (k is null) return null;
            foreach (var v in new[] { "Image192", "Image208", "Image240", "Image424", "Image448", "Image96" })
                if (k.GetValue(v) is string p && File.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    public static string Greeting() => DateTime.Now.Hour switch
    {
        >= 6 and < 12 => "Buenos días",
        >= 12 and < 20 => "Buenas tardes",
        _ => "Buenas noches"
    };
}
