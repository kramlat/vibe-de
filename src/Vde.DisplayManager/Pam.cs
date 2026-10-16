using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Vde.DisplayManager;

// Authentication through PAM (the same system login(1), sudo and sshd use), so passwords, fingerprints, lockouts and
// account rules are whatever the administrator configured for the service `vdedm` in /etc/pam.d.
//
// PAM calls back into us from inside pam_authenticate to ask its questions, and the answers come from a person, so
// each login runs PAM on a thread of its own and the questions travel over a channel.
public sealed class PamAuthenticator : IAuthenticator
{
    private readonly string _service;
    private readonly string? _confDir;

    // confDir: read the service file from there instead of /etc/pam.d (for tests and for running without root).
    public PamAuthenticator(string service = "vdedm", string? confDir = null)
    {
        _service = service;
        _confDir = confDir;
    }

    public IAuthSession Begin(string username) => new PamSession(_service, _confDir, username);

    private sealed class PamSession : IAuthSession
    {
        private const int Success = 0, ConvErr = 19;
        private const int PromptEchoOff = 1, PromptEchoOn = 2, ErrorMsg = 3, TextInfo = 4;
        private const int PamTty = 3, EstablishCred = 0x2, DeleteCred = 0x4;

        [StructLayout(LayoutKind.Sequential)] private struct PamConv { public IntPtr Conv; public IntPtr AppData; }
        [StructLayout(LayoutKind.Sequential)] private struct PamMessage { public int Style; public IntPtr Msg; }
        [StructLayout(LayoutKind.Sequential)] private struct PamResponse { public IntPtr Resp; public int RetCode; }
        private delegate int ConvDelegate(int count, IntPtr messages, IntPtr responses, IntPtr appData);

        [DllImport("libpam.so.0")] private static extern int pam_start(string service, string? user, ref PamConv conv, out IntPtr handle);
        [DllImport("libpam.so.0")] private static extern int pam_start_confdir(string service, string? user, ref PamConv conv, string confDir, out IntPtr handle);
        [DllImport("libpam.so.0")] private static extern int pam_end(IntPtr handle, int status);
        [DllImport("libpam.so.0")] private static extern int pam_authenticate(IntPtr handle, int flags);
        [DllImport("libpam.so.0")] private static extern int pam_acct_mgmt(IntPtr handle, int flags);
        [DllImport("libpam.so.0")] private static extern int pam_setcred(IntPtr handle, int flags);
        [DllImport("libpam.so.0")] private static extern int pam_open_session(IntPtr handle, int flags);
        [DllImport("libpam.so.0")] private static extern int pam_close_session(IntPtr handle, int flags);
        [DllImport("libpam.so.0")] private static extern int pam_putenv(IntPtr handle, string nameValue);
        [DllImport("libpam.so.0")] private static extern int pam_set_item(IntPtr handle, int item, string value);
        [DllImport("libpam.so.0")] private static extern IntPtr pam_getenvlist(IntPtr handle);
        [DllImport("libpam.so.0")] private static extern IntPtr pam_strerror(IntPtr handle, int code);
        [DllImport("libc")] private static extern IntPtr calloc(UIntPtr count, UIntPtr size);
        [DllImport("libc")] private static extern IntPtr strdup(IntPtr s);
        [DllImport("libc")] private static extern void free(IntPtr p);

        private readonly Channel<AuthEvent> _events = Channel.CreateUnbounded<AuthEvent>();
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _answers = new();
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _commands = new();
        private readonly ConvDelegate _conv;   // kept alive: PAM holds a pointer to it
        private readonly Thread _thread;
        private IntPtr _handle;
        private volatile bool _cancelled;
        private bool _sessionOpen, _credsSet;
        private IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _opened = new();

        public PamSession(string service, string? confDir, string username)
        {
            _conv = Converse;
            _thread = new Thread(() => Run(service, confDir, username)) { IsBackground = true, Name = "pam " + username };
            _thread.Start();
        }

        public async Task<AuthEvent> NextAsync(CancellationToken cancel = default) => await _events.Reader.ReadAsync(cancel);

        public void Respond(string? answer) => _answers.Add(answer);

        public IReadOnlyDictionary<string, string> OpenSession(SessionInfo info)
        {
            var done = new TaskCompletionSource<IReadOnlyDictionary<string, string>>();
            _commands.Add(() =>
            {
                try { done.SetResult(DoOpenSession(info)); }
                catch (Exception e) { done.SetException(e); }
            });
            return done.Task.GetAwaiter().GetResult();
        }

        public void Close()
        {
            _cancelled = true;
            _answers.Add(null);          // unblocks a question that is waiting
            if (!_commands.IsAddingCompleted)
                try { _commands.Add(() => { }); _commands.CompleteAdding(); } catch (InvalidOperationException) { }
        }

        public void Dispose() => Close();

        // ---- on the PAM thread ----

