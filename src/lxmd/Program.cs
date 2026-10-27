using System.Runtime.InteropServices;
using Lxmd;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
return await LxmdApp.RunAsync(args, Console.Out, Console.Error, stop.Token).ConfigureAwait(false);
