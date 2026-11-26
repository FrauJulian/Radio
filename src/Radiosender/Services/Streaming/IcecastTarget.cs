using System.Text;
using Radiosender.Services.Settings;

namespace Radiosender.Services.Streaming;

public record IcecastTarget(string Host, int Port, string Mount, string User, string Password)
{
    public string Path => Mount.StartsWith('/') ? Mount : "/" + Mount;

    public static IcecastTarget From(AppSettings settings) => new(
        settings.LautFmHost.Trim(), settings.LautFmPort, settings.LautFmMount.Trim(), settings.LautFmUser.Trim(), settings.LautFmPassword);

    public string Credentials => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    // Security: keeps the password out of logs and debugger views.
    public override string ToString() => $"{User}@{Host}:{Port}{Path}";
}
