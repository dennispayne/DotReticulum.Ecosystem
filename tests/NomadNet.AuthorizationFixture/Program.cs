using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
if (args is ["--child"])
{
    File.WriteAllText(executable + ".childpid", Environment.ProcessId.ToString());
    Thread.Sleep(TimeSpan.FromSeconds(60));
    return 0;
}
var input = File.Exists(executable + ".input") ? File.ReadAllLines(executable + ".input") : [];
var mode = Environment.GetEnvironmentVariable("var_mode") ?? input.ElementAtOrDefault(0) ?? "allow";
var identity = Environment.GetEnvironmentVariable("var_allow") ?? input.ElementAtOrDefault(1) ?? new string('a', 32);
File.WriteAllText(executable + ".pid", Environment.ProcessId.ToString());
switch (mode)
{
    case "environment":
        if (Environment.GetEnvironmentVariable("remote_identity") != identity ||
            Environment.GetEnvironmentVariable("link_id") != new string('0', 32) ||
            Environment.GetEnvironmentVariable("field_name") != "Alice" ||
            Environment.GetEnvironmentVariable("var_value") != "bounded" ||
            Environment.GetEnvironmentVariable("AUTHORIZATION_MUST_NOT_LEAK") is not null ||
            Environment.GetEnvironmentVariable("NOT_ALLOWED") is not null ||
            Environment.GetEnvironmentVariable("var_bad-name") is not null ||
            Environment.GetEnvironmentVariable("var_oversize") is not null ||
            Environment.GetEnvironmentVariable("var_nul") is not null ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH")) ||
            (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SystemRoot"))))
            return 9;
        Console.WriteLine(identity);
        break;
    case "allow":
        Console.WriteLine(identity);
        break;
    case "mixed":
        Console.WriteLine("malformed\n" + identity.ToUpperInvariant() + "\n" + new string('z', 32));
        break;
    case "cr":
        Console.Write("malformed\r" + identity + "\r" + new string('z', 32));
        break;
    case "malformed":
        Console.WriteLine(new string('z', 32));
        break;
    case "overflow":
        Console.Write(new string('x', 65537));
        break;
    case "boundary":
        Console.Write(new string('x', 65503) + "\n" + identity);
        break;
    case "stderr":
        for (var index = 0; index < 512; index++) Console.Error.Write(new string('x', 4096));
        Console.WriteLine(identity);
        break;
    case "nonzero":
        Console.WriteLine(identity);
        return 7;
    case "sleep":
        Thread.Sleep(TimeSpan.FromSeconds(60));
        break;
    case "descendant":
    case "orphan":
        using (var child = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            ArgumentList = { "--child" },
        }) ?? throw new IOException("Child did not start."))
        {
            while (!File.Exists(executable + ".childpid")) Thread.Sleep(10);
            Console.WriteLine(identity);
            if (mode == "descendant") Thread.Sleep(TimeSpan.FromSeconds(60));
        }
        break;
    default:
        return 8;
}
return 0;