        private void Run(string service, string? confDir, string username)
        {
            var conv = new PamConv { Conv = Marshal.GetFunctionPointerForDelegate(_conv), AppData = IntPtr.Zero };
            int rc;
            try
            {
                rc = confDir is null ? pam_start(service, username, ref conv, out _handle) : pam_start_confdir(service, username, ref conv, confDir, out _handle);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Fail("PAM is not available: " + e.Message, authError: false);
                return;
            }
            if (rc != Success) { Fail("cannot start PAM: " + Describe(rc), authError: false); return; }

            rc = pam_authenticate(_handle, 0);
            if (rc != Success) { Fail(Describe(rc), authError: true); End(rc); return; }
            rc = pam_acct_mgmt(_handle, 0);   // account valid? not expired, allowed to log in now
            if (rc != Success) { Fail(Describe(rc), authError: true); End(rc); return; }
            _events.Writer.TryWrite(new Authenticated());

            // The session (if the login goes on) is opened and closed on this thread: PAM wants one thread for both.
            foreach (var command in _commands.GetConsumingEnumerable())
                command();
            if (_sessionOpen) pam_close_session(_handle, 0);
            if (_credsSet) pam_setcred(_handle, DeleteCred);
            End(Success);
        }

        private IReadOnlyDictionary<string, string> DoOpenSession(SessionInfo info)
        {
            if (info.Tty is not null) pam_set_item(_handle, PamTty, info.Tty);
            foreach (var kv in new[] { $"XDG_SEAT={info.Seat}", $"XDG_VTNR={info.Vt}", $"XDG_SESSION_TYPE={info.Type}", $"XDG_SESSION_CLASS={info.Class}" }
                         .Concat(info.Desktop is null ? [] : [$"XDG_SESSION_DESKTOP={info.Desktop}", $"XDG_CURRENT_DESKTOP={info.Desktop}"]))
                pam_putenv(_handle, kv);   // pam_systemd reads these to register the session with logind
            var rc = pam_setcred(_handle, EstablishCred);
            if (rc != Success) throw new InvalidOperationException("cannot set credentials: " + Describe(rc));
            _credsSet = true;
            rc = pam_open_session(_handle, 0);
            if (rc != Success) throw new InvalidOperationException("cannot open the session: " + Describe(rc));
            _sessionOpen = true;

            var env = new Dictionary<string, string>();
            var list = pam_getenvlist(_handle);
            if (list != IntPtr.Zero)
            {
                for (var p = list; Marshal.ReadIntPtr(p) is var s && s != IntPtr.Zero; p += IntPtr.Size)
                {
                    var text = Marshal.PtrToStringUTF8(s) ?? "";
                    if (text.IndexOf('=') is > 0 and var eq) env[text[..eq]] = text[(eq + 1)..];
                    free(s);
                }
                free(list);
            }
            _environment = env;
            return env;
        }

        private int Converse(int count, IntPtr messages, IntPtr responses, IntPtr appData)
        {
            var array = calloc((UIntPtr)count, (UIntPtr)Marshal.SizeOf<PamResponse>());
            for (var i = 0; i < count; i++)
            {
                var message = Marshal.PtrToStructure<PamMessage>(Marshal.ReadIntPtr(messages, i * IntPtr.Size));
                var text = Marshal.PtrToStringUTF8(message.Msg) ?? "";
                AuthEvent ev = message.Style switch
                {
                    PromptEchoOff => new Prompt(true, text),
                    PromptEchoOn => new Prompt(false, text),
                    ErrorMsg => new Notice(true, text),
                    _ => new Notice(false, text),
                };
                _events.Writer.TryWrite(ev);
                var answer = _answers.Take();   // blocks until the greeter responds (or the attempt is cancelled)
                if (_cancelled) { FreeResponses(array, i); return ConvErr; }
                if (ev is Prompt)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes((answer ?? "") + "\0");
                    var ptr = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    var dup = strdup(ptr);   // PAM frees answers with free(), so they must come from malloc
                    Marshal.FreeHGlobal(ptr);
                    Marshal.WriteIntPtr(array, i * Marshal.SizeOf<PamResponse>(), dup);
                }
            }
            Marshal.WriteIntPtr(responses, array);
            return Success;
        }

        private static void FreeResponses(IntPtr array, int filled)
        {
            for (var i = 0; i < filled; i++)
            {
                var s = Marshal.ReadIntPtr(array, i * Marshal.SizeOf<PamResponse>());
                if (s != IntPtr.Zero) free(s);
            }
            free(array);
        }

        private void Fail(string reason, bool authError) => _events.Writer.TryWrite(new Failed(reason, authError));

        private string Describe(int rc) => Marshal.PtrToStringUTF8(pam_strerror(_handle, rc)) ?? $"PAM error {rc}";

        private void End(int status)
        {
            if (_handle != IntPtr.Zero) pam_end(_handle, status);
            _handle = IntPtr.Zero;
            _events.Writer.TryComplete();
        }
    }
}
