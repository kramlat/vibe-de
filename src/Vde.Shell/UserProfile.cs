namespace Vde.Shell;

// Who is logged in, as the start menu shows them: the full name from the account (the first comma-separated field of
// the passwd entry's GECOS, which is what "Full name" in the user manager sets) and the account picture.
public sealed class UserProfile
{
    private readonly Func<string, string?> _env;
    private readonly string _passwd;
    private readonly string _accountPictures;

    public UserProfile(Func<string, string?>? env = null, string passwdPath = "/etc/passwd", string accountPictures = "/var/lib/AccountsService/icons")
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _passwd = passwdPath;
        _accountPictures = accountPictures;
    }

    public string UserName => _env("USER") is { Length: > 0 } u ? u : Environment.UserName;

    public string DisplayName
    {
        get
        {
            try
            {
                var prefix = UserName + ":";
                var line = File.ReadLines(_passwd).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
                var fields = line?.Split(':');
                if (fields is { Length: > 4 } && fields[4].Split(',')[0].Trim() is { Length: > 0 } full)
                    return full;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return UserName;
        }
    }

    // The first letter of the name, for the round badge shown when there is no picture.
    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";

    // ~/.face (the long-standing convention), ~/.face.icon, or the picture the accounts service holds; null when none exists.
    public string? PicturePath
    {
        get
        {
            var home = _env("HOME");
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(home)) { candidates.Add(Path.Combine(home, ".face")); candidates.Add(Path.Combine(home, ".face.icon")); }
            candidates.Add(Path.Combine(_accountPictures, UserName));
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
