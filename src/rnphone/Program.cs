using System.Runtime.InteropServices;
using Rnphone;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; stop.Cancel(); });
return await PhoneApp.RunAsync(args, Console.In, Console.Out, Console.Error, stop.Token);
