using System.Runtime.InteropServices;

using var stop = new CancellationTokenSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });

return await VdeNotify.NotifyDaemon.RunAsync(args, Console.Out, Console.Error, stop.Token);
