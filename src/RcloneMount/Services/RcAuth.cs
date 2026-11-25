using System;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace RcloneMount.Services
{
    // Credentials for the rclone remote-control (RC) server every child process opens on
    // 127.0.0.1. Without them rclone needs --rc-no-auth to allow operations/*, sync/* and
    // config/* — which hands those calls to any local program, other Windows users, and
    // localhost-capable web pages. One random password per app run; it lives only in this
    // process and on the child's command line (readable by the same user, who can already
    // read rclone.conf).
    internal static class RcAuth
    {
        internal const string User = "rcm";

        internal static string Pass { get; } =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static AuthenticationHeaderValue Header { get; } =
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Pass}")));

        internal static void AddServerArgs(Collection<string> args)
        {
            args.Add("--rc-user");
            args.Add(User);
            args.Add("--rc-pass");
            args.Add(Pass);
        }
    }
}
