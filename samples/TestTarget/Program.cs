using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace ObsidianTestTarget;

/// <summary>Deliberately full of metadata so a dump can be verified end to end.</summary>
public static class Program
{
    public const string Marker = "OBSIDIAN-TEST-TARGET-7F3A";

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"target pid={Environment.ProcessId} runtime={Environment.Version}");
        Console.WriteLine(LicenseEngine.Describe());
        Console.WriteLine(new SecretVault().Unlock("hunter2"));

        // A dynamic assembly only exists in memory: the out-of-process dump
        // engine cannot see it, the managed agent can enumerate it.
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ObsidianDynamicPayload"), AssemblyBuilderAccess.Run);
        var module = dynamicAssembly.DefineDynamicModule("ObsidianDynamicPayload.dll");
        var type = module.DefineType("Dynamic.OnlyInMemory", TypeAttributes.Public);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.CreateType();

        Console.WriteLine($"dynamic assembly: {dynamicAssembly.FullName}");
        Console.Out.Flush();

        int seconds = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 600;
        seconds = Math.Clamp(seconds, 1, 3600);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    /// <summary>Deterministic control-flow shapes for the IL formatter tests.</summary>
    public static string SwitchProbe(int value) => value switch
    {
        0 => "zero",
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        _ => "many",
    };

    /// <summary>Guarantees an exception handler (try/catch/finally) in the IL.</summary>
    public static int TryCatchProbe(string? text)
    {
        int length = 0;
        try
        {
            length = text!.Length;
        }
        catch (NullReferenceException)
        {
            length = -1;
        }
        finally
        {
            length += 1;
        }
        return length;
    }
}

public sealed class SecretVault
{
    private readonly string _salt = "s3cr3t-s4lt-value";

    public string Unlock(string password)
    {
        if (password.Length < 4)
            throw new ArgumentException("password too short", nameof(password));

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(_salt + password)));
    }

    /// <summary>Nested type: the inspector must inherit the outer namespace.</summary>
    public sealed class SaltState
    {
        public int Rounds { get; private set; }

        public void Advance() => Rounds++;
    }
}

public static class LicenseEngine
{
    private static readonly string[] Features = ["alpha", "beta", "gamma", "delta"];

    public static string Describe() => string.Join(", ", Features.Select(f => f.ToUpperInvariant()));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Validate(string key) => key.Length == 32 && key.All(char.IsLetterOrDigit);
}

public interface IPlugin
{
    string Name { get; }
    void Execute();
}

public abstract class PluginBase : IPlugin
{
    public abstract string Name { get; }
    public virtual void Execute() => Console.WriteLine($"plugin {Name}");
}
