using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RcloneMount.Services
{
    // Named-pipe IPC between an external CLI invocation (`RcloneMount.exe --mount X`)
    // and the resident tray process. The tray hosts the server; the CLI process is a
    // one-shot client that sends a request, prints the reply, exits.
    //
    // Pipe name is per-user so several users on the same box each get their own
    // instance and don't step on each other. CurrentUserOnly on both ends: the default
    // pipe ACL lets Everyone read, and the name (SID-based) is guessable, so another user
    // could otherwise create it first and answer our CLI calls.
    public static class CliRelay
    {
        // WELL-KNOWN NAME: keep stable — external scripts rely on the tray answering
        // here. Bumping this breaks any user's existing scheduled tasks.
        private static string PipeName =>
            "RcloneMount.Cli." + (WindowsIdentity.GetCurrent().User?.Value ?? "user");

        // Append only — the value goes over the wire as its number.
        public enum Command { List, Mount, Unmount, Show }

        public sealed record Request(Command Command, string? Name);

        // ExitCode mirrors what the CLI process should return: 0 success, 2 unknown
        // remote/mount, 3 mount timeout, 4 no resident, 5 other error, 6 unmount refused
        // because mount uploads are still pending. Kept small so
        // shell scripts can if-branch on it.
        public sealed record MountEntry(string RemoteName, string RemotePath, string DriveLetter, bool Mounted);
        public sealed record Response(int ExitCode, string Message, IReadOnlyList<MountEntry>? Mounts = null);

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        // Server side (hosted by MainForm). Keeps looping until stopToken fires; a
        // single-shot handler runs on the calling thread — the tray marshals to the
        // UI thread inside its own callback if it needs to touch controls.
        public sealed class Server : IDisposable
        {
            private readonly Func<Request, CancellationToken, Task<Response>> handler;
            private CancellationTokenSource? cts;
            private Task? loop;

            public Server(Func<Request, CancellationToken, Task<Response>> handler)
            {
                this.handler = handler;
            }

            public void Start()
            {
                if (loop is not null) return;
                cts = new CancellationTokenSource();
                loop = Task.Run(() => AcceptLoopAsync(cts.Token));
            }

            private async Task AcceptLoopAsync(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    NamedPipeServerStream? pipe = null;
                    try
                    {
                        // MaxAllowedServerInstances lets us accept the next client while
                        // handling the current one; PipeOptions.Asynchronous is required
                        // for cooperative cancellation.
                        pipe = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                        _ = HandleClientAsync(pipe, ct); // fire-and-forget; loop next accept
                        pipe = null;
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        // Address-in-use, IO failure, etc. Brief pause avoids hot-looping.
                        try { await Task.Delay(500, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                    finally
                    {
                        pipe?.Dispose();
                    }
                }
            }

            private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
            {
                using (pipe)
                {
                    try
                    {
                        using var reader = new StreamReader(pipe, leaveOpen: true);
                        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line is null) return;
                        Request req;
                        try
                        {
                            req = JsonSerializer.Deserialize<Request>(line, JsonOpts)
                                ?? throw new InvalidDataException("null request");
                        }
                        catch (Exception ex)
                        {
                            await WriteReplyAsync(writer, new Response(5, "Bad request: " + ex.Message));
                            return;
                        }

                        Response reply;
                        try
                        {
                            reply = await handler(req, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            reply = new Response(5, "Handler error: " + ex.Message);
                        }
                        await WriteReplyAsync(writer, reply);
                    }
                    catch { /* client hung up mid-conversation — ignore */ }
                }
            }

            private static async Task WriteReplyAsync(StreamWriter writer, Response r)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(r, JsonOpts));
            }

            public void Dispose()
            {
                try { cts?.Cancel(); } catch { }
                try { loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
                cts?.Dispose();
                cts = null;
                loop = null;
            }
        }

        // Client side: connect, send request, read reply. Short connect timeout so
        // scripts don't hang if the tray isn't running.
        public static async Task<Response> SendAsync(Request req, TimeSpan connectTimeout, CancellationToken ct = default)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new Response(4, "RcloneMount is not running.");
            }
            catch (Exception ex)
            {
                return new Response(4, "Could not connect: " + ex.Message);
            }

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(req, JsonOpts)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) return new Response(5, "No reply from tray.");
            try
            {
                return JsonSerializer.Deserialize<Response>(line, JsonOpts)
                    ?? new Response(5, "Empty reply from tray.");
            }
            catch (Exception ex)
            {
                return new Response(5, "Malformed reply: " + ex.Message);
            }
        }

        // Attach the current process to its parent console so Console.WriteLine
        // shows up in the shell that launched us. Windows GUI subsystem apps have
        // no console by default; AttachConsole opts in. No-op if there's no parent
        // console (double-clicked from Explorer) — output just falls in the void.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        public static void AttachParentConsole()
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        }
    }
}
